using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Bizigo.Mcp.Tools;
using Bizigo.Simulators.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M08 · Kimliğin MCP oturumundan araca ulaştığının ölçümü.</b>
///
/// <para>
/// Bitti tanımının 7. maddesi: <i>"Kapsam filtresi MCP yüzeyinde de tek kapıdan
/// geçiyor."</i> Buradaki testler o cümlenin iki yarısını ayrı ayrı ölçüyor —
/// <b>kapı var mı</b> (kimliksiz çağrı reddediliyor mu) ve <b>kapı aynı kapı mı</b>
/// (MCP'nin kullandığı çevrim REST uçlarınınkiyle aynı mı).
/// </para>
///
/// <para>
/// <b>Konteyner yok</b> (§2): <c>TestServer</c> süreç içinde koşuyor, kimlik
/// şeması asgari. Canlı Keycloak'a karşı ölçüm ayrı dosyada ve <b>koşturulmadı</b>
/// — bkz. <c>tests/Bizigo.IntegrationTests/McpKeycloakIdentityTests.cs</c>.
/// </para>
///
/// <para>
/// <b>Neden gerçek JWT doğrulaması ölçülmüyor.</b> Bu testler MCP'nin
/// <b>taşıdığı</b> kimliği ölçüyor, kimliğin <b>doğrulanmasını</b> değil.
/// İkincisi <c>AddJwtBearer</c>'ın ve Keycloak'ın işi ve zaten
/// <c>KeycloakRealmTests</c> ile entegrasyon paketinde ölçülüyor; buraya sahte
/// bir JWT doğrulayıcısı koymak, ölçtüğü şeyi ölçen aracın kendisi olurdu.
/// </para>
/// </summary>
public sealed class McpIdentityTests
{
    /// <summary>
    /// Sahte işleyicinin şeması, ve adı <b>üretimin şemasıyla aynı olmak
    /// zorunda</b>.
    ///
    /// <para>
    /// M09'dan önce <c>MapBizigoMcp</c> <c>RequireAuthorization()</c> ile
    /// host'un <i>varsayılan</i> şemasını kullanıyordu, dolayısıyla harness
    /// istediği adı verebiliyordu (<c>M08Test</c>). Artık uç şemasını
    /// <b>açıkça</b> belirtiyor — 45 ucun 401 davranışını değiştirmemek için —
    /// ve harness'ın onu taklit etmesi gerekiyor. Bu bir uyarlama değil bir
    /// düzeltme: harness artık üretimin gerçekten kullandığı şemayı ölçüyor.
    /// </para>
    /// </summary>
    private const string TestScheme = Api.BizigoAuthSchemes.Mcp;

