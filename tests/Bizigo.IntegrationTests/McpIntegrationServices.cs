using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Mcp.Product;
using Bizigo.Parsing.Dispatch;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Ürün yüzeyinin MCP ilkellerinin bağımlılıkları — TEK yerde.</b>
///
/// <para>
/// <b>Var olma sebebi ölçülmüş bir olay.</b> M07 keşfi araçlardan
/// <b>ilkellere</b> genişletti (<c>McpPrimitiveDiscovery</c>): artık
/// <c>CreateOptions</c> beyan edilen derlemedeki araçları <i>ve kaynakları</i>
/// örnekliyor. Sonucu, beş entegrasyon testinin aynı anda düşmesi — üç ayrı
/// sınıfta, üç ayrı eksik kayıtla:
/// </para>
///
/// <list type="bullet">
/// <item><c>AlertRuleService</c> — M04'ün <c>alerts.rules</c> aracı</item>
/// <item><c>ParserCatalog</c> — M07'nin <c>ParserDefinitionResource</c>'u</item>
/// <item><c>RcaReportStore</c> — M07'nin <c>RcaReportResource</c>'u</item>
/// </list>
///
/// <para>
/// Üç dosyaya üç ayrı yama yazmak <b>dördüncü ilkel geldiğinde dördüncü kez</b>
/// aynı işi yapmak olurdu, ve bu depoda ölçülmüş sınıf: elle tutulan liste
/// ayrışıyor. Bu sınıf tek giriş veriyor.
/// </para>
///
/// <para>
/// <b>Kayıtlar ÜRETİMİN kendi uzantılarından</b> (<c>AddBizigoEvidence</c>,
/// <c>AddBizigoRcaTriggers</c>, <c>AddBizigoReadTools</c>) — elle
/// <c>AddScoped&lt;EvidenceBundleStore&gt;</c> yazmak daha kısa olurdu ve
/// yanlış olurdu: ömürler üretimde değiştiği gün test eski ömürle ölçmeye
/// devam ederdi, ve esir bağımlılık ancak üretimde patlardı. Aynı gerekçe
/// birim tarafında <c>McpTestServices.AddDiscoveredToolDependencies</c>
/// içinde de yazılı (§9).
/// </para>
///
/// <para>
/// <b>Bu sınıfın KARŞILAMADIĞI şey:</b> <c>IScopedQuery</c> ve
/// <c>IAccessScopeResolver</c>. İkisi de her testin <b>kendi iddiasının
/// parçası</b> — biri gerçek ClickHouse bağlamını, diğeri ölçülen kapsamı
/// taşıyor. Buraya konsalardı testler kapsamı kendi seçemez ve kapsam kapısı
/// hakkında hiçbir şey söyleyemezlerdi.
/// </para>
/// </summary>
internal static class McpIntegrationServices
{
    /// <summary>
    /// Ürün ilkellerinin örneklenebilmesi için gereken asgari kap.
    /// </summary>
    /// <param name="services">Doldurulacak koleksiyon.</param>
    /// <param name="controlPlane">
    /// Gerçek kontrol düzlemi fabrikası. Testin kendi Postgres konteynerinden
    /// geliyor — sahte bir fabrika, kapsam kapısının veritabanı tarafını
    /// ölçülmez kılardı.
    /// </param>
    public static IServiceCollection AddProductPrimitiveDependencies(
        this IServiceCollection services,
        IDbContextFactory<ControlPlaneDbContext> controlPlane)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(controlPlane);

        services.AddSingleton(controlPlane);

        // Alarm araçları: `AlertingOptions` yapılandırmadan gelmiyor, çünkü bu
        // testlerin hiçbiri alarm DAVRANIŞINI ölçmüyor — araçların
        // kurulabilmesini ölçüyor. Varsayılan yeterli ve gerekçesi burada.
        services.AddSingleton(new AlertingOptions());
        services.AddSingleton<AlertRuleService>();

        // `catalog.parsers` aracı ve `ParserDefinitionResource` aynı kataloğu
        // istiyor. Boş katalog geçerli: kaynağın "bulunamadı" dalı da ölçülüyor.
        services.AddSingleton(new ParserCatalog());

        // Kanıt paketi deposu (scoped) ve RCA rapor deposu (singleton) —
        // ömürler uzantıların içinde, burada tekrarlanmıyor.
        services.AddBizigoEvidence();
        services.AddBizigoRcaTriggers();

        // `TimeProvider.System` dahil, M04'ün okuma araçlarının tabanı.
        services.AddBizigoReadTools();

        return services;
    }
}
