using System.Reflection;
using System.Text.Json;

// Üretimin araç derlemesi beyanı (`McpEndpoints.ToolAssemblies`) burada:
// kapı, üretimin ilan ettiği kümeye bakmak zorunda.
using Bizigo.Api;
using Bizigo.Cli;
using Bizigo.Contracts.Security;
using Bizigo.Commands;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Mcp.Tools;
using Bizigo.Simulators.Mcp.Tools;
using Json.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>MCP uyum kapısı</b> (M01).
///
/// <para>
/// Uyum bir <b>iddia</b> değil bir <b>kapı</b>. Bu sınıfın her testi,
/// sunucunun teldeki hâlini gerçek bir istemciyle sorguluyor — araç listesini
/// koddan okumak yerine <c>tools/list</c> çağırıyor, örnek çıktıyı doğrudan
/// çağırmak yerine <c>tools/call</c>'dan alıyor. Aradaki fark bu kapının
/// varlık sebebi: serileştirme, anlaşma ve hata dönüşümü koşmadan sorulan soru
/// <i>"kodumuz kendi beklentimize uyuyor mu"</i> olurdu.
/// </para>
///
/// <para>
/// <b>Denetlenen küme keşfediliyor.</b> Elle yazılmış bir araç listesi yok:
/// hangi yüzeyler varsa (<see cref="McpSurface"/>) hepsi denetleniyor, her
/// yüzeyde sunucunun ilan ettiği <b>her</b> araç denetleniyor. Bu deponun
/// dördüncü kez ödediği ders — <c>Produces&lt;T&gt;</c> kapısı elle tutulan bir
/// listeden besleniyordu, 16 uç ona hiç görünmedi ve üç test de yeşil yandı.
/// </para>
///
/// <para>
/// <b>Kapı SDK'ya güvenmiyor.</b> Şema doğrulaması <c>JsonSchema.Net</c> ile
/// ayrıca yapılıyor; MCP SDK'sı kendi içinde de doğruluyor olabilir ama kapının
/// ona dayanması, ölçtüğü şeyi ölçen araca sormak olurdu. Bağımlılık protokolü
/// <i>konuşmak</i> için, kapıyı <i>kurmak</i> için değil.
/// </para>
/// </summary>
public sealed class McpComplianceTests
{
    /// <summary>Denetlenen yüzeyler — <b>beyan edilmiş olanların tamamı</b>.</summary>
    public static TheoryData<McpSurface> Surfaces()
    {
        var data = new TheoryData<McpSurface>();

        foreach (var surface in Enum.GetValues<McpSurface>().Where(static s => s is not McpSurface.Unspecified))
        {
            data.Add(surface);
        }

        return data;
    }

