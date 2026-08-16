using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.Raw;

/// <summary>
/// Yükleyiciyi ve scrub'ı periyodik koşturur.
///
/// <para>
/// Her tur kendi hatasını yutuyor: arşiv katmanının çökmesi ingest'i
/// durdurmamalı. Dayanıklılık sınırı WAL (F1 §2.3) — arşiv geride kalırsa
/// segmentler birikir, veri kaybolmaz.
/// </para>
/// </summary>
public sealed class RawArchiveService(
    RawArchiveUploader uploader,
    RawArchiveScrubber scrubber,
    IRawObjectStore store,
    IOptions<RawStoreOptions> options,
    ILogger<RawArchiveService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly RawStoreOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.EnsureBucketAsync(stoppingToken);

        var uploadLoop = RunLoopAsync(
            "yükleyici",
            _options.UploadInterval,
            async ct =>
            {
                var report = await uploader.RunOnceAsync(ct);
                if (report.ObjectsWritten > 0 || report.SegmentsDeleted > 0)
                {
                    logger.LogInformation(
                        "Ham arşiv: {Segments} segment işlendi, {Objects} nesne yazıldı, {Deleted} segment silindi.",
                        report.SegmentsProcessed,
                        report.ObjectsWritten,
                        report.SegmentsDeleted);
                }
            },
            stoppingToken);

        var scrubLoop = RunLoopAsync(
            "scrub",
            _options.ScrubInterval,
            async ct =>
            {
                var report = await scrubber.RunOnceAsync(ct);
                if (report.Mismatched > 0 || report.MissingObjects > 0)
                {
                    logger.LogError(
                        "Scrub sorun buldu: {Checked} nesne denetlendi, {Mismatched} bozuk, {Missing} kayıp.",
                        report.Checked,
                        report.Mismatched,
                        report.MissingObjects);
                }
            },
            stoppingToken);

        await Task.WhenAll(uploadLoop, scrubLoop);
    }

    private async Task RunLoopAsync(
        string name,
        TimeSpan interval,
        Func<CancellationToken, Task> work,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await work(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Yutuluyor ama SESSİZ değil: bir sonraki turda yeniden denenir.
                logger.LogError(ex, "Ham arşiv {Loop} turu başarısız; bir sonraki turda yeniden denenecek.", name);
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
