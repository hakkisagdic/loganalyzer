using System.Buffers;
using System.Threading.Channels;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Pipeline;

public enum IngestOutcome
{
    /// <summary>Ham veri WAL'da ve fsync'lendi — 200.</summary>
    Accepted = 1,

    /// <summary>Gövde çözülemedi — 400. Yeniden denemek işe yaramaz.</summary>
    Invalid = 2,

    /// <summary>WAL dolu — 503 + Retry-After. Collector yeniden dener.</summary>
    Full = 3,
}

public sealed record IngestResult(IngestOutcome Outcome, int RecordCount, string? Message = null, int RetryAfterSeconds = 0);

/// <summary>
/// Verinin içeri girdiği <b>tek kapı</b> (F1 §2.3).
///
/// <code>
/// istek → çöz → ham batch'i WAL'a yaz + fsync → 200 OK
///                                  ↓ (asenkron)
///                        boru hattı → parse → ClickHouse
///                        yükleyici → ham arşiv
/// </code>
///
/// <para>
/// Sıralama pazarlık konusu değil: ack <b>WAL'dan sonra</b>, işleme <b>ack'ten
/// sonra</b>. Tersi olsaydı parse hatası ya da ClickHouse kesintisi veri kaybına
/// dönerdi; ham arşivin varlık sebebi tam olarak bu.
/// </para>
/// </summary>
public sealed class IngestGateway
{
    private readonly OtlpLogsDecoder _decoder;
    private readonly WriteAheadLog _wal;
    private readonly ChannelWriter<IReadOnlyList<RawRecord>> _writer;
    private readonly IngestStats _stats;
    private readonly WalOptions _walOptions;
    private readonly ILogger<IngestGateway> _logger;
    private readonly TimeProvider _time;

    public IngestGateway(
        OtlpLogsDecoder decoder,
        WriteAheadLog wal,
        IngestChannel channel,
        IngestStats stats,
        IOptions<WalOptions> walOptions,
        ILogger<IngestGateway> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(walOptions);

        _decoder = decoder;
        _wal = wal;
        _writer = channel.Writer;
        _stats = stats;
        _walOptions = walOptions.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IngestResult> AcceptAsync(
        ReadOnlyMemory<byte> payload,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var receivedAt = _time.GetUtcNow();

        IReadOnlyList<RawRecord> records;
        try
        {
            records = _decoder.Decode(payload, contentType, receivedAt);
        }
        catch (OtlpDecodeException ex)
        {
            _stats.RejectInvalid();
            _logger.LogWarning(ex, "OTLP gövdesi reddedildi.");
            return new IngestResult(IngestOutcome.Invalid, 0, ex.Message);
        }

        if (records.Count == 0)
        {
            // Boş export geçerlidir (collector sağlık yoklaması) — WAL'a yazılmaz.
            return new IngestResult(IngestOutcome.Accepted, 0);
        }

        var batch = Serialize(records);

        try
        {
            await _wal.AppendAsync(batch, cancellationToken);
        }
        catch (WalFullException ex)
        {
            _stats.RejectFull();
            _logger.LogError(
                "WAL dolu ({Used}/{Limit} bayt) — istek 503 ile reddedildi.",
                ex.TotalBytes,
                ex.LimitBytes);

            return new IngestResult(
                IngestOutcome.Full,
                0,
                "WAL kapasitesi doldu.",
                _walOptions.RetryAfterSeconds);
        }

        // Buradan sonrası artık dayanıklı. Kanal doluysa BEKLİYORUZ: sessizce
        // düşürmek, veriyi yalnızca WAL'da bırakıp işlenmemiş göstermek olurdu.
        // Bekleme geriye doğru yayılır ve sonunda WAL dolarak 503'e döner —
        // backpressure zinciri budur.
        await _writer.WriteAsync(records, cancellationToken);

        _stats.Accepted(records.Count);
        return new IngestResult(IngestOutcome.Accepted, records.Count);
    }

    /// <summary>
    /// Batch'i NDJSON'a çevirir. Format ham arşivinkiyle <b>aynı</b> — yükleyici
    /// dönüştürmez, kopyalar (F1 §7.1).
    /// </summary>
    private static ReadOnlyMemory<byte> Serialize(IReadOnlyList<RawRecord> records)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

        foreach (var record in records)
        {
            RawRecordCodec.Write(buffer, record);
            buffer.Write("\n"u8);
        }

        return buffer.WrittenMemory;
    }
}
