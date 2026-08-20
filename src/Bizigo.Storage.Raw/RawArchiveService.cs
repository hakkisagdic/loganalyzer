using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
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
    IDbContextFactory<ControlPlaneDbContext> factory,
    IOptions<RawStoreOptions> options,
    ILogger<RawArchiveService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly RawStoreOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.EnsureBucketAsync(stoppingToken);
        await WarnIfSweepOutrunsRetentionAsync(stoppingToken);

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

                // Kurtarma AYNI TURDA, ayrı bir zamanlamada değil (T40).
                //
                // Bağlayıcı kısıt saklama penceresi: kurtarma kendi takvimiyle
                // koşsaydı tespit ile kurtarma arasına ikinci bir gecikme girer
                // ve 48 saatlik bütçe iki bağımsız periyot arasında bölünürdü.
                // Burada pencerenin tamamı tespite kalıyor ve bütçeyi tek bir
                // sayı belirliyor: tam tarama süresi.
                var recovery = await uploader.RecoverAsync(ct);
                if (recovery.Attempted > 0)
                {
                    logger.LogWarning(
                        "Kurtarma: {Attempted} nesne denendi, {Recovered} geri yüklendi, " +
                        "{Unrecoverable} kurtarılamaz işaretlendi.",
                        recovery.Attempted,
                        recovery.Recovered,
                        recovery.Unrecoverable);
                }
            },
            stoppingToken);

        await Task.WhenAll(uploadLoop, scrubLoop);
    }

    /// <summary>
    /// Tam tarama süresi ile kurtarma penceresini karşılaştırır (T40).
    ///
    /// <para>
    /// <b>Neden bir bekçi gerekiyordu:</b> üç sayı birbirinden habersiz
    /// seçilmişti. Scrub 6 saatte 20 nesne tarıyor, saklama penceresi 48 saat —
    /// yani pencere içinde ancak 160 nesneye bakılabiliyor. Arşiv bundan
    /// büyükse kayıp, kurtarma kaynağı silindikten <b>sonra</b> fark ediliyor
    /// ve koruma, kodu yazılmış olsa bile <b>aritmetik olarak erişilemez</b>
    /// hale geliyor.
    /// </para>
    ///
    /// <para>
    /// Bekçi sayıyı değil <b>ilişkiyi</b> sınıyor: doğru <c>ScrubSampleSize</c>
    /// gerçek arşiv boyutuna bağlı ve onu buradan bilemeyiz. Bilebileceğimiz
    /// tek şey, bugünkü yapılandırmayla tam turun pencereyi aşıp aşmadığı.
    /// </para>
    /// </summary>
    private async Task WarnIfSweepOutrunsRetentionAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var objects = await db.RawManifest.CountAsync(cancellationToken);

        if (objects == 0 || _options.ScrubSampleSize <= 0)
        {
            return;
        }

        var rounds = Math.Ceiling((double)objects / _options.ScrubSampleSize);
        var sweep = TimeSpan.FromTicks((long)(_options.ScrubInterval.Ticks * rounds));

        if (sweep <= _options.SegmentRetention)
        {
            logger.LogInformation(
                "Ham arşiv: {Objects} nesne, tam tarama ~{Sweep:F1} saat, kurtarma penceresi {Window:F1} saat.",
                objects,
                sweep.TotalHours,
                _options.SegmentRetention.TotalHours);

            return;
        }

        logger.LogWarning(
            "Ham arşiv taraması kurtarma penceresinden UZUN: {Objects} nesne için tam tarama " +
            "~{Sweep:F1} saat sürüyor, pencere {Window:F1} saat. Bu yapılandırmada bir kayıp, " +
            "kaynak WAL segmenti silindikten sonra fark edilebilir ve kurtarma çalışmaz. " +
            "ScrubSampleSize artırılmalı ya da SegmentRetention uzatılmalı.",
            objects,
            sweep.TotalHours,
            _options.SegmentRetention.TotalHours);
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
