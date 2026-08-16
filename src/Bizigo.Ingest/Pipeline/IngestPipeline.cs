using Bizigo.Contracts;
using Bizigo.Ingest.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Kanaldan okuyan işçiler: kodlama tespiti (K4) + sink çağrısı.
///
/// <para>
/// T03'te sink geçiştir; parse (T05) ve dispatcher (T06) buraya takılır.
/// Kodlama tespitinin <b>burada</b> olması bilinçli: parse'tan önce metin
/// gerekiyor ama ham baytlar zaten WAL'a yazılmış durumda, yani yanlış tespit
/// geri alınabilir bir hata.
/// </para>
/// </summary>
public sealed class IngestPipeline : BackgroundService
{
    private readonly IngestChannel _channel;
    private readonly IIngestSink _sink;
    private readonly EncodingDetector _detector;
    private readonly IngestStats _stats;
    private readonly IngestOptions _options;
    private readonly ILogger<IngestPipeline> _logger;

    public IngestPipeline(
        IngestChannel channel,
        IIngestSink sink,
        EncodingDetector detector,
        IngestStats stats,
        IOptions<IngestOptions> options,
        ILogger<IngestPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = channel;
        _sink = sink;
        _detector = detector;
        _stats = stats;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable
            .Range(0, _options.EffectiveWorkerCount)
            .Select(index => RunWorkerAsync(index, stoppingToken))
            .ToArray();

        _logger.LogInformation("Ingest boru hattı {Workers} işçiyle başladı.", workers.Length);
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(int index, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var batch in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                var decoded = new List<DecodedRecord>(batch.Count);

                foreach (var record in batch)
                {
                    var body = _detector.Decode(
                        record.Body.Span,
                        record.EncodingDeclared,
                        _options.DefaultFallbackEncoding);

                    _stats.Processed(body.Name, body.WasDeclaredHonored);
                    decoded.Add(new DecodedRecord(record, body));
                }

                await _sink.HandleAsync(decoded, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal kapanış.
        }
        catch (Exception ex)
        {
            // Bir işçinin ölmesi sessiz kapasite kaybıdır — görünür olmalı.
            _logger.LogError(ex, "Ingest işçisi {Index} beklenmedik biçimde durdu.", index);
            throw;
        }
    }
}

/// <summary>
/// T03'ün geçiş sink'i: kaydı sayar, ilerlemeyi görünür kılar. T06 devralır.
/// </summary>
public sealed class PassthroughSink(ILogger<PassthroughSink> logger) : IIngestSink
{
    public ValueTask HandleAsync(IReadOnlyList<DecodedRecord> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (logger.IsEnabled(LogLevel.Debug) && batch.Count > 0)
        {
            var first = batch[0];
            logger.LogDebug(
                "Parse öncesi geçiş: {Count} kayıt, ilk kaynak {Source}, kodlama {Encoding}.",
                batch.Count,
                first.Raw.SourceKey,
                first.Decoded.Name);
        }

        return ValueTask.CompletedTask;
    }
}
