using Bizigo.Contracts;
using Bizigo.Normalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.ClickHouse;

public sealed class EventSinkOptions
{
    public const string SectionName = "EventSink";

    /// <summary>Bu satır sayısına ulaşınca yazılır (F1 §4.3).</summary>
    public int BatchRows { get; set; } = 10_000;

    /// <summary>Bu süre dolunca kısmi batch de yazılır.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>events</c> tablosunun saklama penceresi.
    ///
    /// <para>
    /// <b>Bu sayı tablonun TTL'i ile AYNI olmak zorunda</b>
    /// (<c>db/clickhouse/0001_events.sql</c>: <c>TTL toDateTime(ts) + INTERVAL 90 DAY</c>).
    /// Ayrışırlarsa sink ya olmayan bir kaybı raporlar ya da gerçek kaybı
    /// göremez — ikisi de sayacın kendisini işe yaramaz kılar. Ayrışmayı
    /// <c>EventRetentionTests</c> DDL'i okuyarak sınıyor.
    /// </para>
    ///
    /// <para>
    /// Burada duruyor çünkü sink'in bilmesi gereken şey retention politikası
    /// değil, <b>hangi satırın yazıldıktan sonra yok olacağı</b>.
    /// </para>
    /// </summary>
    public TimeSpan EventRetention { get; set; } = TimeSpan.FromDays(90);
}

/// <summary>
/// Normalize edilmiş olayları ClickHouse'a toplu yazar (F1 §4.3).
///
/// <para>
/// <b>Tek yazar, biriktirmeli.</b> ClickHouse'a satır satır INSERT bu hacimde
/// çalışmaz; her INSERT bir part oluşturur ve birleştirme (merge) yükü sistemi
/// boğar. 10 000 satır ya da 2 saniye — hangisi önce gelirse.
/// </para>
///
/// <para>
/// Yazma başarısız olursa <b>yutulmuyor ama ingest de durdurulmuyor</b>: veri
/// zaten WAL'da ve ham arşivde duruyor (F1 §2.3), yani en kötü durum "ClickHouse
/// geride kaldı"dır, "veri gitti" değil. Kayıp sayacı sağlık ucunda görünüyor.
/// </para>
/// </summary>
public sealed class ClickHouseEventSink : IParsedEventSink, IAsyncDisposable
{
    private readonly IEventWriter _writer;
    private readonly EventNormalizer _normalizer;
    private readonly EventSinkOptions _options;
    private readonly ILogger<ClickHouseEventSink> _logger;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<LogEvent> _buffer = [];
    private DateTimeOffset _lastFlush;

    private long _written;
    private long _dropped;
    private long _expired;
    private int _disposed;

    // Tampondaki satırların kaçı pencere dışında ve en eskisi hangisi. Yazım
    // sonucundan düşülecekleri için tampondan AYRI tutuluyorlar.
    private int _expiredInBuffer;
    private DateTimeOffset? _oldestExpired;

    public ClickHouseEventSink(
        IEventWriter writer,
        EventNormalizer normalizer,
        IOptions<EventSinkOptions> options,
        ILogger<ClickHouseEventSink> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _writer = writer;
        _normalizer = normalizer;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _lastFlush = _time.GetUtcNow();
    }

    public long Written => Interlocked.Read(ref _written);

    /// <summary>ClickHouse'a yazılamamış satırlar. Sıfırdan büyükse replay gerekir.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// <b>Yazıldı denip yok olan satırlar (S02a).</b>
    ///
    /// <para>
    /// Zaman damgası saklama penceresinin dışında kalan bir satır TTL ile
    /// siliniyor, ama istemciye <b>yazdım deniyor</b>: ölçüldü, tek bir INSERT
    /// içindeki 2020 ve 2026 tarihli iki satırdan yalnızca ikincisi tabloya
    /// girdi, dönen <c>written_rows</c> ise ikisini de saydı.
    /// </para>
    ///
    /// <para>
    /// <b>Silmenin ANI garanti değil</b> ve bu ayrım ölçülerek öğrenildi: aynı
    /// beş satırlık koşum yerel sunucuda insert anında ayıklandı, CI'nın taze
    /// sunucusunda ayıklanmadı ve satırlar bir sonraki birleştirmeye kadar
    /// tabloda durdu. Yani sayaç "şu anda tabloda kaç satır var" demiyor —
    /// <b>"TTL çalıştıktan sonra kaçı kalacak"</b> diyor. Kalıcı olan iddia bu,
    /// ve raporlanması gereken de bu: anlık sayı bir süre sonra yalan oluyor.
    /// </para>
    ///
    /// <para>
    /// Bu sayaç olmadan kayıp hiçbir yerde görünmüyordu: 100 satır basıldığında
    /// <c>accepted</c> 100, <c>processed</c> 100, INSERT başarılı, tabloda sıfır
    /// satır ve tek bir hata kaydı yok. Sahadaki tetikleyicisi test verisi değil
    /// — <b>saati yanlış bir cihaz</b>, retention'dan eski bir arşivin replay'i,
    /// ya da <c>ts</c> alanını yanlış seçen bir parser. Sonuncusunda parser
    /// hatası görünür yanlış değer yerine görünmez veri kaybına dönüşüyor.
    /// </para>
    ///
    /// <para>
    /// Satır yine de gönderiliyor: ham arşiv duruyor, yani parser ya da saat
    /// düzeldikten sonra K12 replay ile geri kazanılabilir bir kayıp bu.
    /// </para>
    /// </summary>
    public long Expired => Interlocked.Read(ref _expired);