    /// <summary>
    /// Uyum kapısının kurduğu sunucunun K6 beyanı (M06).
    ///
    /// <para>
    /// <c>Internal</c> çünkü kapının ölçtüğü şey <b>üretimde koşan</b> kurulum
    /// ve üretimde ürün yüzeyi <c>Mcp:DataBoundary=Internal</c> ile kuruluyor.
    /// Beyanın kendisinin kapısı ayrı ölçülüyor — bkz.
    /// <see cref="Beyansiz_sunucu_kurulamiyor"/> ve
    /// <see cref="Kurum_disi_beyan_urun_yuzeyinde_reddediliyor"/>.
    /// </para>
    /// </summary>
    private static McpBoundaryDeclaration TestBoundary =>
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "uyum kapısı: birim testi");

    /// <summary>
    /// <b>Yüzeyin ÜRETİMDEKİ araç derlemeleri</b> (M03) — ve bu ayrım ölçülerek
    /// eklendi.
    ///
    /// <para>
    /// İlk hâli her iki yüzey için de <c>Bizigo.Api</c> veriyordu. Ürün yüzeyi
    /// için doğru: <c>POST /mcp</c> orada barınıyor. <b>Simülatör yüzeyi için
    /// yanlıştı</b> — <c>bizigo-sim</c>'in HTTP'si bilerek yok, tek yolu
    /// <c>bizigo mcp serve --surface bizigo-sim</c>, ve o komut kökü
    /// <c>Assembly.GetExecutingAssembly()</c> ile <b><c>Bizigo.Cli</c></b>
    /// veriyor.
    /// </para>
    ///
    /// <para>
    /// <b>Kusurun bedeli sessizlikti.</b> <c>Bizigo.Api</c>'nin
    /// <c>Bizigo.Simulators</c>'a hiç referansı yok (ölçüldü), dolayısıyla
    /// <c>McpToolDiscovery.ProductAssemblies</c> o derlemeye hiç ulaşmıyordu:
    /// yedi <c>sim.*</c> aracı kapıya <b>görünmüyor</b>, kapı simülatör yüzeyi
    /// için yine tek araç sayıyor ve <b>yeşil kalıyordu</b>. M04'ün ölçtüğü
    /// budama körlüğüyle aynı sonuç, başka bir sebeple: orada referans
    /// budanıyordu, burada kapı yanlış kökten bakıyordu.
    /// </para>
    ///
    /// <para>
    /// Bu, sınıfın kendi belgesindeki cümlenin gereği: <i>uyum kapısının
    /// ölçtüğü şeyin üretimde koşan şey olması, kapının anlamının tamamı.</i>
    /// </para>
    /// </summary>
        internal static IReadOnlyList<Assembly> DeclaredAssemblies(McpSurface surface) => surface switch
    {
        McpSurface.Product => McpEndpoints.ToolAssemblies,
        McpSurface.Simulator => Bizigo.Cli.McpCommandHandlers.ToolAssembliesFor(surface),
        _ => throw new ArgumentOutOfRangeException(
            nameof(surface),
            surface,
            "Beyan edilmemiş bir yüzeyin araç derlemesi yok. Yeni bir yüzey eklendiğinde "
            + "burası kırmızı yanıyor — ve yanması gerekiyor: beyansız bir yüzey, araçları hiç "
            + "bulunmayan ve bu yüzden SESSİZCE yeşil kalan bir kapı demek."),
    };

    private static McpServerOptions ProductionOptions(McpSurface surface, IServiceProvider services) =>
        BizigoMcpServer.CreateOptions(surface, TestBoundary, DeclaredAssemblies(surface), services);

    /// <summary>Testin kendi iptali; xUnit koşumu kesildiğinde çağrılar da kesiliyor.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Araç hatalarının <b>kapalı kümesi</b> — <c>McpToolError</c>'ın sabitleri.
    /// Elle yazılmıyor, oradan okunuyor: iki liste olsaydı yeni bir kod eklenip
    /// burası eski kalırdı ve kapı onu <i>"bilinmeyen"</i> sayardı.
    /// </summary>
    private static readonly string[] KnownErrorCodes =
    [
        McpToolError.InvalidArgument,
        McpToolError.NotFound,
        McpToolError.Unavailable,
        McpToolError.WrongSurface,
    ];

    // ---------------------------------------------------------------------
    // 1 · Keşif — kapı denetlediği kümeyi kendisi buluyor mu
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Kapının kendi bekçisi.</b> Yeni bir <see cref="BizigoMcpTool"/> alt
    /// sınıfı eklendiğinde keşif onu <b>söylenmeden</b> buluyor mu.
    ///
    /// <para>
    /// Ölçüm gerçek: <c>Bizigo.UnitTests</c> derlemesinde iki test aracı var
    /// (<see cref="TestOnlyTool"/>, <see cref="NeverEndingTool"/>) ve keşif
    /// onları hiçbir listeye yazılmadan buluyor. Bir gün keşif bozulursa bu
    /// test düşer — üretim kümesine bakan test ise bozulmayı fark etmez, çünkü
    /// ürün araçları elle de eklenmiş olabilir.
    /// </para>
    /// </summary>
    [Fact]
    public void Kesif_yeni_bir_araci_soylenmeden_buluyor()
    {
        var discovered = McpToolDiscovery.ToolTypes([typeof(McpComplianceTests).Assembly]);

        Assert.Contains(typeof(TestOnlyTool), discovered);
        Assert.Contains(typeof(NeverEndingTool), discovered);

        // Soyut taban keşfe girmemeli: girse "araç" sayılır ve örneklenmeye
        // çalışılırken patlardı.
        Assert.DoesNotContain(typeof(BizigoMcpTool), discovered);
    }

    /// <summary>
    /// <b>Keşif, yüzeyini yapıcıdan almayan aracı da kurabiliyor.</b>
    ///
    /// <para>
    /// M08 sırasında ölçüldü ve M01'in bir kusuruydu: <c>Instantiate</c>
    /// <c>surface</c>'i <b>koşulsuz</b> fazladan argüman olarak veriyordu ve
    /// <c>ActivatorUtilities</c> fazladan argümanı olan çağrıyı eşleştirmiyor
    /// (<i>"Also ensure no extraneous arguments are provided"</i>). Sonuç:
    /// yüzeyini yapıcıdan almayan <b>hiçbir</b> araç kurulamıyordu — ne
    /// parametresiz bir yapıcı, ne yalnızca <c>IScopedQuery</c> isteyen bir M04
    /// aracı.
    /// </para>
    ///
    /// <para>
    /// Kapının bugüne kadar sessiz kalma sebebi ölçüldü: üretimde tek araç
    /// (<c>server.info</c>) yüzeyini yapıcıdan alıyor, yani <b>tek örnek yanlış
    /// tarafı hiç göstermiyordu</b>. Test araçları da keşfe hiç verilmemişti.
    /// Bu test o boşluğu kapatıyor ve M04'ün ilk aracından önce kırmızı yanmayı
    /// üstleniyor.
    /// </para>
    ///
    /// <para>
    /// Kusurun asıl bedeli mesajdı: <c>Instantiate</c>'in <c>catch</c>'i bunu
    /// <i>"Bağımlılığı DI'ya kaydedilmemiş olabilir"</i> diye raporluyordu, yani
    /// sebebi <b>olmayan</b> bir yere işaret ediyordu.
    /// </para>
    /// </summary>
    [Fact]
    public void Kesif_yuzeyini_yapicidan_almayan_araci_da_kurabiliyor()
    {
        using var services = McpTestServices.Empty();

        var tools = McpToolDiscovery.Instantiate(
            [typeof(TestOnlyTool), typeof(ServerInfoTool)], McpSurface.Product, services);

        // İkisi de kuruldu: biri yüzeyini yapıcıdan alıyor, diğeri almıyor.
        Assert.Equal(
            [ServerInfoTool.ToolIdentifier, "test.only"],
            tools.Select(static t => t.ToolName).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>Bugün ilan edilen küme.</b> Elle yazılmış olan denetlenen küme değil
    /// <b>beklenen</b> küme: keşif bundan azını bulursa bir araç sessizce
    /// düşmüş, fazlasını bulursa yeni bir araç gelmiş ve buraya bilinçli olarak
    /// yazılması gerekiyor.
    ///
    /// <para>
    /// M03/M04/M05 araç eklerken bu satır büyüyecek — ve büyümesi <b>görünür</b>
    /// bir hareket olacak.
    /// </para>
    ///
    /// <para>
    /// <b>"T48 elle listeyi kaldırdı, bu neden duruyor?"</b> — soru haklı ve
    /// cevabı ikisinin <b>hangi tarafta</b> durduğu. T48'in kaldırdığı liste
    /// <c>Produces&lt;T&gt;</c> kapısının <b>denetlediği</b> kümeydi: listede
    /// olmayan uç kapıya hiç görünmüyordu, yani elle tutulan taraf kapıyı
    /// <b>kör</b> ediyordu. Burada denetlenen küme zaten türetiliyor —
    /// sunucunun <c>tools/list</c> yanıtından, canlı bir oturum üzerinden. Elle
    /// olan taraf <b>beklenti</b>.
    /// </para>
    ///
    /// <para>
    /// Ve beklenti tarafı <b>türetilemez</b>: "sunucunun ilan ettiği" kümeyi
    /// "kodda var olan"dan türetmek, sunucunun kullandığı yansımanın
    /// <b>aynısını</b> ikinci kez koşturmak olurdu — test kendini kendisiyle
    /// karşılaştırır ve <b>hiçbir zaman kırmızı yanamaz</b>. Bir tarafın insan
    /// eliyle yazılması, bu testin bir şey söyleyebilmesinin tek şartı.
    /// </para>
    ///
    /// <para>
    /// Aynı kalıp <c>ArchitectureTests.Kapsam_bekcisi_butun_kayit_uzantilarini_kendisi_buluyor</c>
    /// içinde de duruyor ve T48 onu da kaldırmadı — ölçüt <i>"elle liste var
    /// mı"</i> değil, <b>"elle liste kapının gözü mü, yoksa kapının beyanı
    /// mı"</b>. Gözse kaldırılır; beyansa kalır.
    ///
    /// <para>
    /// <b>Küme artık yüzeye göre.</b> M04'ün okuma araçları yalnızca
    /// <c>bizigo</c>'da; <c>bizigo-sim</c> ürün verisine dokunmuyor. Tek bir
    /// beklenen küme yazmak, ürün araçlarının simülatör yüzeyine sızmasını
    /// <b>ölçülmez</b> kılardı (K6).
    /// </para>
    ///
    /// <para>
    /// <b>Bu test M04'te bir kez ÖLÇÜLEREK kırmızı yandı ve sebebi bir kusurdu:</b>
    /// beş araç yazılıp <c>ProjectReference</c> eklendiğinde bile keşif onları
    /// bulamıyordu, çünkü derleyici kullanılmayan referansı meta veriden
    /// düşürüyor. Ölçüm ve düzeltme <c>BizigoReadToolsSetup</c> belgesinde.
    /// Kapının <b>yeşilliği bir şey ifade etsin</b> diye bu satırın elle
    /// tutulması tam olarak bu yüzden değerli.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Sunucunun_ilan_ettigi_araclar(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        var tools = await session.Client.ListToolsAsync(cancellationToken: Ct);

        // M02: yedi komut aracı eklendi ve bu satır BÜYÜDÜ. Listenin elle
        // taşınması bilinçli — yeni bir araç eklemek burayı da değiştirmeyi
        // gerektiriyor, yani ilan edilen küme kimse karar vermeden büyüyemiyor.
        //
        // `bizigo-sim` yüzeyinde komut araçları YOK: hepsi `McpSurface.Product`
        // ve simülatörün kendi araçları M03'ün.
        string[] expected = surface == McpSurface.Product
            ?
            [
                .. CommandCatalog.Tools.Select(static c => c.Name).Append(ServerInfoTool.ToolIdentifier)
                    .Order(StringComparer.Ordinal),
            ]
            : [ServerInfoTool.ToolIdentifier];

        Assert.Equal(
            McpExpectedTools.For(surface),
            tools.Select(static t => t.Name).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// <b>Yüzey başına beklenen araç kümesi</b> — elle yazılı ve öyle kalmalı.
    ///
    /// <para>
    /// Denetlenen küme keşfediliyor; <b>beklenen</b> küme burada duruyor. Keşif
    /// bundan azını bulursa bir araç sessizce düşmüş, fazlasını bulursa yeni bir
    /// araç gelmiş ve buraya <b>bilinçli olarak</b> yazılması gerekiyor.
    /// </para>
    ///
    /// <para>
    /// M03 bu listeyi yedi satır büyüttü ve büyümesi görünür bir hareket oldu.
    /// M04/M05 ürün yüzeyini büyütecek.
    /// </para>
    /// </summary>
    private static string[] Expected(McpSurface surface) => surface switch
    {
        McpSurface.Product => [ServerInfoTool.ToolIdentifier],

        McpSurface.Simulator =>
        [
            ServerInfoTool.ToolIdentifier,
            DeviceSilenceTool.ToolIdentifier,
            FleetListTool.ToolIdentifier,
            ScenarioListTool.ToolIdentifier,
            ScenarioSetTool.ToolIdentifier,
            StateTool.ToolIdentifier,
            SyslogBurstTool.ToolIdentifier,
            WebhookEmitTool.ToolIdentifier,
        ],

        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "Beyan edilmemiş yüzey."),
    };

    /// <summary>
    /// Kapı <b>boş bir kümeyi</b> sessizce onaylamıyor.
    ///
    /// <para>
    /// Ayrı bir test, çünkü kaybı ayrı bir hata: sıfır araçlı bir sunucuda
    /// aşağıdaki şema testleri sıfır şema doğrular ve <b>hepsi yeşil</b> yanar.
    /// Bu deponun adını koyduğu hata sınıfı — yeşilliği hiçbir şey ifade
    /// etmeyen bir bekçi.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Her_yuzey_en_az_bir_arac_ilan_ediyor(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        Assert.NotEmpty(await session.Client.ListToolsAsync(cancellationToken: Ct));
    }

    // ---------------------------------------------------------------------
    // 2 · Şema geçerliliği ve örnek çağrı
    // ---------------------------------------------------------------------

    /// <summary>
    /// İlan edilen <b>her</b> aracın girdi ve çıktı şeması geçerli bir JSON
    /// Schema (2020-12) mi.
    ///
    /// <para>
    /// <c>outputSchema</c> <b>zorunlu</b>: şemasız bir araç, modelin çıktıyı
    /// tahmin etmesi demek — ve MCP'de tahmin doğrudan modelin bağlamına giren
    /// bir yanlış.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Her_aracin_semasi_gecerli(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        foreach (var tool in await session.Client.ListToolsAsync(cancellationToken: Ct))
        {
            var declared = tool.ProtocolTool;

            AssertValidSchema(declared.Name, "inputSchema", declared.InputSchema);

            Assert.True(
                declared.OutputSchema is not null,
                $"`{declared.Name}` bir `outputSchema` ilan etmiyor. Şemasız bir araç, modelin "
                + "çıktıyı tahmin etmesi demek.");

            AssertValidSchema(declared.Name, "outputSchema", declared.OutputSchema!.Value);
        }
    }

    /// <summary>
    /// İlan edilen <b>her</b> aracın örnek çağrısı <c>outputSchema</c>'ya
    /// uyuyor mu — ve argümansız çağrılabilenler ayrıca <b>protokolden</b>
    /// geçiyor mu.
    ///
    /// <para>
    /// <b>İlk hâli her araca boş argümanlı bir <c>tools/call</c> yapıyordu ve
    /// bu, kapıyı zorunlu argümanı olan HİÇBİR aracın geçemeyeceği hâle
    /// getiriyordu.</b> <c>sim.scenario.set</c> <c>device</c> ve
    /// <c>scenario</c> istiyor; boş argümanla <c>invalid_argument</c> dönüyor
    /// ve kapı, aracın <b>doğru</b> davranışı yüzünden kırmızı yanıyordu.
    /// Aynı duvar M04'ün <c>logs.search</c>'ünü ve M05'in araçlarını da
    /// bekliyordu.
    /// </para>
    ///
    /// <para>
    /// <b>Bugüne kadar sessiz kalma sebebi ölçüldü ve tanıdık:</b> üretimdeki
    /// tek araç (<c>server.info</c>) argümansız, yani <b>tek örnek yanlış
    /// tarafı hiç göstermiyordu</b> — <c>Instantiate</c>'in koşulsuz
    /// <c>surface</c> kusurunun aynı sınıfı, aynı sebeple.
    /// </para>
    ///
    /// <para>
    /// <b>Ve belge ile davranış ayrışmıştı.</b> <c>BizigoMcpTool.SampleAsync</c>
    /// kendi belgesinde <i>"uyum kapısının koşturduğu örnek çağrı"</i> diye
    /// tanımlanıyor, ama kapı onu şema uyumu için <b>hiç çağırmıyordu</b>
    /// (yalnızca iptal testinde kullanılıyordu). Yani bir üyenin varlık sebebi
    /// olarak yazılan cümle doğru değildi — §7'nin belge hâli.
    /// </para>
    ///
    /// <para>
    /// <b>Şimdi ölçülen şey:</b> <c>SampleAsync</c> çıktısı, gerçek tel
    /// dönüşümünden (<c>ToProtocol</c>) geçirilip şemaya karşı doğrulanıyor.
    /// Kapının asıl kazancı olan <c>structuredContent</c> dönüşümü
    /// <b>korunuyor</b>; kaybedilen tek şey soket, ve <c>SampleAsync</c> zaten
    /// bilerek yan etkisiz (<i>"şart değil: altyapıya bağlanmak"</i>) — bu
    /// yüzden mutasyon araçlarının örneği durum dosyasına dokunmuyor.
    /// </para>
    ///
    /// <para>
    /// ⚠ <b>M02 İLE KESİŞİYOR — birleşmede M02'nin bölmesi kazanmalı.</b> M02
    /// aynı duvarı kendi dalında ikiye bölerek çözdü: <i>telde</i> eksik
    /// argüman iyi biçimli bir araç hatası mı, <i>süreç içinde</i>
    /// <c>SampleAsync</c> çıktısı şemaya uyuyor mu. Aşağıdaki iddia ikincisinin
    /// aynısı; buradaki hâli M02 birleşene kadar bu dalın yeşil kalabilmesi
    /// için duruyor. Birleşmede ikinci kopya olarak <b>düşmeli</b> (§9).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Ornek_cagri_cikti_semasina_uyuyor(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        var options = ProductionOptions(surface, services);

        // OTURUM KİMLİKLİ. M04'ten itibaren ürün araçları kimlik istiyor (M08)
        // ve kimliksiz bir oturumda bu kapı şemayı değil `unauthenticated`
        // retini ölçerdi. Gerekçenin tamamı `McpTestSession.StartAsync`
        // belgesinde; kısası: kapıyı `SampleAsync`'e çevirmek serileştirmeyi ve
        // `structuredContent` dönüşümünü ölçümden düşürürdü — M01'in kapıyı
        // protokolden geçirme kararının tam tersi.
        await using var session = await McpTestSession.StartAsync(
            options, services, user: McpTestServices.ComplianceIdentity, cancellationToken: Ct);

        var declaredTools = await session.Client.ListToolsAsync(cancellationToken: Ct);
        var serverTools = BizigoMcpServer.Tools(surface, DeclaredAssemblies(surface), services);

        Assert.NotEmpty(declaredTools);

        // İlan edilen küme ile sunucu tarafındaki örnekler AYNI kümeyi
        // adlandırmalı. Ayrışırlarsa aşağıdaki döngü bazı araçları sessizce
        // atlardı — kapının kendi kör noktası.
        Assert.Equal(
            declaredTools.Select(static t => t.Name).Order(StringComparer.Ordinal),
            serverTools.Select(static t => t.ToolName).Order(StringComparer.Ordinal));

        var samples = serverTools.ToDictionary(static t => t.ToolName, StringComparer.Ordinal);

        foreach (var tool in declaredTools)
        {
            var schema = JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText());

            // (1) HER ARAÇ — örnek, gerçek tel dönüşümünden geçiyor.
            var sample = await samples[tool.Name].SampleAsync(Ct);

            Assert.False(
                sample.IsError,
                $"`{tool.Name}` örneği hata döndürdü: {sample.Payload.GetRawText()}. Örnek, aracın "
                + "başarılı çıktısının şeklini göstermeli — hata hâlinin şemasını değil.");

            var wire = BizigoMcpTool.ToProtocol(sample);

            // Beklenen şey BAŞARI DEĞİL: argümansız çağrı çoğu araçta meşru
            // olarak düşüyor. Ölçülen şey düşüşün BİÇİMİ — kapalı kümeden bir
            // araç hatası mı, yoksa protokolü patlatan bir istisna mı. İkincisi
            // istemciye ürün hakkında hiçbir şey söylemeyen bir arıza verirdi.
            Assert.True(
                wire.StructuredContent is not null,
                $"`{tool.Name}` `outputSchema` ilan ediyor ama örneği `structuredContent` "
                + "üretmiyor — yani şema hiçbir şeyi tarif etmiyor.");

            AssertMatchesSchema(tool.Name, "SampleAsync", schema, wire.StructuredContent!.Value);
        }
    }

    private static void AssertMatchesSchema(
        string toolName,
        string origin,
        JsonSchema schema,
        JsonElement payload)
    {
        var evaluation = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(
            evaluation.IsValid,
            $"`{toolName}` çıktısı ({origin}) kendi `outputSchema`'sına UYMUYOR.\n"
            + $"Çıktı: {payload.GetRawText()}\n"
            + $"Hatalar: {Errors(evaluation)}");
    }

    // ---------------------------------------------------------------------
    // 3 · Revizyon — "en güncel" bir hedef değil bir kayma
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Yazdığımız revizyon gerçekten konuşuluyor mu.</b>
    ///
    /// <para>
    /// <see cref="McpRevision.Supported"/> bir <b>hedef</b>; sunucu ona uyduğunu
    /// <c>server.info</c> ile de söylüyor. Bu test o cümlenin doğru olduğunu
    /// ölçüyor: o revizyonu isteyen bir istemci el sıkışmayı tamamlayabiliyor.
    /// </para>
    ///
    /// <para>
    /// <b>Ve bu, kaymayı engelleyen mekanizmanın kendisi.</b> SDK bir gün
    /// <c>2026-07-28</c>'i bırakırsa burası <b>kırmızı yanıyor</b> ve sabiti
    /// yükseltmek bilinçli bir hareket oluyor — sessizce değil. <i>"En güncel"</i>
    /// bir hedef değil bir kaymadır; yazılı sabit + ölçüm ikisi birlikte onu
    /// hedefe çeviriyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yazili_revizyon_el_sikismada_kabul_ediliyor()
    {
        await using var services = McpTestServices.ForDiscoveredTools();

        var clientOptions = new McpClientOptions { ProtocolVersion = McpRevision.Supported };

        await using var session = await McpTestSession.StartAsync(
            ProductionOptions(McpSurface.Product, services), services, clientOptions, cancellationToken: Ct);

        // El sıkışma gerçekten oldu: istemci sunucunun kimliğini biliyor.
        Assert.Equal(McpSurfaces.ProductName, session.Client.ServerInfo?.Name);

        Assert.NotEmpty(await session.Client.ListToolsAsync(cancellationToken: Ct));
    }

    /// <summary>
    /// <b>Anlaşma gerçekten anlaşma mı</b> — eski bir istemci bağlanabiliyor mu.
    ///
    /// <para>
    /// Yalnızca hedefi sınayan bir test, hedefin doğru olduğunu gösterirdi;
    /// <b>anlaşmanın çalıştığını</b> değil. Ayrım burada teorik değil: ilk
    /// yazılan hâlde sunucu <c>ProtocolVersion</c>'ı <see cref="McpRevision.Supported"/>'a
    /// SABİTLİYORDU ve bu test kırmızı yandı — <c>2025-11-25</c> konuşan istemci
    /// el sıkışmada reddediliyordu.
    /// </para>
    ///
    /// <para>
    /// Yani bir çivi gibi görünen satır, planın §2'sinde <b>şart</b> olan sürüm
    /// anlaşmasını kapatıyordu. Bu test o farkı tutuyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Anlasma_eski_istemciyi_indiriyor()
    {
        await using var services = McpTestServices.ForDiscoveredTools();

        var clientOptions = new McpClientOptions { ProtocolVersion = McpRevision.PreviousStable };

        await using var session = await McpTestSession.StartAsync(
            ProductionOptions(McpSurface.Product, services), services, clientOptions, cancellationToken: Ct);

        Assert.NotEmpty(await session.Client.ListToolsAsync(cancellationToken: Ct));
    }

    /// <summary>
    /// <c>server.info</c>'nun söylediği revizyon, yazılı sabitle aynı.
    ///
    /// <para>
    /// Ayrı bir test çünkü ayrı bir kayıp: sabit doğru olabilir ama araç eski
    /// bir değeri raporluyor olabilir, ve o zaman istemci <b>yanlış bir cevap</b>
    /// alır — hata yok, belirti yok (§7).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Server_info_yazili_revizyonu_soyluyor(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        var result = await session.Client.CallToolAsync(
            ServerInfoTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        var payload = result.StructuredContent!.Value;

        Assert.Equal(McpRevision.Supported, payload.GetProperty("protocol_revision").GetString());
        Assert.Equal(McpSurfaces.WireName(surface), payload.GetProperty("surface").GetString());
    }

    // ---------------------------------------------------------------------
    // 4 · Yetenek anlaşması
    // ---------------------------------------------------------------------

    /// <summary>
    /// Desteklenmeyen bir yetenek <b>ilan edilmiyor</b> — ve ilan edilmeyen
    /// yeteneğin yolu kapalı.
    ///
    /// <para>
    /// Bugün yalnızca araçlar var; kaynaklar M07'de, istemler daha sonra.
    /// Olmayan bir yeteneği ilan etmek istemciye olmayan bir yol göstermek
    /// demek — ve o yolu deneyen istemci bir <b>protokol</b> hatası alıp bunu
    /// bağlantı arızası sanıyor.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Desteklenmeyen_yetenek_ilan_edilmiyor(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        var capabilities = session.Client.ServerCapabilities;

        Assert.NotNull(capabilities.Tools);
        Assert.Null(capabilities.Resources);
        Assert.Null(capabilities.Prompts);
        Assert.Null(capabilities.Completions);

        // `Logging` BİLEREK sınanmıyor. Çivilediğimiz revizyonda (2026-07-28)
        // günlükleme yeteneği ARTIK KULLANIMDAN KALDIRILDI (SEP-2577) ve SDK
        // onu okuyan kodu `MCP9005` ile işaretliyor — `TreatWarningsAsErrors`
        // altında derlemeyi kıran bir uyarı.
        //
        // Bu, planın §2'siyle bir AYRIŞMA: orada günlükleme bir şart olarak
        // yazılı. Ayrışma koordinatöre bildirildi; kararı ona ait, ve o karar
        // verilene kadar burada sessizce bir iddia yazmıyoruz.
    }

    // ---------------------------------------------------------------------
    // 5 · Hata ayrımı
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Araç hatası protokol hatası değil.</b>
    ///
    /// <para>
    /// Karıştırılırsa istemci bir iş hatasını bağlantı arızası sanar: yeniden
    /// dener, bekler, sunucuyu suçlar — ve ürünün cevabı kaybolur. Ölçüm iki
    /// yönlü: <b>araç</b> hatası istisna fırlatmıyor, <b>protokol</b> hatası
    /// fırlatıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Arac_hatasi_ile_protokol_hatasi_ayri()
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        var options = ProductionOptions(McpSurface.Product, services);

        // Argümanı zorunlu bir araç ekleniyor: hata yolunu ölçmek için hata
        // ÜRETEBİLEN bir özne gerekiyor.
        options.ToolCollection!.Add(new ArgumentDemandingTool());

        await using var session = await McpTestSession.StartAsync(options, services, cancellationToken: Ct);

        // (a) ARAÇ hatası — başarılı bir yanıtın içinde `isError`.
        var toolFailure = await session.Client.CallToolAsync(
            ArgumentDemandingTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.True(
            toolFailure.IsError is true,
            "Eksik argüman bir İŞ hatası; `isError` ile dönmeliydi.");

        // (b) PROTOKOL hatası — istisna.
        var protocolFailure = await Assert.ThrowsAnyAsync<McpException>(
            async () => await session.Client.CallToolAsync(
                "boyle.bir.arac.yok", new Dictionary<string, object?>(), cancellationToken: Ct));

        Assert.NotNull(protocolFailure);
    }

    // ---------------------------------------------------------------------
    // 6 · İptal
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>notifications/cancelled</c> <b>gerçekten</b> iptal ediyor mu.
    ///
    /// <para>
    /// Ürünün sorguları ClickHouse'a iniyor ve uzun sürebiliyor. İptal
    /// edilmeyen bir sorgu kaynak sızdırır ve istemci <i>"iptal ettim"</i>
    /// sanır — sessiz yanlış davranışın protokol katmanındaki hâli.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçülen şey bir zaman aşımı değil.</b> Araç
    /// <c>Task.Delay(Infinite)</c> ile bekliyor, yani yalnızca iptal onu
    /// döndürebiliyor; ve <c>ObservedCancellation</c> belirtecin <b>uca</b>
    /// ulaştığını söylüyor. Süreye bakan bir test "iptal çalıştı" ile "süre
    /// doldu"yu ayırt edemezdi.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Iptal_bildirimi_araci_gercekten_iptal_ediyor()
    {
        await using var services = McpTestServices.ForDiscoveredTools();
        var options = ProductionOptions(McpSurface.Product, services);

        var tool = new NeverEndingTool();
        options.ToolCollection!.Add(tool);

        await using var session = await McpTestSession.StartAsync(options, services, cancellationToken: Ct);

        // İstek KİMLİĞİ elle veriliyor, çünkü iptal bildirimi onu taşıyor.
        // `CallToolAsync` kimliği kendi seçiyor ve dışarı vermiyor.
        var requestId = new RequestId("m01-iptal-olcumu");

        var call = session.Client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
            RequestMethods.ToolsCall,
            new CallToolRequestParams { Name = tool.ToolName },
            McpJsonUtilities.DefaultOptions,
            requestId,
            Ct).AsTask();

        // Sunucu iptal edilen isteğe yanıt dönmüyor (spesifikasyon böyle
        // diyor), yani bu görev askıda kalıp oturumla birlikte düşüyor.
        // İstisnası gözlemsiz kalmasın.
        _ = call.ContinueWith(static finished => finished.Exception, TaskScheduler.Default);

        // Araç GERÇEKTEN koşmaya başlasın: iptali başlamadan göndermek,
        // hiç başlamamış bir işi "iptal ettik" diye ölçmek olurdu.
        await tool.Started.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // ÖLÇÜLEN ŞEY BU: teldeki bildirimin kendisi.
        //
        // İlk hâli istemcinin `CancellationToken`'ını iptal ediyordu ve test
        // kırmızı yandı — ÖLÇÜLDÜ: SDK istemcisi belirteç iptal edildiğinde
        // `notifications/cancelled` GÖNDERMİYOR, yalnızca yerel beklemeyi
        // bırakıyor. O hâliyle test "istemci bekle­meyi bıraktı" diyordu ve
        // sunucu hakkında hiçbir şey söylemiyordu — tam olarak sızıntının
        // görünmediği hâl.
        //
        // Bildirimi doğrudan göndermek soruyu doğru yere soruyor: bu ticket
        // SUNUCUYU yazıyor.
        await session.Client.SendNotificationAsync(
            NotificationMethods.CancelledNotification,
            new CancelledNotificationParams { RequestId = requestId },
            McpJsonUtilities.DefaultOptions,
            Ct);

        await WaitUntil(() => tool.ObservedCancellation, TimeSpan.FromSeconds(30));

        Assert.True(
            tool.ObservedCancellation,
            "`notifications/cancelled` geldi ama araç koşmaya devam etti — iptal edilmeyen "
            + "bir ClickHouse sorgusu tam olarak böyle sızar.");
    }

    /// <summary>
    /// <b>Her araç iptali gözetiyor mu.</b>
    ///
    /// <para>
    /// Yukarıdaki test bir aracı ölçüyor; bu test <b>hepsini</b>. İptal
    /// edilmiş bir belirteçle çağrılan araç <c>OperationCanceledException</c>
    /// fırlatmak zorunda — yani belirteci gerçekten taşıyor.
    /// </para>
    ///
    /// <para>
    /// Bu, planın <i>"iptal gerçekten iptal etsin"</i> şartının ClickHouse
    /// gerektirmeyen ve <b>gelecekteki her araca</b> uygulanan hâli: M04'ün
    /// <c>logs.search</c>'ü belirteci yutarsa burada kırmızı yanıyor, canlı bir
    /// sorgu beklemeye gerek kalmadan.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Her_arac_iptal_edilmis_belirteci_gozetiyor(McpSurface surface)
    {
        await using var services = McpTestServices.ForDiscoveredTools();

        var tools = BizigoMcpServer.Tools(surface, DeclaredAssemblies(surface), services);

        Assert.NotEmpty(tools);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        foreach (var tool in tools)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await tool.SampleAsync(cancelled.Token));
        }
    }

    // ---------------------------------------------------------------------
    // 7 · İki taşıma
    // ---------------------------------------------------------------------

    /// <summary>
    /// stdio taşıması, bu sınıfın ölçtüğü akış taşımasının <b>ta kendisi</b>.
    ///
    /// <para>
    /// Testler <c>StreamServerTransport</c> üzerinden koşuyor ve
    /// <c>StdioServerTransport</c> ondan türüyor — yani ölçülen kod yolu
    /// stdio'nunkiyle aynı, fark yalnızca akışların nereden geldiği. Bu ilişki
    /// <b>varsayılmıyor</b>: SDK bir gün stdio'yu ayrı bir uygulamaya
    /// taşırsa burası kırmızı yanıyor ve "stdio ölçüldü" iddiası dayanağını
    /// kaybettiğinde haber veriyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Stdio_tasimasi_olculen_akis_tasimasindan_turuyor() =>
        Assert.Equal(typeof(StreamServerTransport), typeof(StdioServerTransport).BaseType);

    // ---------------------------------------------------------------------
    // 8 · Redaksiyon kapısı (M06 — menteşe kapıya çevrildi)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Log metni sonuca yalnızca redaksiyon kapısından giriyor</b> ve tele
    /// <b>maskelenmiş</b> hâliyle iniyor.
    ///
    /// <para>
    /// M01 bu testi bir <i>menteşenin çalıştığını</i> ölçmek için yazmıştı:
    /// taşıyıcı serbest bir <c>string</c> alıyordu ve ürün tarafında çağıranı
    /// yoktu. M06 fabrikanın parametre tipini <see cref="RedactedPrompt"/>
    /// yaptı; bu test artık menteşeyi değil <b>kapıyı</b> ölçüyor.
    /// </para>
    ///
    /// <para>
    /// <b>Ölçülen şey bir tip kaydı değil, davranış:</b> girdi gerçek bir sır
    /// taşıyor ve tele inen metinde <b>o sır yok</b>. Kapının varlığını
    /// derleyici tutuyor; burada tutulan şey kapının <i>çalıştığı</i> — imza
    /// doğru ama gövde <c>redacted.Text</c> yerine ham metni taşısaydı
    /// derleme yeşil kalırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Log_metni_yalnizca_redaksiyon_kapisindan_giriyor()
    {
        const string Sir = "AbcDef0123456789XyzQwertyUiop";
        var redacted = RedactedPrompt.Redact($"set password {Sir}");

        var result = McpToolResult.Structured(new { ok = true }).WithLogText(redacted);

        var wire = BizigoMcpTool.ToProtocol(result);

        var metinler = wire.Content.OfType<TextContentBlock>().Select(static b => b.Text).ToArray();

        Assert.Contains(metinler, text => string.Equals(text, redacted.Text, StringComparison.Ordinal));

        Assert.DoesNotContain(
            metinler,
            text => text.Contains(Sir, StringComparison.Ordinal));

        // Log metni EKLENMEDEN sonuç üretmek hâlâ mümkün — kapı "her sonuç log
        // taşımalı" demiyor, "log taşıyan her sonuç kapıdan geçmiş olmalı"
        // diyor.
        Assert.Empty(McpToolResult.Structured(new { ok = true }).LogText);
    }

    /// <summary>
    /// <b>Yüzey beyanı zorunlu.</b> <c>Unspecified</c> bir varsayılan değil bir
    /// ret: yüzeyini yazmayan bir araç sessizce <b>ürün verisi döndüren</b>
    /// kümeye düşerdi. Kalıp T42'nin <c>ModelEndpoint</c>'inden.
    /// </summary>
    [Fact]
    public void Yuzeyini_beyan_etmeyen_arac_reddediliyor()
    {
        var tool = new SurfacelessTool();

        var error = Assert.Throws<InvalidOperationException>(() => tool.ProtocolTool);

        Assert.Contains("Unspecified", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 9 · K6 — sunucunun ağ sınırı beyanı (M06, bitti tanımı 6)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Beyansız bir sunucu kurulamıyor.</b>
    ///
    /// <para>
    /// Ret <see cref="BizigoMcpServer.Apply"/>'nin bir dalı DEĞİL, beyan
    /// tipinin varoluş şartı: <see cref="McpBoundaryDeclaration"/> yalnızca
    /// <see cref="McpBoundaryDeclaration.Declare"/>'den çıkıyor ve o
    /// <see cref="DataBoundary.Unspecified"/>'ı kabul etmiyor. Yani beyansız
    /// bir sunucu <b>ifade edilemiyor</b>.
    /// </para>
    /// </summary>
    [Fact]
    public void Beyansiz_sunucu_kurulamiyor()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => McpBoundaryDeclaration.Declare(DataBoundary.Unspecified, "test"));

        // Ölçüt "fırlattı mı" DEĞİL, "ne yazması gerektiğini söyledi mi".
        //
        // Bu satır bir kırmızı ölçümünden doğdu. İlk hâli `Assert.Contains
        // ("Unspecified", ...)` idi ve kusur ölçümünde YEŞİL KALDI: adanmış
        // dalı kapattığımda alttaki "bilinmeyen değer" dalı devraldı ve onun
        // mesajı da `Unspecified` sözcüğünü taşıyordu. Yani test iki farklı
        // reddi ayırt edemiyordu — ve ayırt edemediği şey operatörün
        // gördüğü metin, yani kapının TEK ÇIKTISI. Beyansız bir sunucunun
        // reddedilmesi ile o reddin ne yapılacağını söylemesi ayrı şeyler.
        Assert.Contains("K6", error.Message, StringComparison.Ordinal);
        Assert.Contains("`internal`", error.Message, StringComparison.Ordinal);
        Assert.Contains("`external`", error.Message, StringComparison.Ordinal);

        // Sıfır değeri bilerek geçersiz — T42'den devralınan kalıp ve
        // `DataBoundary` belgesinde yazılı.
        Assert.Equal(0, (int)DataBoundary.Unspecified);
    }

    /// <summary>
    /// <b>Gerekçesiz beyan da kurulamıyor.</b>
    ///
    /// <para>
    /// <see cref="McpBoundaryDeclaration.Basis"/> boş olamıyor çünkü
    /// <see cref="DataBoundary.Internal"/> iki farklı garanti gücüyle
    /// dolaşabiliyor: T42'nin kapısında <b>adrese karşı doğrulanmış</b>,
    /// burada <b>yalnızca beyan edilmiş</b>. Gerekçesiz bir <c>Internal</c>,
    /// okuyanı birinciyi varsaymaya iter.
    /// </para>
    /// </summary>
    [Fact]
    public void Gerekcesiz_beyan_kurulamiyor()
    {
        Assert.Throws<ArgumentException>(
            () => McpBoundaryDeclaration.Declare(DataBoundary.Internal, "   "));

        Assert.Throws<ArgumentException>(
            () => McpBoundaryDeclaration.Declare(DataBoundary.Internal, null!));
    }

    /// <summary>
    /// <b>Kurum dışı beyan ürün yüzeyinde reddediliyor</b> — K6.
    ///
    /// <para>
    /// <c>bizigo</c> log içeriği döndürüyor; onu kurum dışı bir istemciye açmak
    /// K6'nın birebir ihlali ve hiçbir içerik düzeyi için esnemiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kurum_disi_beyan_urun_yuzeyinde_reddediliyor()
    {
        var declaration = McpBoundaryDeclaration.Declare(DataBoundary.External, "test");

        var error = Assert.Throws<InvalidOperationException>(
            () => McpBoundaryGate.Require(declaration, McpSurface.Product));

        Assert.Contains(McpSurfaces.ProductName, error.Message, StringComparison.Ordinal);
        Assert.Contains("K6", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Simülatör yüzeyi kurum dışı beyanla geçiyor</b> — ve bu bir boşluk
    /// değil, iki yüzeyin ayrı olmasının sebebi.
    ///
    /// <para>
    /// <c>bizigo-sim</c> ürün verisi değil simülatör durumu döndürüyor;
    /// kurum dışı bir istemciye açılması K6'yı ihlal etmiyor.
    /// </para>
    ///
    /// <para>
    /// <b>Kapsam beyanı:</b> bu yol bugün ürün kurulumunda <b>ulaşılamaz</b> —
    /// <c>BizigoMcpSetup</c> yalnızca <see cref="McpSurface.Product"/> kuruyor
    /// ve <c>bizigo-sim</c>'in HTTP yüzeyi M03'ün kararı (bugünkü yönü
    /// stdio-only). Yani bu testin ölçtüğü şey bugün <b>yalnızca burada</b>
    /// görülüyor; M03 gerçek bir yol açtığında orada da ölçülmeli.
    /// </para>
    /// </summary>
    [Fact]
    public void Simulator_yuzeyi_kurum_disi_beyanla_gecebiliyor()
    {
        var declaration = McpBoundaryDeclaration.Declare(DataBoundary.External, "test");

        McpBoundaryGate.Require(declaration, McpSurface.Simulator);
    }

    /// <summary>
    /// <b>Kapı sunucu kurulumunun İÇİNDE duruyor</b>, yanında değil.
    ///
    /// <para>
    /// Yukarıdaki üç test <see cref="McpBoundaryGate"/>'i doğrudan çağırıyor ve
    /// bu tek başına yanıltıcı olurdu: kapı doğru cevap veriyor olabilir ve
    /// <see cref="BizigoMcpServer.Apply"/> onu hiç çağırmıyor olabilir. Bu
    /// deponun T50'de ölçtüğü ayrım — <i>var olmak ile bağlı olmak</i>.
    /// </para>
    /// </summary>
    [Fact]
    public void Kurum_disi_beyan_sunucu_kurulumunda_reddediliyor()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var declaration = McpBoundaryDeclaration.Declare(DataBoundary.External, "test");

        Assert.Throws<InvalidOperationException>(() => BizigoMcpServer.CreateOptions(
            McpSurface.Product, declaration, McpEndpoints.ToolAssemblies, services));
    }

    /// <summary>
    /// <b>HTTP tarafının beyanı yapılandırmadan geliyor ve eksikliği ret.</b>
    ///
    /// <para>
    /// Üç hâl ayrı ayrı ölçülüyor çünkü üçü ayrı arama yaptırıyor: anahtar yok,
    /// anahtar var ama çözümlenemiyor, anahtar doğru.
    /// </para>
    /// </summary>
    [Fact]
    public void Yapilandirmadaki_beyan_okunuyor()
    {
        Assert.Throws<InvalidOperationException>(
            () => BizigoMcpSetup.ReadBoundary(new ConfigurationBuilder().Build()));

        Assert.Throws<InvalidOperationException>(
            () => BizigoMcpSetup.ReadBoundary(Yapilandirma("iç-ağ")));

        var okunan = BizigoMcpSetup.ReadBoundary(Yapilandirma("Internal"));

        Assert.Equal(DataBoundary.Internal, okunan.Boundary);
        Assert.Contains(BizigoMcpSetup.DataBoundaryKey, okunan.Basis, StringComparison.Ordinal);

        // `Unspecified` yazılı bir değer de reddediliyor: yazmak ile beyan
        // etmek aynı şey değil.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BizigoMcpSetup.ReadBoundary(Yapilandirma("Unspecified")));
    }

    /// <summary>
    /// <b>Üretimin kendi yapılandırması kapıdan geçiyor.</b>
    ///
    /// <para>
    /// Yukarıdaki test kapının çalıştığını ölçüyor; bu, <c>appsettings.json</c>
    /// içindeki <b>gerçek</b> değerin o kapıdan geçtiğini ölçüyor. İkisi ayrı:
    /// kapı kusursuz olup üretim yapılandırması eksik olabilirdi ve o hâlde
    /// MCP hiç ayağa kalkmazdı — <b>koşum anında</b> keşfedilecek bir kusur.
    /// </para>
    /// </summary>
    [Fact]
    public void Uretim_yapilandirmasi_siniri_beyan_ediyor()
    {
        var path = Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Api", "appsettings.json");

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        var declaration = BizigoMcpSetup.ReadBoundary(configuration);

        Assert.Equal(DataBoundary.Internal, declaration.Boundary);

        // Ve o beyan ürün yüzeyinde gerçekten geçiyor.
        McpBoundaryGate.Require(declaration, McpSurface.Product);
    }

    /// <summary>
    /// <b>stdio tarafı da beyansız koşmuyor</b> — ve bu ayrı bir kapı.
    ///
    /// <para>
    /// HTTP'nin beyanı yapılandırmadan, stdio'nunki CLI seçeneğinden geliyor;
    /// ikisi <b>ayrı yollar</b> ve birinin kapısı diğerini kapatmıyor.
    /// <c>bizigo mcp serve</c> beyansız çağrıldığında sunucu <b>hiç
    /// başlamıyor</b>, çıkış kodu 2.
    /// </para>
    ///
    /// <para>
    /// <b>Test sunucuyu ayağa kaldırmıyor</b> ve kaldıramaz: reddedilen her yol
    /// <c>McpStdioHost.RunAsync</c>'e ulaşmadan dönüyor. Kabul edilen bir yolu
    /// buradan çağırmak süreç kapanana kadar bloke olurdu — o yüzden bu test
    /// yalnızca <b>ret</b> hâllerini ölçüyor ve bunu kapsam olarak beyan
    /// ediyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(McpSurfaces.ProductName, null)]
    [InlineData(McpSurfaces.ProductName, "")]
    [InlineData(McpSurfaces.ProductName, "Unspecified")]
    [InlineData(McpSurfaces.ProductName, "iç-ağ")]
    [InlineData(McpSurfaces.ProductName, "External")]
    public async Task Stdio_beyansiz_ya_da_kurum_disi_kosmuyor(string surface, string? boundary)
    {
        var kod = await McpCommandHandlers.ServeAsync(surface, boundary, verbose: false, Ct);

        Assert.Equal(2, kod);
    }

    private static IConfiguration Yapilandirma(string boundary) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BizigoMcpSetup.DataBoundaryKey] = boundary,
            })
            .Build();

    // ---------------------------------------------------------------------
    // Yardımcılar
    // ---------------------------------------------------------------------

    private static void AssertValidSchema(string toolName, string kind, JsonElement schema)
    {
        var evaluation = MetaSchemas.Draft202012.Evaluate(
            schema, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(
            evaluation.IsValid,
            $"`{toolName}` aracının `{kind}` şeması geçerli bir JSON Schema (2020-12) değil.\n"
            + $"Şema: {schema.GetRawText()}\n"
            + $"Hatalar: {Errors(evaluation)}");
    }

    private static string Errors(EvaluationResults results) =>
        string.Join(
            "; ",
            (results.Details ?? [])
                .Where(static d => d.Errors is { Count: > 0 })
                .SelectMany(static d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));

    private static string Describe(CallToolResult result) =>
        string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(static b => b.Text));

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget)
    {
        // Duvar saati bir BÜTÇE, bir ölçüt değil: koşul sağlandığı anda
        // dönüyoruz. Yüklü makinede yavaşlamak testi düşürmüyor, yalnızca
        // koşulun hiç sağlanmaması düşürüyor.
        var deadline = DateTime.UtcNow + budget;

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Zorunlu argüman isteyen araç — hata yolunun öznesi.</summary>
    private sealed class ArgumentDemandingTool : ProtocolMechanicsTool
    {
        public const string ToolIdentifier = "test.needs_argument";

        public override string ToolName => ToolIdentifier;

        public override McpSurface Surface => McpSurface.Product;

        public override string ToolTitle => "Argüman isteyen";

        public override string ToolDescription => "Hata ayrımının öznesi.";

        public override JsonElement InputSchema { get; } = McpSchema.Parse(
            """
            {
              "type": "object",
              "properties": { "kaynak": { "type": "string" } },
              "required": ["kaynak"],
              "additionalProperties": false
            }
            """);

        public override JsonElement OutputSchema { get; } = McpSchema.Parse(
            """
            {
              "type": "object",
              "properties": { "kaynak": { "type": "string" } },
              "required": ["kaynak"],
              "additionalProperties": false
            }
            """);

        protected override ValueTask<McpToolResult> ExecuteAsync(
            McpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(
                McpToolResult.Structured(new { kaynak = invocation.Required<string>("kaynak") }));
        }

        public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(McpToolResult.Structured(new { kaynak = "ornek" }));
        }
    }

    /// <summary>Yüzeyini beyan etmeyen araç — beyan kapısının öznesi.</summary>
    private sealed class SurfacelessTool : BizigoMcpTool
    {
        public override string ToolName => "test.surfaceless";

        public override McpSurface Surface => McpSurface.Unspecified;

        public override string ToolTitle => "Yüzeysiz";

        public override string ToolDescription => "Beyan kapısının öznesi.";

        public override JsonElement InputSchema { get; } = McpSchema.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        public override JsonElement OutputSchema { get; } = McpSchema.Parse(
            """{ "type": "object", "properties": {}, "additionalProperties": false }""");

        protected override ValueTask<McpToolResult> ExecuteAsync(
            McpToolInvocation invocation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(McpToolResult.Structured(new { }));

        public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(McpToolResult.Structured(new { }));
    }
}
