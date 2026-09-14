using Bizigo.Cli;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>stdio ürün yüzeyinin servis grafiği GERÇEK yığına karşı</b> (M12).
///
/// <para>
/// Birim bekçisi (<c>McpStdioSurfaceTests</c>) <b>ulaşılamayan</b> adreslerle
/// koşuyor ve doğru soruyu soruyor: <i>grafik kurulabiliyor mu</i>. Kurulabilmek
/// bağlanabilmek değil, ve aradaki boşlukta iki arıza sınıfı yaşıyor:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>ÖMÜR hataları.</b> <c>IScopedQuery</c> <b>scoped</b>, <c>AlertRuleService</c>
/// ve <c>IDbContextFactory</c> <b>singleton</b>. Kurulum bunu ölçmüyor: esir
/// bağımlılık (singleton bir servisin scoped bir bağımlılığı tutması) ancak
/// gerçek bir kapsam açılıp <b>kullanıldığında</b> görünüyor — ve bu depoda
/// <c>ProductReadTool</c> belgesinde adı konmuş bir sınıf.
/// </item>
/// <item>
/// <b>Yanlış yapılandırılmış bağlam.</b> <c>AddControlPlane</c> ve
/// <c>AddBizigoDataPlane</c> bağlantı dizgesini yapıcıda kullanmıyor; hatalı bir
/// kayıt ilk SORGUDA görünüyor.
/// </item>
/// </list>
///
/// <para>
/// ⚠️ <b>§2 — BU TESTLER BU TURDA KOŞTURULMADI.</b> Docker açık ve §2 ajanın
/// entegrasyon testi koşturmasına yalnızca Docker kapalıyken izin veriyor; koşum
/// koordinatörde. <b>Yeşil gösterilmiyor.</b>
/// </para>
///
/// <h3>Burada OLMAYAN test — ve sebebi bir karar bekliyor</h3>
///
/// <para>
/// <i>"stdio'dan <c>logs.search</c> çağırınca satır dönüyor mu"</i> testi
/// <b>yazılamıyor</b>, çünkü stdio taşımasında kimlik yok: ürün araçlarının
/// tamamı <c>unauthenticated</c> dönüyor (M12 turunda gerçek süreçle ölçüldü —
/// 16 araç ilan ediliyor, <c>server.info</c> dışındaki 15'i reddediyor).
/// <c>McpCallerScope.NoIdentityMessage</c> yönü zaten yazıyor — <i>"stdio
/// taşıması … kimliği ortamdan bekliyor"</i> — ama o mekanizma henüz yok ve
/// <b>ne olacağı bir K17 kararı</b>: kimliği ortamdan almak, veri kapsamını
/// Keycloak yerine bir süreç ayarına bağlamak demek.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class McpStdioProductGraphTests(DevStackFixture stack)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// <b>Grafikten çözülen <c>IScopedQuery</c> gerçekten sorgu koşturuyor.</b>
    ///
    /// <para>
    /// Ölçülen şey satırların içeriği değil — onu <c>ScopeNegativeTests</c> ve
    /// <c>F2ChainTests</c> zaten ölçüyor ve ikinci bir kopya §9'un yasakladığı
    /// şey. Ölçülen şey <b>CLI'ın kurduğu grafiğin</b> o sorguyu koşturabilmesi:
    /// doğru ömürler, doğru bağlantı dizgeleri, çalışan bir kapsam fabrikası.
    /// </para>
    ///
    /// <para>
    /// Sorgu <b>boş sonuç</b> dönebilir ve bu bir başarı: iddia satır sayısı
    /// değil, çağrının <b>tamamlanması</b>. Satır bekleyen bir iddia bu testi
    /// tohumlamaya bağlardı ve ölçtüğü şeyi bulanıklaştırırdı.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Urun_grafiginden_cozulen_sorgu_kosuyor()
    {
        IDbContextFactory<ControlPlaneDbContext> factory =
            new ControlPlaneFactory(stack.PostgresConnectionString);

        await using (var db = await factory.CreateDbContextAsync(Token))
        {
            await db.Database.MigrateAsync(Token);
        }

        await using var services = McpCommandHandlers.BuildServices(
            McpSurface.Product,
            stack.ClickHouseConnectionString,
            stack.PostgresConnectionString);

        // KAPSAM AÇILIYOR — araçların üretimde yaptığı şeyin aynısı.
        // Kök sağlayıcıdan `IScopedQuery` çözmek zaten patlıyor (scoped) ve o
        // hâl birim bekçisinin kapsamında; buradaki soru kapsamın İÇİNDE ne
        // olduğu.
        await using var scope = services.CreateAsyncScope();

        var query = scope.ServiceProvider.GetRequiredService<IScopedQuery>();

        var page = await query.SearchEventsAsync(
            new EventQuery
            {
                From = DateTimeOffset.UtcNow.AddHours(-1),
                To = DateTimeOffset.UtcNow,
                Limit = 1,
            },
            AccessScope.ForGroups("m12-olcum", ["net-core"]),
            Token);

        Assert.NotNull(page);
    }

    /// <summary>
    /// <b>Araçların kendisi grafikten kurulup KOŞUYOR.</b>
    ///
    /// <para>
    /// Birim bekçisi araçların <i>kurulabildiğini</i> ölçüyor. Bu test bir adım
    /// ötesi: kurulan araç gerçek bir kapsamla çağrıldığında dönüyor mu. Özne
    /// <c>alerts.maintenance</c>, çünkü tek bağımlılığı kontrol düzlemi ve
    /// ClickHouse istemiyor — yani düşerse sebep <b>grafik</b>, veri değil.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Grafikten_kurulan_arac_kosuyor()
    {
        IDbContextFactory<ControlPlaneDbContext> factory =
            new ControlPlaneFactory(stack.PostgresConnectionString);

        await using (var db = await factory.CreateDbContextAsync(Token))
        {
            await db.Database.MigrateAsync(Token);
        }

        await using var services = McpCommandHandlers.BuildServices(
            McpSurface.Product,
            stack.ClickHouseConnectionString,
            stack.PostgresConnectionString);

        var tool = BizigoMcpServer
            .Tools(McpSurface.Product, McpCommandHandlers.ToolAssembliesFor(McpSurface.Product), services)
            .OfType<Bizigo.Mcp.Product.Tools.AlertsMaintenanceTool>()
            .Single();

        var result = await tool.ExecuteScopedAsync(
            new McpToolInvocation(new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal), AccessScope.Denied),
            AccessScope.ForGroups("m12-olcum", ["net-core"]),
            Token);

        Assert.False(result.IsError);
        Assert.True(result.Payload.GetProperty("total").GetInt32() >= 0);
    }
}
