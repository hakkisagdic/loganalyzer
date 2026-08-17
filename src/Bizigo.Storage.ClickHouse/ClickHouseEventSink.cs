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
    private readonly EventWriter _writer;
    private readonly EventNormalizer _normalizer;
    private readonly EventSinkOptions _options;
    private readonly ILogger<ClickHouseEventSink> _logger;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<LogEvent> _buffer = [];
    private DateTimeOffset _lastFlush;

    private long _written;
    private long _dropped;
    private int _disposed;

    public ClickHouseEventSink(
        EventWriter writer,
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

    public async ValueTask HandleAsync(
        IReadOnlyList<ParsedEvent> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            foreach (var parsed in batch)
            {
                _buffer.Add(_normalizer.Normalize(parsed));
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
            Interlocked.Add(ref _written, result.RowsWritten);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
