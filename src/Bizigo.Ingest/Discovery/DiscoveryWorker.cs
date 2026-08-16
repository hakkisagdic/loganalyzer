using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Ingest.Discovery;

/// <summary>
/// Keşif kuyruğunu tüketen tek işçi (F1 §9).
///
/// <para>
/// Ingest boru hattından <b>tamamen ayrık</b>: buradaki her arıza en fazla
/// <c>template_id</c> kaybına yol açar. Bu yüzden döngünün gövdesi baştan sona
/// yakalanıyor — bir istisnanın işçiyi düşürmesi, sidecar'ı sıcak yola sokmakla
/// aynı sonucu verir: özellik sessizce ölür ve kimse fark etmez.
/// </para>
///
/// <para>
/// Devre açıkken kuyruk yine <b>boşaltılıyor</b>. Boşaltılmasaydı kuyruk
/// dolar, her yeni olay "dolu" diye düşerdi ve devre kapandığında elde beş
/// dakika bayatlamış iş kalırdı — istenen, en taze satırlarla devam etmek.
/// </para>
/// </summary>
public sealed class DiscoveryWorker(
    SidecarOptions options,
    DiscoveryQueue queue,
    SidecarClient client,
    SidecarCircuitBreaker breaker,
    TemplateCache cache,
    DiscoveryStats stats,
    ILogger<DiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Sidecar keşif yolu kapalı (Sidecar:Enabled=false).");
            return;
        }

        logger.LogInformation(
            "Keşif işçisi başladı: {BaseUrl}, kuyruk {Capacity}, zaman aşımı {Timeout}.",
            options.BaseUrl,
            options.QueueCapacity,
            options.Timeout);

        var buffer = new List<DiscoveryItem>(options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                buffer.Clear();
                while (buffer.Count < options.BatchSize && queue.Reader.TryRead(out var item))
                {
                    buffer.Add(item);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                if (!breaker.TryAcquire())
                {
                    for (var index = 0; index < buffer.Count; index++)
                    {
                        stats.DropCircuitOpen();
                    }

                    continue;
                }

                await ProcessAsync(buffer, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Buraya düşmek bir hata; ama işçiyi öldürmek daha büyük hata.
                logger.LogError(ex, "Keşif işçisinde beklenmedik hata; döngü sürüyor.");
                breaker.RecordFailure($"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task ProcessAsync(List<DiscoveryItem> buffer, CancellationToken cancellationToken)
    {
        // Kaynak sınıfı başına ayrı miner: tek istekte tek anahtar.
        foreach (var group in buffer.GroupBy(static item => item.SourceClass, StringComparer.Ordinal))
        {
            var items = group.ToArray();
            var request = new MineRequest(
                group.Key,
                [.. items.Select(static (item, index) =>
                    new MineMessage(index.ToString(CultureInfo.InvariantCulture), item.Text))]);

            stats.Request();
            var outcome = await client.MineAsync(request, cancellationToken).ConfigureAwait(false);

            if (outcome.VersionMismatch)
            {
                stats.RequestFailed(timedOut: false);
                breaker.TripOnVersionMismatch(outcome.Error ?? "sürüm uyuşmazlığı");
                logger.LogError(
                    "Sidecar sözleşmesi uyuşmuyor, devre kesici açıldı: {Error}", outcome.Error);
                return;
            }

            if (outcome.Response is null)
            {
                stats.RequestFailed(outcome.TimedOut);
                breaker.RecordFailure(outcome.Error ?? "bilinmeyen hata");
                logger.LogDebug("Sidecar isteği başarısız: {Error}", outcome.Error);
                return;
            }

            breaker.RecordSuccess();
            Apply(items, outcome.Response);
        }
    }

    private void Apply(DiscoveryItem[] items, MineResponse response)
    {
        var newTemplates = 0;

        foreach (var result in response.Results)
        {
            if (!int.TryParse(result.Id, out var index) || index < 0 || index >= items.Length)
            {
                continue;
            }

            var item = items[index];

            if (result.IsNew)
            {
                newTemplates++;
            }

            // Sidecar'ın maskesi ile bizimki ayrışıyorsa önbelleğe yazmak
            // yanlış `template_id` üretir — sayacı artır, kaydı atla.
            if (!string.Equals(result.Masked, item.Signature, StringComparison.Ordinal))
            {
                stats.Drift();
                continue;
            }

            if (!string.IsNullOrEmpty(result.TemplateId))
            {
                cache.Set(item.Signature, result.TemplateId);
            }
        }

        stats.Mined(response.Results.Count, newTemplates);
    }
}
