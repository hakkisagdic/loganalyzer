using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Rca.Reasoning;
using Microsoft.EntityFrameworkCore;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Resources;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>M07 · Kaynak kanalının canlı yığına karşı ölçümü.</b>
///
/// <para>
/// <b>KOŞTURULMADI.</b> Yazan ajan Docker'a dokunmuyor (§2); bu dosya
/// koordinatörün canlı yığın koşumunda açılıyor.
/// </para>
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlıyor.</b> Birim paketi
/// (<c>McpResourceContractTests</c>) kapsam kapısını <b>bellek içi</b> EF ile
/// ölçüyor: <c>ReadScopedAsync</c> doğrudan çağrılıyor ve kapsam elle veriliyor.
/// O ölçüm iki halkayı <b>atlıyor</b>, ve ikisi de bu depoda daha önce sessizce
/// kırılmış halkalar:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Kapsam gerçekten OTURUMDAN geliyor mu.</b> Birim testinde kapsamı test
/// yazıyor; burada <c>resources/read</c> çağrısının kimliği
/// <c>IAccessScopeResolver</c> üzerinden çözülüyor, yani REST uçlarının geçtiği
/// kapıdan. Bu halka kırılırsa kapsam <b>boşalır</b> ve cevap "bulunamadı"
/// olur — yani kusur, kapının çalıştığı hâlle <b>aynı</b> görünür. Ölçümün
/// olumlu yarısı (sahibin okuyabilmesi) bu yüzden burada zorunlu.
/// </item>
/// <item>
/// <b>Gerçek Postgres'te <c>BundleScope</c> geri okunabiliyor mu.</b> Kapsam
/// kontrolü deponun döndürdüğü nesne üzerinde çalışıyor; JSON kolonundan
/// <c>OwnerGroups</c> boş dönerse <c>IsReadableBy</c> <b>her zaman</b>
/// <see langword="false"/> olur ve kanal sessizce hiçbir belge sunmaz.
/// Bellek içi sağlayıcı bunu ölçmüyor.
/// </item>
/// </list>
///
/// <para>
/// <b>Ölçmediği şey:</b> HTTP taşıması ve Keycloak kimliği — o zincir
/// <c>McpKeycloakIdentityTests</c>'te ve ikinci kez kurulmadı (§9).
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class McpResourceScopeIntegrationTests(DevStackFixture stack)
{
    /// <summary>
    /// <b>Kapsam dışı bir paketin adresi canlı depoda da reddediliyor.</b>
    ///
    /// <para>
    /// Ölçümün üç adımı ve üçü de gerekli: sahibi okuyabiliyor (yoksa test
    /// "her zaman null" bir kaynakla da geçerdi), yabancı okuyamıyor, ve
    /// reddin biçimi <b>bulunamadı</b> — "yetkiniz yok" değil.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_paket_canli_depoda_da_bulunamiyor()
    {
        await using var services = await BuildAsync();

        var bundle = await KaydetAsync(services, "network/edge");
        var resource = Kaynak<EvidenceBundleResource>(services);

        var sahip = await resource.ReadScopedAsync(
            Adres(EvidenceBundleResource.ResourceKind, bundle.Id, ["network/edge"]),
            TestContext.Current.CancellationToken);

        // OLUMLU YARI ŞART: gerçek Postgres'te `BundleScope` geri okunabiliyor
        // mu. Okunamazsa `IsReadableBy` her zaman false döner ve aşağıdaki
        // olumsuz iddia YANLIŞ SEBEPLE geçer.
        Assert.NotNull(sahip);

        var yabanci = await resource.ReadScopedAsync(
            Adres(EvidenceBundleResource.ResourceKind, bundle.Id, ["network/core"]),
            TestContext.Current.CancellationToken);

        Assert.Null(yabanci);
    }

    /// <summary>
    /// <b>Sistem kapsamıyla toplanmış paket yalnızca sınırsız okuyucuya açık.</b>
    ///
    /// <para>
    /// Bu, <c>BundleScope.IsReadableBy</c>'ın en kolay atlanan dalı: içinde
    /// <b>her grubun</b> verisi olabilen bir paketi, sınırlı bir okuyucunun
    /// daraltarak alması. Bellek içi ölçüm bu dalı görmüyor çünkü orada sistem
    /// kapsamlı paket üretilmiyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sistem_kapsamli_paket_sinirli_okuyucuya_kapali()
    {
        await using var services = await BuildAsync();

        var bundle = await KaydetAsync(services, ownerGroup: null);
        var resource = Kaynak<EvidenceBundleResource>(services);

        var sinirli = await resource.ReadScopedAsync(
            Adres(EvidenceBundleResource.ResourceKind, bundle.Id, ["network/edge"]),
            TestContext.Current.CancellationToken);

        Assert.Null(sinirli);

        var sinirsiz = await resource.ReadScopedAsync(
            new McpResourceRead(
                new McpResourceUri(EvidenceBundleResource.ResourceKind, bundle.Id.ToString()),
                AccessScope.System("yonetici")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(sinirsiz);
    }

    /// <summary>
    /// <b>Tel üzerinden: kapsam dışı adres <c>ResourceNotFound</c> ile dönüyor.</b>
    ///
    /// <para>
    /// Yukarıdaki iki test okuma yolunu ölçüyor; bu test <b>protokol
    /// biçimini</b>: kaynak kanalında <c>isError</c> yok, dolayısıyla "bu belge
    /// yok" cevabının spesifikasyondaki tek yeri bir JSON-RPC hatası. Kod
    /// yanlışsa istemci bir iş cevabını <b>bağlantı arızası</b> sanar (M01 §4).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Tel_uzerinde_bulunamadi_kodu_donuyor()
    {
        await using var services = await BuildAsync();

        var bundle = await KaydetAsync(services, "network/edge");

        // Oturum yardımcısı M08'in `McpIntegrationSession`'ı — ikinci bir taşıma
        // kurulumu yazmak, ölçülen sunucu ile üretimde koşanı ayırırdı (§9).
        // Kimliği o damgalıyor; kapsama çeviren şey aşağıdaki çözücü ve o da
        // REST'in geçtiği arayüzün kendisi.
        await using var session = await McpIntegrationSession.StartAsync(
            BizigoMcpServer.CreateOptions(
                McpSurface.Product,
                McpBoundaryDeclaration.Declare(DataBoundary.Internal, "entegrasyon: yerel yığın"),
                [typeof(EvidenceBundleResource).Assembly],
                services),
            services,
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await session.Client.ReadResourceAsync(
                $"{McpResourceUri.Scheme}://{EvidenceBundleResource.ResourceKind}/{bundle.Id}",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(McpErrorCode.ResourceNotFound, error.ErrorCode);

        // MESAJ SEBEBİ SÖYLEMİYOR — ve söylememesi şart: "kapsamınız görmüyor"
        // demek, paketin VAR OLDUĞUNU doğrulamak olur.
        Assert.DoesNotContain("network/edge", error.Message, StringComparison.Ordinal);
    }

    private static McpResourceRead Adres(string kind, Guid id, string[] groups) =>
        new(new McpResourceUri(kind, id.ToString()), AccessScope.ForGroups("okuyucu", groups));

    private static TResource Kaynak<TResource>(IServiceProvider services)
        where TResource : ProductResource =>
        BizigoMcpServer
            .Resources(McpSurface.Product, [typeof(EvidenceBundleResource).Assembly], services)
            .OfType<TResource>()
            .Single();

    private async Task<ServiceProvider> BuildAsync()
    {
        var factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        var services = new ServiceCollection();

        // ÖMÜRLER ÜRETİMDEKİ: `EvidenceBundleStore` scoped
        // (`EvidenceServiceCollectionExtensions`), `RcaReportStore` singleton
        // (`RcaServiceCollectionExtensions`). İkisini de singleton yazmak,
        // kaynakların okuma başına kapsam açtığını ölçülmez kılardı — esir
        // bağımlılık testte görünmez, üretimde patlar.
        // Kayıtlar ORTAK girişten: bu kap M04'ün araçlarını da örneklemek
        // zorunda (keşif beyan edilen derlemenin TAMAMINI kuruyor) ve elle
        // yazılmış hâli `AlertRuleService` ile `ParserCatalog`'u kaçırıyordu.
        // Ömür gerekçeleri uzantıların içinde.
        services.AddProductPrimitiveDependencies(factory);

        // Kapsam çözücüsü: `McpIntegrationSession`'ın damgaladığı kimliği
        // YABANCI bir gruba çeviriyor. Sabit olması ölçümü zayıflatmıyor —
        // ölçülen şey kaynak kanalının kapsamı UYGULAYIP UYGULAMADIĞI, kapsamın
        // nasıl türetildiği değil (o `McpKeycloakIdentityTests`'in işi).
        services.AddSingleton<IAccessScopeResolver>(
            new SabitKapsam(AccessScope.ForGroups("yabanci", ["network/core"])));

        return services.BuildServiceProvider();
    }

    private async Task<EvidenceBundle> KaydetAsync(IServiceProvider services, string? ownerGroup)
    {
        await using var scope = services.CreateAsyncScope();

        var store = scope.ServiceProvider.GetRequiredService<EvidenceBundleStore>();

        return await store.SaveAsync(
            new EvidenceBundle
            {
                Id = Guid.NewGuid(),
                GatheredAt = DateTimeOffset.UnixEpoch,
                Window = new RcaWindow
                {
                    From = DateTimeOffset.UnixEpoch,
                    To = DateTimeOffset.UnixEpoch.AddMinutes(45),
                    BaselineFrom = DateTimeOffset.UnixEpoch.AddDays(-7),
                    BaselineTo = DateTimeOffset.UnixEpoch,
                },
                Scope = ownerGroup is null
                    ? new BundleScope([], IsSystem: true)
                    : new BundleScope([ownerGroup], IsSystem: false),
                Slices = [],
                Trust = new WindowTrust(TotalEvents: 0, UnreliableTimeEvents: 0),
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Sabit kapsam döndüren çözücü — gerekçe kullanım yerinde.</summary>
    private sealed class SabitKapsam(AccessScope scope) : IAccessScopeResolver
    {
        public AccessScope Resolve(System.Security.Claims.ClaimsPrincipal? principal) => scope;
    }
}
