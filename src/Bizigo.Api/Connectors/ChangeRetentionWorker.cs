using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Connectors;

/// <summary>
/// Webhook teslimat kayıtlarının ve connector çalışma geçmişinin saklama
/// temizliği (T24'ün açık bıraktığı, T25'te kapatılan karar).
///
/// <para>
/// <b>Tek iş, iki tablo.</b> İki ayrı saklama politikası tasarlamak altı ay
/// sonra iki farklı temizlik işi ve iki farklı sürpriz demekti. İkisi de aynı
/// soruya hizmet ediyor — "bu kaynak ne zaman ne yaptı" — ve aynı hızda
/// eskiyorlar.
/// </para>
///
/// <para>
/// <b>Silme neden güvenli:</b> <c>change_webhook_deliveries</c> yalnızca
/// idempotans penceresi için var; 90 gün önceki bir teslimatın yeniden gelmesi
/// diye bir şey yok (sağlayıcılar saatler içinde yeniden dener). Asıl veri —
/// <c>change_events</c> — bu temizlikten <b>etkilenmiyor</b>; RCA'nın F3'te
/// arayacağı geçmiş orada duruyor ve hiç silinmiyor.
/// </para>
///
/// <para>
/// Tur doğrudan çağrılabiliyor (<see cref="RunTurnAsync"/>): testin arka plan
/// görevini başlatıp duvar saatiyle beklemesi F1'in en pahalı hata sınıfıydı.
/// </para>
/// </summary>
public sealed class ChangeRetentionWorker(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ChangeConnectorOptions options,
    TimeProvider clock,
    ILogger<ChangeRetentionWorker> log) : BackgroundService
{
    public long Turns { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.CleanupInterval, clock);

        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunTurnAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Saklama temizliği turu hata verdi.");
            }
        }
    }

    /// <returns>Silinen satır sayısı: teslimat kayıtları + çalışma geçmişi.</returns>
    public async Task<(int Deliveries, int Runs)> RunTurnAsync(
        CancellationToken cancellationToken = default)
    {
        Turns++;

        var cutoff = clock.GetUtcNow() - options.Retention;

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // `ExecuteDelete` tek SQL: satırları belleğe çekip tek tek silmek, 90
        // günlük birikimde işi kendi başına bir yük hâline getirirdi.
        var deliveries = await db.ChangeWebhookDeliveries
            .Where(d => d.ReceivedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var runs = await db.ChangeConnectorRuns
            .Where(r => r.StartedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deliveries + runs > 0)
        {
            // Sessizce silmek, "kayıtlarım nereye gitti" sorusunu cevapsız
            // bırakırdı.
            log.LogInformation(
                "Saklama temizliği: {Deliveries} teslimat, {Runs} koşu kaydı silindi ({Cutoff} öncesi).",
                deliveries, runs, cutoff);
        }

        return (deliveries, runs);
    }
}
