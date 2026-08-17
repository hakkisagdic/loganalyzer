using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Ingest.Discovery;

/// <summary>Bir <see cref="DiscoveryWorker.RunTurnAsync"/> turunun sonucu.</summary>
public enum DiscoveryTurn
{
    /// <summary>Kuyrukta iş yoktu.</summary>
    Idle,

    /// <summary>Devre açıktı; yığın alındı ve düşürüldü.</summary>
    CircuitOpen,

    /// <summary>Sidecar cevapladı.</summary>
    Processed,

    /// <summary>İstek düştü — çağıran geri adım atmalı.</summary>
    Failed,
}

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
    ILogger<DiscoveryWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Geri adım ilerlemesi: ilk düşüşte 200 ms, sonra ikiye katlanarak 5 sn'de
    /// duruyor. Saf fonksiyon — saate dokunmadan sınanabilsin diye ayrı.
    /// </summary>
    public static TimeSpan NextBackoff(TimeSpan previous) =>
        previous == TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(200)
            : TimeSpan.FromMilliseconds(Math.Min(previous.TotalMilliseconds * 2, 5_000));

    /// <summary>
    /// Döngünün <b>tek turu</b>: kuyruğu boşalt, devreyi kontrol et, sidecar'a git.
    ///
    /// <para>
    /// <b>Neden public:</b> testin bu işi arka plan görevini başlatıp sonucu
    /// yoklayarak sınaması gerekiyordu, ve yoklamanın duvar saati bütçesi ağır
    /// yükte doluyordu — aynı commit yerelde 6,5 dakika, CI'da 14 saniye sürüyor.
    /// Turu doğrudan çağırmak zamanlamayı denklemden çıkarıyor. Bu bir veri
    /// kapısı değil, işin biriminin kendisi: <c>ExecuteAsync</c> de tam olarak
    /// bunu çağırıyor, dolayısıyla test ile üretim aynı kodu koşuyor.
    /// </para>
    /// </summary>
    public async Task<DiscoveryTurn> RunTurnAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new List<DiscoveryItem>(options.BatchSize);

        while (buffer.Count < options.BatchSize && queue.Reader.TryRead(out var item))
        {
            buffer.Add(item);
        }

        if (buffer.Count == 0)
        {
            return DiscoveryTurn.Idle;
        }

        if (!breaker.TryAcquire())
        {
            for (var index = 0; index < buffer.Count; index++)
            {
                stats.DropCircuitOpen();
            }

            return DiscoveryTurn.CircuitOpen;
        }

        return await ProcessAsync(buffer, cancellationToken).ConfigureAwait(false)
            ? DiscoveryTurn.Failed
            : DiscoveryTurn.Processed;
    }

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

        var backoff = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                var turn = await RunTurnAsync(stoppingToken).ConfigureAwait(false);

                // Devre açılana kadarki pencerede geri adım **şart**. Ölü bir
                // sidecar'da bağlantı reddi mikrosaniyede dönüyor, yani işçi
                // kuyruğu sıkı döngüde tüketip ağ çağrısı üstüne ağ çağrısı
                // yapıyor. Canlı ölçümde bunun bedeli görüldü: sidecar ölüyken
                // ingest'in etiketleme yolu 2,7× yavaşladı — sebep etiketleme
                // değil, bu döngünün çaldığı CPU'ydu.
                if (turn == DiscoveryTurn.Failed)
                {
                    backoff = NextBackoff(backoff);
                    await Task.Delay(backoff, _time, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    backoff = TimeSpan.Zero;
                }
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

    /// <returns><c>true</c> ise istek düştü — çağıran geri adım atmalı.</returns>
    private async Task<bool> ProcessAsync(List<DiscoveryItem> buffer, CancellationToken cancellationToken)
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
                return true;
            }

            if (outcome.Response is null)
            {
                stats.RequestFailed(outcome.TimedOut);
                breaker.RecordFailure(outcome.Error ?? "bilinmeyen hata");
                logger.LogDebug("Sidecar isteği başarısız: {Error}", outcome.Error);
                return true;
            }

            breaker.RecordSuccess();
            Apply(items, outcome.Response);
        }

        return false;
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