    /// <summary>
    /// Henüz yazılmamış, tamponda bekleyen satırlar.
    ///
    /// <para>
    /// Kayıp farkı okunurken bilinmek zorunda: yoksa her ölçüm boşaltma aralığı
    /// kadar yanlış görünür ve sağlıklı bir boru hattı kayıp veriyor sanılır.
    /// </para>
    /// </summary>
    public int Buffered
    {
        get
        {
            _lock.Wait();
            try
            {
                return _buffer.Count;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public async ValueTask HandleAsync(
        IReadOnlyList<ParsedEvent> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Kesim NOKTASI batch başına bir kez alınıyor: satır başına saat
            // okumak aynı batch'in satırlarını farklı pencerelerde
            // değerlendirebilirdi ve testin geçme sebebi duvar saatine bağlanırdı.
            var cutoff = _time.GetUtcNow() - _options.EventRetention;

            foreach (var parsed in batch)
            {
                var normalized = _normalizer.Normalize(parsed);

                if (normalized.Timestamp < cutoff)
                {
                    _expiredInBuffer++;

                    if (_oldestExpired is null || normalized.Timestamp < _oldestExpired)
                    {
                        _oldestExpired = normalized.Timestamp;
                    }
                }

                _buffer.Add(normalized);
            }

            if (_buffer.Count >= _options.BatchRows
                || _time.GetUtcNow() - _lastFlush >= _options.FlushInterval)
            {
                await FlushLockedAsync(cancellationToken);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Zamanlayıcının çağırdığı boşaltma — kısmi batch'ler beklemesin.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await FlushLockedAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task FlushLockedAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            _lastFlush = _time.GetUtcNow();
            return;
        }

        try
        {
            var result = await _writer.WriteEventsAsync(_buffer, cancellationToken);

            // `RowsWritten` GÖNDERİLENİ sayıyor, KALICI OLANI değil: pencere
            // dışındaki satırlar TTL ile siliniyor (insert anında ya da bir
            // sonraki birleştirmede) ama dönen sayıya dahil ediliyor.
            // Düşülmezse `Written` yarın var olmayacak satırları bugün sayar —
            // ve düzeltilmek istenen hata tam olarak buydu.
            Interlocked.Add(ref _written, result.RowsWritten - _expiredInBuffer);

            if (_expiredInBuffer > 0)
            {
                Interlocked.Add(ref _expired, _expiredInBuffer);

                // Boşaltma başına TEK kayıt: satır başına loglamak gerçek bir
                // arıza sırasında (saati 2020'de kalmış bir cihaz) log'u
                // boğardı ve uyarı kendi gürültüsünde kaybolurdu.
                _logger.LogWarning(
                    "{Rows} satır saklama penceresinin ({Days} gün) dışında; ClickHouse " +
                    "yazıldı dese de tabloya girmiyorlar. En eskisi {Oldest:u}. " +
                    "Muhtemel sebep: cihaz saati yanlış ya da parser yanlış alanı ts sanıyor. " +
                    "Ham arşiv duruyor, replay ile geri kazanılabilir.",
                    _expiredInBuffer,
                    _options.EventRetention.TotalDays,
                    _oldestExpired);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Yazım hiç olmadı: pencere dışındakiler de ClickHouse'a ulaşmadı,
            // yani burada `expired` değil `dropped` sayılıyorlar. İkisini birden
            // saymak aynı satırı iki kez kaybetmiş göstermek olurdu.
            Interlocked.Add(ref _dropped, _buffer.Count);

            // Yutuluyor ama sessiz değil. Veri WAL'da ve ham arşivde duruyor;
            // buradaki kayıp replay ile kapatılabilir bir kayıp.
            _logger.LogError(
                ex,
                "ClickHouse'a {Rows} satır yazılamadı; veri ham arşivde duruyor, replay gerekebilir.",
                _buffer.Count);
        }
        finally
        {
            _buffer.Clear();
            _expiredInBuffer = 0;
            _oldestExpired = null;
            _lastFlush = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Kapanışta elde kalan satırlar yazılır; yoksa son 2 saniyelik veri
    /// gereksiz yere replay işi olurdu.
    ///
    /// <para>
    /// <b>Tekrar çağrılabilir ve atmaz — bilinçli.</b> Konteyner başlatılamadığında
    /// DI kapsamı yine de atılıyor ve burası ikinci kez çalışabiliyor; atılmış
    /// semaforda beklemek <see cref="ObjectDisposedException"/> fırlatıyordu ve o
    /// istisna, açılışın <b>gerçek</b> hatasının yerine geçip süreci
    /// düşürüyordu. Gözlenen hâli: "Maskeleme sözlüğü bulunamadı" mesajı log'da
    /// duruyor ama süreç 134 ile ve tamamen ilgisiz bir yığın iziyle ölüyordu.
    /// Kapanış yolu, açılış hatasını gizleyebilecek hiçbir şey yapmamalı.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Kilit zaten atılmış; boşaltacak bir şey de yok.
        }

        _lock.Dispose();
    }
}
