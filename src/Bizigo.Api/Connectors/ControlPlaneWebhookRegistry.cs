using Bizigo.Api.Webhooks;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Connectors;

/// <summary>
/// Webhook uçlarını <b>kontrol düzleminden</b> çözer — yani ekrandan tanımlanan
/// connector'lardan (T25, K34: "ekrandan yapılandırılabilmeli").
///
/// <para>
/// <b>İki kaynak var ve sıra bir karar:</b> veritabanı önce, yapılandırma
/// dosyası sonra. T24 uçları <c>appsettings.json</c>'dan okuyordu; o yol
/// silinmedi çünkü ekran gelmeden kurulmuş ortamların çalışmaya devam etmesi
/// gerekiyor. Ama <b>veritabanı kazanıyor</b>: tersi olsaydı, ekrandan yapılan
/// bir değişiklik unutulmuş bir <c>appsettings</c> satırı yüzünden sessizce
/// etkisiz kalırdı — T18'in "yayınlanan taslak repodaki dosyayı gölgeliyor"
/// kuralıyla aynı gerekçe: ürünün içinden yapılan değişiklik görünür olmalı.
/// </para>
///
/// <para>
/// Önbellek yok. Webhook trafiği düşük (günde onlarca-yüzlerce) ve tek bir
/// indekslenmiş satır okuması, bir önbelleğin getireceği tazelik sorusundan
/// ucuz: geçersiz kılınmamış bir önbellek, ekrandan pasife alınan bir ucun
/// dakikalarca kabul etmeye devam etmesi demekti.
/// </para>
/// </summary>
public sealed class ControlPlaneWebhookRegistry(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ChangeConnectorService connectors,
    ChangeWebhookRegistry fallback,
    ILogger<ControlPlaneWebhookRegistry> log) : IChangeWebhookRegistry
{
    public async Task<ChangeWebhookEndpoint?> FindAsync(
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(endpointId))
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var connector = await db.ChangeConnectors
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Slug == endpointId && c.ConnectorType == ChangeConnectorType.Webhook,
                cancellationToken);

        if (connector is null)
        {
            return fallback.Find(endpointId);
        }

        // Pasif connector "yok" görünüyor — T24'teki gerekçenin aynısı: "var ama
        // kapalı" ayrımını dışarı vermek, geçerli uç kimliklerini deneyerek
        // keşfetmeye kapı açardı.
        if (!connector.Enabled)
        {
            return null;
        }

        var context = connectors.BuildContext(connector);

        if (WebhookConnectorRunner.TryBuildEndpoint(
                connector, context.Credential, out var endpoint, out var error))
        {
            return endpoint;
        }

        // Bozuk yapılandırma sessizce yapılandırma dosyasına DÜŞMÜYOR: o, ekranda
        // düzeltilmesi gereken bir hatayı görünmez kılardı.
        log.LogError(
            "Webhook connector yapılandırması geçersiz: {Slug} — {Error}", connector.Slug, error);

        return null;
    }
}