    /// <summary>
    /// M06 sonrası <c>CreateOptions</c> bir <b>K6 beyanı</b> istiyor: MCP
    /// sunucusu ağ sınırını beyan etmeden kurulamıyor.
    ///
    /// <para>
    /// Bu dosyanın sorusu kimlik, sınır değil — beyan burada bir <b>ön şart</b>
    /// olarak duruyor ve <c>Internal</c> seçildi çünkü ölçülen kurulum üretimin
    /// kurulumu (<c>Mcp:DataBoundary=Internal</c>). Sınırın kendi kapısı
    /// <c>McpComplianceTests</c>'te ölçülüyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bu satır bir merge bulgusu.</b> M08 ile M06 ayrı dallarda koştu; git
    /// bu dosyayı çakışmasız birleştirdi çünkü M08 onu yeni ekliyordu ve M06
    /// hiç dokunmamıştı — ama <c>CreateOptions</c>'ın imzası değişmişti ve
    /// derleme kırıldı. <c>CLAUDE.md</c> §5'in adını koyduğu sınıf:
    /// <i>metinsel merge temiz, derleme kırık</i>.
    /// </para>
    /// </summary>
    private static McpBoundaryDeclaration IdentityTestBoundary =>
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "kimlik testleri: birim testi");

    /// <summary>
    /// <b>Kalıcı</b> kimlik muafiyetleri — ürün yüzeyinde kimlik istemeyen
    /// araçlar.
    ///
    /// <para>
    /// §8'in ayrımı: bu bir <c>Exempt</c> listesi, bir <c>Pending</c> listesi
    /// değil. "Bir gün kapanacak" ile "hiç kapanmayacak" aynı listede duramaz —
    /// dursaydı "liste boşaldı mı" sorusunun cevabı asla evet olamazdı.
    /// </para>
    /// </summary>
    private static readonly ImmutableDictionary<string, string> Exempt =
        ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            [
                KeyValuePair.Create(
                    ServerInfoTool.ToolIdentifier,
                    "Ürün verisine hiç dokunmuyor: yüzey adı, revizyon sabiti ve araç sayısı "
                    + "döndürüyor. Kapsam filtresinin uygulanacağı bir satır yok."),
            ]);

    /// <summary>
    /// Muafiyet sayısı <b>sabit</b>. Listeyi büyütmek bu satırı da değiştirmeyi
    /// gerektiriyor — muafiyet eklemek <b>iki ayrı bilinçli hareket</b> (§8).
    /// </summary>
    private const int ExpectedExemptCount = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------
    // 1 · Kimlik uca ulaşıyor mu
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Keycloak kimliği MCP oturumundan araca ulaşıyor</b> — ticket'ın
    /// cümlesinin kendisi.
    ///
    /// <para>
    /// Ölçülen şey teldeki sonuç değil yalnızca: aracın <b>eline geçen</b>
    /// <see cref="AccessScope"/> nesnesi. Yalnızca yükü sınayan bir test,
    /// aracın kapsamı görmediği ama şemaya uyan bir şey döndürdüğü hâli
    /// ayırt edemezdi.
    /// </para>
    ///
    /// <para>
    /// <b>Grupların eşlemeden geçtiği ayrıca ölçülüyor:</b> istekteki claim
    /// <c>/network/core</c> (Keycloak'ın baştaki eğik çizgisi), aracın gördüğü
    /// <c>owner_group</c> ise <c>core</c>. Yani claim doğrudan kapsam sayılmıyor,
    /// <c>idp_group_mapping</c> çevrimi MCP yüzeyinde de koşuyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kimlik_HTTP_tasimasindan_araca_ulasiyor()
    {
        var tool = new ScopeEchoTool();
        var gate = new RecordingScopeResolver(DefaultMapping);

        await using var app = BuildHost(tool, gate);
        await app.StartAsync(Ct);

        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestScheme, "ayse|/network/core");

        var payload = await CallEchoAsync(app, http);

        Assert.True(tool.Executed, "Araç hiç koşmadı — kimlik kapısı yanlış tarafta reddetmiş olmalı.");

        // Aracın ELİNE GEÇEN kapsam.
        Assert.Equal("ayse", tool.Observed!.Subject);
        Assert.Equal(["core"], tool.Observed.OwnerGroups.Order(StringComparer.Ordinal));
        Assert.False(tool.Observed.IsUnrestricted);

        // Aynı şey TELDE de görünüyor: aracın kapsamı okuyabildiği, okuduğunu
        // döndürebildiği anlamına gelmeli.
        Assert.Equal("ayse", payload.Subject);
        Assert.Equal(["core"], payload.OwnerGroups);
        Assert.False(payload.Unrestricted);

        await app.StopAsync(Ct);
    }

    /// <summary>
    /// <b>Kapı REST uçlarının geçtiği kapıyla aynı kapı</b> (bitti tanımı §7).
    ///
    /// <para>
    /// İki şey birden ölçülüyor ve ikisi de gerekli:
    /// </para>
    /// <list type="number">
    /// <item>
    /// MCP yolu <see cref="IAccessScopeResolver"/>'ı <b>gerçekten çağırıyor</b>
    /// ve ona <b>isteğin kendi kimliğini</b> veriyor — kaydedici bunu görüyor.
    /// Yalnızca sonuca bakan bir test, MCP'nin kendi çevrimini yazıp aynı sonucu
    /// üretmesini fark edemezdi (§9: "ikinci kopya yazma").
    /// </item>
    /// <item>
    /// Sonuç, aynı kimlik için <c>ICurrentUser.Scope</c>'un üreteceğiyle
    /// <b>birebir aynı</b>. Aynı kişi REST'ten ve MCP'den aynı veriyi görüyor.
    /// </item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Kapsam_REST_ile_ayni_kapidan_geliyor()
    {
        var tool = new ScopeEchoTool();
        var gate = new RecordingScopeResolver(DefaultMapping);

        await using var app = BuildHost(tool, gate);
        await app.StartAsync(Ct);

        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(TestScheme, "mehmet|/network/core|/guvenlik");

        await CallEchoAsync(app, http);

        // (1) Çevrim çağrıldı, ve isteğin kimliğiyle çağrıldı.
        var seen = Assert.Single(gate.Seen);
        Assert.Equal("mehmet", seen.FindFirst(BizigoClaims.Subject)?.Value);

        // (2) REST tarafının aynı kimlik için üreteceği kapsam — `ICurrentUser`
        //     de bu çevrimi çağırıyor, `HttpContextCurrentUser.Scope` üzerinden.
        var restScope = DefaultMapping.Resolve(seen);

        Assert.Equal(restScope.Subject, tool.Observed!.Subject);
        Assert.Equal(restScope.IsUnrestricted, tool.Observed.IsUnrestricted);
        Assert.Equal(
            restScope.OwnerGroups.Order(StringComparer.Ordinal),
            tool.Observed.OwnerGroups.Order(StringComparer.Ordinal));

        // Ve bu kişi gerçekten iki gruba çözülüyor: eşitliğin BOŞ iki kümenin
        // eşitliği olmadığını görmek gerekiyor, yoksa test her hâlükârda yeşil.
        Assert.Equal(["core", "guvenlik"], tool.Observed.OwnerGroups.Order(StringComparer.Ordinal));

        await app.StopAsync(Ct);
    }

    // ---------------------------------------------------------------------
    // 2 · Kimlik yokken ne oluyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>stdio'da kimlik yok, ve ürün aracı KOŞMADAN reddediliyor.</b>
    ///
    /// <para>
    /// Bu test M08'in açık sorusunun cevabı. Uyduğumuz revizyonun yetkilendirme
    /// spesifikasyonu (<c>2026-07-28</c>) stdio'yu kapsam dışı bırakıyor —
    /// <i>"Implementations using an STDIO transport SHOULD NOT follow this
    /// specification, and instead retrieve credentials from the environment"</i> —
    /// ve SDK'nın <c>MessageContext.User</c>'ı yalnızca ASP.NET taşımasında
    /// doluyor. Yani stdio'da <c>User</c> <see langword="null"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçümün kalbi son iddia:</b> araç <b>hiç koşmadı</b>. Kimliksiz bir
    /// çağrıya boş kapsam verip aracı koşturmak da "kapalı" olurdu — ama
    /// sessiz: <c>logs.search</c>'ün sıfır satırı ajan tarafından
    /// <i>"eşleşme yok"</i> diye okunur ve kimliğin kaybolduğu hiç görünmez.
    /// §7'nin en pahalı sınıfı tam olarak bu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kimliksiz_oturumda_urun_araci_kosmadan_reddediliyor()
    {
        var tool = new ScopeEchoTool();

        await using var services = ServicesWithGate(new RecordingScopeResolver(DefaultMapping));

        // Üretimin kendi kurulumu; araç sonradan ekleniyor çünkü keşif
        // `Bizigo.Api` kökünden koşuyor ve bu araç test derlemesinde.
        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product, IdentityTestBoundary, McpEndpoints.ToolAssemblies, services);
        options.ToolCollection!.Add(tool);

        // Akış taşıması — `StdioServerTransport`'un taban sınıfı, yani ölçülen
        // kod yolu stdio'nunki (ilişki `McpComplianceTests` içinde sınanıyor).
        await using var session = await McpTestSession.StartAsync(options, services, cancellationToken: Ct);

        var result = await session.Client.CallToolAsync(
            ScopeEchoTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.True(result.IsError is true, "Kimliksiz çağrı başarılı sayıldı.");

        Assert.Equal(
            McpToolError.Unauthenticated,
            result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());

        // Hata mesajı SEBEBİ söylüyor: "sonuç yok" ile "kimlik yok" aynı
        // cümleye düşerse istemci ilkini varsayar.
        Assert.Contains(
            "kimlik yok",
            result.StructuredContent.Value.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);

        Assert.False(
            tool.Executed,
            "Araç kimliksiz koştu. Boş kapsamla dönen bir sonuç 'eşleşme yok' diye okunur ve "
            + "kimliğin kaybolduğunu kimse görmez.");
    }

    /// <summary>
    /// <b>"Kimlik yok" ile "kimlik var, kapsam boş" ayrı şeyler.</b>
    ///
    /// <para>
    /// Eşlemesi olmayan bir gruba sahip kullanıcı <b>koşuyor</b> ve <b>hiçbir
    /// satır göremeyen</b> bir kapsam alıyor. Ret değil, çünkü kimlik gerçek:
    /// cevabı "yetkiniz yok" değil "kapsamınızda kayıt yok" — ve ikisi
    /// kullanıcıya farklı şey yaptırıyor (biri giriş, biri yönetici).
    /// </para>
    ///
    /// <para>
    /// Ayrı bir test, çünkü yukarıdaki ret testi tek başına <i>"her kimliksiz
    /// gibi görünen şeyi reddet"</i> ile de yeşil kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Eslesmeyen_gruplu_kimlik_kosuyor_ama_hicbir_sey_goremiyor()
    {
        var tool = new ScopeEchoTool();

        await using var app = BuildHost(tool, new RecordingScopeResolver(DefaultMapping));
        await app.StartAsync(Ct);

        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(TestScheme, "yabanci|/hic-eslesmeyen-grup");

        var payload = await CallEchoAsync(app, http);

        Assert.True(tool.Executed, "Gerçek bir kimlik reddedildi.");
        Assert.Empty(payload.OwnerGroups);
        Assert.False(payload.Unrestricted);
        Assert.True(tool.Observed!.IsEmpty, "Boş kapsam 'her şey' anlamına gelemez (K17).");

        await app.StopAsync(Ct);
    }

    // ---------------------------------------------------------------------
    // 3 · Kapının kendisi — muafiyet, kaçış deliği, kurulum
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Muafiyet listesi gerekçeli ve sayısı sabit.</b>
    ///
    /// <para>
    /// Küme <b>keşifle</b> bulunuyor, elle yazılmış bir listeden değil: bu
    /// deponun dört kez ödediği ders (<c>Produces&lt;T&gt;</c> kapısı,
    /// <c>Add*</c> kayıtları, <c>EvidenceEndpoints</c>, T45). Elle tutulan liste
    /// er ya da geç bekçiyi kör ediyor.
    /// </para>
    ///
    /// <para>
    /// M04 kimlik istemeyen bir okuma aracı yazarsa burası kırmızı yanıyor ve
    /// muafiyet <b>gerekçesiyle</b> yazılmak zorunda kalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kimlik_muafiyeti_gerekceli_ve_sayisi_sabit()
    {
        var exempt = ProductionTools()
            .Where(static tool => tool is { Surface: McpSurface.Product, RequiresCallerIdentity: false })
            .Select(static tool => tool.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(Exempt.Keys.Order(StringComparer.Ordinal), exempt);

        // `Assert.Single` değil: ölçülen şey "listede bir eleman var" değil
        // "listenin boyu YAZILI sabitle aynı". İkincisi muafiyet eklemeyi iki
        // ayrı bilinçli hareket yapıyor; birincisi sabit diye bir şey bırakmıyor.
        Assert.True(
            Exempt.Count == ExpectedExemptCount,
            $"Muafiyet sayısı {Exempt.Count}, `{nameof(ExpectedExemptCount)}` ise {ExpectedExemptCount}. "
            + "Muafiyet eklemek listeyi VE sayıyı değiştirmeyi gerektiriyor (§8).");

        Assert.All(Exempt, entry => Assert.False(
            string.IsNullOrWhiteSpace(entry.Value),
            $"`{entry.Key}` muafiyeti gerekçesiz. Gerekçesiz bir muafiyet, kapının silinmiş hâli."));
    }

    /// <summary>
    /// <b>Kimlik taşıyıcısı kapsam kaçış deliğine dokunmuyor.</b>
    ///
    /// <para>
    /// <see cref="AccessScope.System"/> statik, <c>IsUnrestricted = true</c> ve
    /// derleme hatası vermiyor: kapsamı atlamak için unutmak gerekmiyor,
    /// <b>çağırmak yetiyor</b>. Araç başına bu deliği kapatmak M04'ün kapısı;
    /// buradaki iddia dar ve M08'e ait — <b>kimliği taşıyan yolun kendisi</b> o
    /// deliğe düşmüyor.
    /// </para>
    ///
    /// <para>
    /// Ölçüm derleme metadata'sı üzerinden: <c>Bizigo.Mcp</c>'nin
    /// <c>MemberRef</c> tablosunda <c>AccessScope.System</c> <b>yok</b>.
    /// Kaynakta <c>grep</c> aramak fazla kırılgandı; metadata çağrının
    /// derlenmiş hâlini görüyor.
    /// </para>
    ///
    /// <para>
    /// <b>Bekçinin boşa yeşil yanmadığı ayrıca ölçülüyor:</b> aynı tarama
    /// <c>AccessScope.Denied</c>'ı <b>buluyor</b>. Bulmasaydı — tip adı
    /// değişse, tarama yanlış tabloya baksa — "System yok" cümlesi doğru ama
    /// <b>anlamsız</b> olurdu; yeşilliği hiçbir şey ifade etmeyen bekçi bu
    /// deponun adını koyduğu sınıf.
    /// </para>
    /// </summary>
    [Fact]
    public void Mcp_cekirdegi_kapsam_kacis_deligine_dokunmuyor()
    {
        var referenced = AccessScopeMembersReferencedBy(typeof(BizigoMcpTool).Assembly);

        Assert.DoesNotContain(nameof(AccessScope.System), referenced);

        Assert.Contains(
            $"get_{nameof(AccessScope.Denied)}",
            referenced);
    }

    /// <summary>
    /// <b>Kapsam çözücüsü yoksa sunucu hiç ayağa kalkmıyor.</b>
    ///
    /// <para>
    /// Eksik bir çözücü ilk çağrıda fark edilseydi arıza <i>"bu araç bende
    /// çalışmıyor"</i> diye görünürdü — kusurun kendisi değil belirtisi.
    /// Kurulumda patlamak mesajı kusurun üstüne koyuyor.
    /// </para>
    ///
    /// <para>
    /// Bu test aynı zamanda M08'in stdio cevabının mekanizması:
    /// <c>bizigo mcp serve</c> bugün boş bir servis grafiğiyle koşuyor, yani
    /// M04'ün ilk kimlik isteyen aracı geldiğinde <b>burada</b> duracak.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsam_cozucusu_kayitli_degilse_kurulum_patliyor()
    {
        // Araç bağımlılıkları VAR, çözücü YOK: aksi hâlde kurulum kimlikten
        // değil DI'dan şikâyet eder ve bu test yanlış sebeple kırmızı yanar.
        await using var without = McpTestServices.ForDiscoveredToolsWithoutResolver();

        var error = Assert.Throws<InvalidOperationException>(
            () => BizigoMcpServer.CreateOptions(
                McpSurface.Product, IdentityTestBoundary, [typeof(McpIdentityTests).Assembly], without));

        Assert.Contains(nameof(IAccessScopeResolver), error.Message, StringComparison.Ordinal);

        // Mesaj HANGİ araçların kimlik istediğini söylüyor: "bir şeyler eksik"
        // diyen bir hata, arayan kişiyi kodun tamamına gönderir.
        Assert.Contains(ScopeEchoTool.ToolIdentifier, error.Message, StringComparison.Ordinal);

        // Ve çözücü kayıtlıyken aynı kurulum geçiyor — yoksa yukarıdaki iddia
        // "bu kurulum her hâlükârda patlıyor" ile de yeşil kalırdı.
        await using var with = ServicesWithGate(new RecordingScopeResolver(DefaultMapping));

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product, IdentityTestBoundary, [typeof(McpIdentityTests).Assembly], with);

        Assert.NotEmpty(options.ToolCollection!);
    }

    // ---------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------

    /// <summary>
    /// Ölçümlerin eşleme tablosu. Keycloak'ın baştaki eğik çizgisi bilerek
    /// <b>yok</b>: normalizasyon <c>GroupMapping</c>'in işi ve testler onun
    /// koştuğunu görmek istiyor.
    /// </summary>
    private static GroupMapping DefaultMapping { get; } = GroupMapping.From(
        [("network/core", "core"), ("guvenlik", "guvenlik")]);

    /// <summary>
    /// Üretimin ilan ettiği araçlar — <b>üretimin kendi yolundan</b>.
    ///
    /// <para>
    /// Önceki hâli keşfi elle kuruyordu (<c>ProductAssemblies</c> +
    /// <c>ToolTypes</c> + <c>Instantiate</c>). O yol M05'te kaldırıldı: kökten
    /// referans izleme, derleyicinin buduğu bir <c>ProjectReference</c>'ı
    /// göremiyordu. Artık <c>BizigoMcpServer.Tools</c> çağrılıyor, yani bu test
    /// üretimin <b>gerçekten</b> ilan ettiği kümeye bakıyor — ikinci bir keşif
    /// kurulumu, ayrışabilecek ikinci bir gösterim olurdu.
    /// </para>
    /// </summary>
    private static IReadOnlyList<BizigoMcpTool> ProductionTools()
    {
        // Ürün kapanışının TAMAMI keşfe veriliyor, yani M04'ün araçları da
        // kuruluyor ve onlar ürün servislerine bağımlı. Boş bir kap burada
        // "kurulamadı" ile düşerdi — ve o kırmızı kimlik hakkında hiçbir şey
        // söylemezdi.
        var services = McpTestServices.ForDiscoveredTools();

        return BizigoMcpServer.Tools(McpSurface.Product, McpEndpoints.ToolAssemblies, services);
    }

    /// <summary>
    /// Kapsam çözücüsü <b>ve</b> keşfin ulaştığı araçların bağımlılıkları.
    ///
    /// <para>
    /// Simülatör kaydı M03'te eklendi ve gerekçesi mekanik: bu sınıfın
    /// kompozisyon kökü <c>Bizigo.UnitTests</c>, oradan <c>Bizigo.Simulators</c>'a
    /// ulaşılıyor, ve <c>Instantiate</c> bulduğu <b>her</b> aracı kuruyor —
    /// yüzeye göre ancak kurduktan <b>sonra</b> eliyor, çünkü <c>Surface</c> bir
    /// örnek özelliği. Yani ürün yüzeyini kurmak, simülatör araçlarının da
    /// kurulabilmesini istiyor.
    /// </para>
    ///
    /// <para>
    /// Kayıt hiçbir şey <b>ilan etmiyor</b>: <c>sim.*</c> araçları yüzeylerini
    /// kendileri beyan ediyor ve ürün yüzeyinde hiçbiri listeye girmiyor.
    /// Aşağıdaki testlerin ölçtüğü küme değişmiyor.
    /// </para>
    /// </summary>
    private static ServiceProvider ServicesWithGate(IAccessScopeResolver gate) =>
        new ServiceCollection()

            // M04'ün araçları da keşfe giriyor ve ürün servislerine bağımlı.
            // Kayıtlar uyum kapısıyla PAYLAŞILIYOR; ikinci bir kopya, bir gün
            // birinde olup diğerinde olmayan bir kayıt demek olurdu (§9).
            .AddDiscoveredToolDependencies()
            .AddSingleton(gate)
            .BuildServiceProvider();

    private static WebApplication BuildHost(ScopeEchoTool tool, IAccessScopeResolver gate)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services
            .AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, ClaimsFromHeader>(TestScheme, configureOptions: null);

        builder.Services.AddSingleton(gate);

        // M04'ün araçları da keşfe giriyor ve ürün servislerine bağımlı; keşif
        // kurulamayan aracı atlamıyor, patlıyor. Kayıtlar uyum kapısıyla
        // PAYLAŞILIYOR (§9).
        builder.Services.AddDiscoveredToolDependencies();

        // ÜRETİMİN kaydı. İkinci bir kurulum yazmak, ölçülen sunucu ile koşan
        // sunucuyu ayırırdı.
        builder.Services.AddBizigoMcp(builder.Configuration);

        // Araç sonradan ekleniyor: keşif `Bizigo.Api` kökünden koşuyor ve bu
        // araç test derlemesinde. `PostConfigure` üretimin `Configure`'undan
        // SONRA koşuyor, yani `Apply`'nin temizlediği koleksiyona ekliyor.
        builder.Services.PostConfigure<McpServerOptions>(options => options.ToolCollection!.Add(tool));

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBizigoMcp();

        return app;
    }

    private static async Task<EchoPayload> CallEchoAsync(WebApplication app, HttpClient http)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, BizigoMcpServer.HttpPath) },
            http,
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            transport, loggerFactory: NullLoggerFactory.Instance, cancellationToken: Ct);

        var result = await client.CallToolAsync(
            ScopeEchoTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.True(
            result.IsError is not true,
            $"Araç hata döndürdü: {result.StructuredContent?.GetRawText()}");

        return result.StructuredContent!.Value.Deserialize<EchoPayload>(McpJson.PayloadOptions)!;
    }

    /// <summary>
    /// <paramref name="assembly"/>'nin <c>AccessScope</c> üzerinde referans
    /// verdiği üye adları — IL çözümlemesi değil, <c>MemberRef</c> tablosu.
    /// </summary>
    private static IReadOnlyCollection<string> AccessScopeMembersReferencedBy(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);

        var reader = pe.GetMetadataReader();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);

            if (member.Parent.Kind is not HandleKind.TypeReference)
            {
                continue;
            }

            var owner = reader.GetTypeReference((TypeReferenceHandle)member.Parent);

            if (reader.GetString(owner.Name) == nameof(AccessScope))
            {
                names.Add(reader.GetString(member.Name));
            }
        }

        return names;
    }

    /// <summary>
    /// <c>Authorization: M08Test &lt;sub&gt;|&lt;grup&gt;|…</c> başlığını kimliğe
    /// çeviren asgari şema.
    ///
    /// <para>
    /// Claim adları <b>ürünün sözleşmesinden</b> (<see cref="BizigoClaims"/>),
    /// elle yazılmış dizgelerden değil: sözleşme bir gün değişirse bu test de
    /// onunla birlikte hareket etmeli, yoksa ölçtüğü şey artık ürün olmaz.
    /// </para>
    /// </summary>
    private sealed class ClaimsFromHeader(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.Count == 0
                || AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var header) is false
                || header!.Parameter is not { Length: > 0 } parameter)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var parts = parameter.Split('|', StringSplitOptions.RemoveEmptyEntries);

            var claims = new List<Claim> { new(BizigoClaims.Subject, parts[0]) };

            claims.AddRange(parts.Skip(1).Select(static group => new Claim(BizigoClaims.Groups, group)));

            var identity = new ClaimsIdentity(
                claims, TestScheme, BizigoClaims.PreferredUsername, BizigoClaims.Roles);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    /// <summary>
    /// Ürün çevriminin kaydeden sarmalayıcısı — <b>ikinci bir çevrim değil</b>.
    ///
    /// <para>
    /// İçeride gerçek <see cref="GroupMapping"/> koşuyor; bu sınıf yalnızca
    /// <i>"çağrıldı mı, hangi kimlikle"</i> sorusunu görünür kılıyor. Kendi
    /// cevabını üretseydi test, MCP'nin doğru çevrimi kullandığını değil
    /// yalnızca bir çevrim kullandığını ölçerdi.
    /// </para>
    /// </summary>
    private sealed class RecordingScopeResolver(IAccessScopeResolver inner) : IAccessScopeResolver
    {
        private readonly List<ClaimsPrincipal> seen = [];

        public IReadOnlyList<ClaimsPrincipal> Seen => seen;

        public AccessScope Resolve(ClaimsPrincipal? principal)
        {
            if (principal is not null)
            {
                seen.Add(principal);
            }

            return inner.Resolve(principal);
        }
    }

    /// <summary>
    /// Aldığı kapsamı hem <b>kaydeden</b> hem <b>döndüren</b> ürün aracı —
    /// M08'in öznesi.
    ///
    /// <para>
    /// <see cref="RequiresCallerIdentity"/> ezilmiyor: ürün yüzeyindeki
    /// varsayılan zaten kimlik istiyor ve ölçülmek istenen şey <b>o
    /// varsayılan</b>.
    /// </para>
    /// </summary>
    private sealed class ScopeEchoTool : BizigoMcpTool
    {
        public const string ToolIdentifier = "test.scope_echo";

        /// <summary>Araç gerçekten koştu mu — retin "koşmadan" olduğunun kanıtı.</summary>
        public bool Executed { get; private set; }

        /// <summary>Aracın eline geçen kapsam.</summary>
        public AccessScope? Observed { get; private set; }

        public override string ToolName => ToolIdentifier;

        public override McpSurface Surface => McpSurface.Product;

        public override string ToolTitle => "Kapsamı yankıla";

        public override string ToolDescription => "M08 kimlik kapısının öznesi.";

        public override JsonElement InputSchema { get; } = McpSchema.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        public override JsonElement OutputSchema { get; } = McpSchema.Parse(
            """
            {
              "type": "object",
              "properties": {
                "subject":      { "type": "string" },
                "owner_groups": { "type": "array", "items": { "type": "string" } },
                "unrestricted": { "type": "boolean" }
              },
              "required": ["subject", "owner_groups", "unrestricted"],
              "additionalProperties": false
            }
            """);

        protected override ValueTask<McpToolResult> ExecuteAsync(
            McpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Executed = true;
            Observed = invocation.Scope;

            return ValueTask.FromResult(McpToolResult.Structured(Shape(invocation.Scope)));
        }

        public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(McpToolResult.Structured(Shape(AccessScope.Denied)));
        }

        private static EchoPayload Shape(AccessScope scope) => new(
            scope.Subject,
            [.. scope.OwnerGroups.Order(StringComparer.Ordinal)],
            scope.IsUnrestricted);
    }

    /// <summary>Yankının tel sözleşmesi — anonim nesne değil (§8).</summary>
    private sealed record EchoPayload(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("owner_groups")] IReadOnlyList<string> OwnerGroups,
        [property: JsonPropertyName("unrestricted")] bool Unrestricted);
}
