using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Ack ile yanıt arasındaki pencere.</b>
///
/// <para>
/// <c>IngestGateway.AcceptAsync</c> sırası şu: gövdeyi çöz → WAL'a yaz +
/// <c>fsync</c> → <b>sınırlı kanala yaz</b> → 200 dön. Üçüncü adım, kanal
/// doluyken <b>bekliyor</b> — bilerek: düşürmek sessiz veri kaybı olurdu
/// (<c>IngestChannel</c>).
/// </para>
///
/// <para>
/// Sonucu, kimsenin karar verdiği kayıtta olmayan bir durum: veri <b>dayanıklı</b>
/// olduktan sonra, istemci hâlâ yanıt beklemiş oluyor. O pencerede istemci zaman
/// aşımına düşer ve yeniden gönderirse aynı batch WAL'a <b>ikinci kez</b>
/// yazılıyor — <c>AppendAsync</c>'te tekilleştirme anahtarı yok.
/// </para>
///
/// <para>
/// <b>Bu testler bir çözüm değil, bir ölçüm.</b> Pencerenin var olduğunu ve
/// yeniden gönderimin ne ürettiğini sabitliyorlar. Tekilleştirme tasarlandığında
/// buradaki ikinci test kırmızıya döner ve dönmesi <b>istenen</b> şeydir; o gün
/// beklentiyi değiştiren kişi neyin değiştiğini burada okur.
/// </para>
///
/// <para>
/// <b>Duvar saati (§6):</b> testin ölçtüğü şey sıralama, süre değil. Aşağıdaki
/// bekleme yalnızca <b>askıda kalma bekçisi</b> — koşul sağlandığı anda dönüyor,
/// süre sınırı iki kat büyüklük fazlasıyla geniş ve yüklü makinede de aynı
/// sonucu veriyor. Testin geçme sebebi hiçbir zaman "yeterince hızlıydı" değil.
/// </para>
/// </summary>
public sealed class IngestRetryWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "bizigo-window-" + Guid.NewGuid().ToString("N"));

    private WriteAheadLog? _wal;
    private IngestChannel? _channel;

    /// <summary>
    /// İstemcinin zaman aşımını temsil eden iptal kaynağı. <b>Alan olarak
    /// duruyor çünkü test düşerse de iptal edilmesi gerekiyor:</b> dolu kanalda
    /// bekleyen bir <c>AcceptAsync</c> iptal edilmezse hiç bitmiyor ve test
    /// koşucusu çıkışta asılı kalıyor. Ölçüldü — bekçinin kırmızısını ölçerken
    /// koşum kendisi askıda kaldı (§3: başlattığın her şeyi topla).
    /// </summary>
    private readonly CancellationTokenSource _abandon = new();

    /// <summary>Kanal kapasitesi 1: ikinci batch kaçınılmaz olarak bekliyor.</summary>
    private IngestGateway Build()
    {
        var walOptions = new WalOptions { Directory = _directory };

        _wal = new WriteAheadLog(Options.Create(walOptions), NullLogger<WriteAheadLog>.Instance);
        _channel = new IngestChannel(Options.Create(new IngestOptions { ChannelCapacity = 1 }));

        return new IngestGateway(
            new OtlpLogsDecoder(),
            _wal,
            _channel,
            new IngestStats(),
            Options.Create(walOptions),
            NullLogger<IngestGateway>.Instance);
    }

    private static byte[] Request(string body) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            resourceLogs = new[]
            {
                new
                {
                    scopeLogs = new[]
                    {
                        new { logRecords = new[] { new { body = new { stringValue = body } } } },
                    },
                },
            },
        });

    private RawRecord[] WalRecords() =>
        [.. Directory
            .GetFiles(_directory)
            .SelectMany(WriteAheadLog.ReadFrames)
            .SelectMany(frame => Encoding.UTF8
                .GetString(frame.Span)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(line => RawRecordCodec.Read(Encoding.UTF8.GetBytes(line)))];

    /// <summary>
    /// Askıda kalma bekçisi — ölçüm değil. Koşul sağlanınca hemen dönüyor.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            await Task.Delay(5, deadline.Token);
        }
    }

    /// <summary>
    /// <b>Pencere var.</b> İkinci batch WAL'da ve <c>fsync</c>'lenmiş, ama
    /// <c>AcceptAsync</c> hâlâ dönmemiş — yani istemcinin elinde 200 yok.
    /// </summary>
    [Fact]
    public async Task Veri_dayanikli_oldugu_halde_yanit_henuz_donmemis_olabiliyor()
    {
        var gateway = Build();
        var cancellation = TestContext.Current.CancellationToken;

        // Kanalı doldur: bu batch kanala giriyor ve yanıt dönüyor.
        var first = await gateway.AcceptAsync(Request("ilk"), OtlpLogsDecoder.JsonContentType, cancellation);
        Assert.Equal(IngestOutcome.Accepted, first.Outcome);

        // İkincisi: WAL'a yazılacak, sonra dolu kanalda bekleyecek.
        var second = gateway.AcceptAsync(Request("ikinci"), OtlpLogsDecoder.JsonContentType, _abandon.Token);

        await WaitUntilAsync(() => WalRecords().Length == 2, cancellation);

        // Ölçülen iddia: veri diskte, yanıt yolda değil.
        Assert.False(second.IsCompleted);
        Assert.Equal("ikinci", Encoding.UTF8.GetString(WalRecords()[1].Body.Span));

        // İstemcinin zaman aşımı bu noktada düşüyor.
        await _abandon.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        // Vazgeçilen istek WAL'ı geri almıyor — geri almak da yanlış olurdu:
        // veri gerçekten dayanıklı ve boru hattına girmesi gerekiyor.
        Assert.Equal(2, WalRecords().Length);
    }

    /// <summary>
    /// <b>Yeniden gönderim ikinci kez yazıyor</b> — ve iki kaydı birbirine
    /// bağlayan hiçbir şey yok.
    ///
    /// <para>
    /// <c>EventId</c> her çözümlemede yeniden üretiliyor
    /// (<c>Guid.CreateVersion7</c>), yani aynı batch iki farklı olay kimliğiyle
    /// duruyor. Aşağı akıştaki hiçbir bileşen — arşiv, ClickHouse, replay — bu
    /// ikisinin aynı gönderim olduğunu <b>söyleyemez</b>. Tek ortak nokta gövde
    /// baytları.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Ayni_batch_yeniden_gonderilince_WAL_a_ikinci_kez_yaziliyor()
    {
        var gateway = Build();
        var cancellation = TestContext.Current.CancellationToken;
        var payload = Request("aynı gövde");

        await gateway.AcceptAsync(Request("ilk"), OtlpLogsDecoder.JsonContentType, cancellation);

        var abandoned = gateway.AcceptAsync(payload, OtlpLogsDecoder.JsonContentType, _abandon.Token);

        await WaitUntilAsync(() => WalRecords().Length == 2, cancellation);
        await _abandon.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        // Boru hattı ilerliyor, kanal boşalıyor — istemci yeniden gönderiyor.
        await _channel!.Reader.ReadAsync(cancellation);
        var retry = await gateway.AcceptAsync(payload, OtlpLogsDecoder.JsonContentType, cancellation);

        Assert.Equal(IngestOutcome.Accepted, retry.Outcome);

        var records = WalRecords();
        Assert.Equal(3, records.Length);

        var abandonedRecord = records[1];
        var retriedRecord = records[2];

        // Gövde aynı...
        Assert.Equal(
            Encoding.UTF8.GetString(abandonedRecord.Body.Span),
            Encoding.UTF8.GetString(retriedRecord.Body.Span));

        // ...ama kimlik farklı: tekilleştirme anahtarı yok.
        Assert.NotEqual(abandonedRecord.EventId, retriedRecord.EventId);
    }

    public void Dispose()
    {
        // Askıda bekleyen AcceptAsync varsa (test düştüyse) burada bırakılıyor.
        _abandon.Cancel();
        _abandon.Dispose();
        _channel?.Complete();
        _wal?.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
