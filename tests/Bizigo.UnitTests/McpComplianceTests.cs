using System.Text.Json;
using Bizigo.Mcp;
using Bizigo.Mcp.Tools;
using Json.Schema;
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

    private static McpServerOptions ProductionOptions(McpSurface surface, IServiceProvider services) =>
        BizigoMcpServer.CreateOptions(surface, typeof(global::Program).Assembly, services);

    /// <summary>Testin kendi iptali; xUnit koşumu kesildiğinde çağrılar da kesiliyor.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    /// <b>Bugün ilan edilen küme.</b> Elle yazılmış olan denetlenen küme değil
    /// <b>beklenen</b> küme: keşif bundan azını bulursa bir araç sessizce
    /// düşmüş, fazlasını bulursa yeni bir araç gelmiş ve buraya bilinçli olarak
    /// yazılması gerekiyor.
    ///
    /// <para>
    /// M03/M04/M05 araç eklerken bu satır büyüyecek — ve büyümesi <b>görünür</b>
    /// bir hareket olacak.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Sunucunun_ilan_ettigi_araclar(McpSurface surface)
    {
        await using var services = McpTestServices.Empty();
        await using var session = await McpTestSession.StartAsync(ProductionOptions(surface, services), services, cancellationToken: Ct);

        var tools = await session.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            [ServerInfoTool.ToolIdentifier],
            tools.Select(static t => t.Name).Order(StringComparer.Ordinal).ToArray());
    }

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
        await using var services = McpTestServices.Empty();
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
        await using var services = McpTestServices.Empty();
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
    /// İlan edilen <b>her</b> aracın örnek çağrısı, <c>tools/call</c>'dan
    /// dönen yapısal çıktıyla <c>outputSchema</c>'ya uyuyor mu.
    ///
    /// <para>
    /// Örnek doğrudan <c>SampleAsync</c>'ten okunmuyor: çağrı <b>protokolden</b>
    /// geçiyor. Aradaki fark serileştirme ve <c>structuredContent</c>
    /// dönüşümü — yani şemanın karşılaştırıldığı şey istemcinin gerçekten
    /// gördüğü gövde.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Surfaces))]
    public async Task Ornek_cagri_cikti_semasina_uyuyor(McpSurface surface)
    {
        await using var services = McpTestServices.Empty();
        var options = ProductionOptions(surface, services);
        await using var session = await McpTestSession.StartAsync(options, services, cancellationToken: Ct);

        var declaredTools = await session.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.NotEmpty(declaredTools);

        foreach (var tool in declaredTools)
        {
            var schema = JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText());

            var result = await session.Client.CallToolAsync(
                tool.Name, new Dictionary<string, object?>(), cancellationToken: Ct);

            Assert.True(
                result.IsError is not true,
                $"`{tool.Name}` örnek çağrısı hata döndürdü: {Describe(result)}");

            Assert.True(
                result.StructuredContent is not null,
                $"`{tool.Name}` `outputSchema` ilan ediyor ama `structuredContent` döndürmüyor — "
                + "yani şema hiçbir şeyi tarif etmiyor.");

            var payload = result.StructuredContent!.Value;
            var evaluation = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });

            Assert.True(
                evaluation.IsValid,
                $"`{tool.Name}` çıktısı kendi `outputSchema`'sına UYMUYOR.\n"
                + $"Çıktı: {payload.GetRawText()}\n"
                + $"Hatalar: {Errors(evaluation)}");
        }
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
        await using var services = McpTestServices.Empty();

        var clientOptions = new McpClientOptions { ProtocolVersion = McpRevision.Supported };

        await using var session = await McpTestSession.StartAsync(
            ProductionOptions(McpSurface.Product, services), services, clientOptions, Ct);

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
        await using var services = McpTestServices.Empty();

        var clientOptions = new McpClientOptions { ProtocolVersion = McpRevision.PreviousStable };

        await using var session = await McpTestSession.StartAsync(
            ProductionOptions(McpSurface.Product, services), services, clientOptions, Ct);

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
        await using var services = McpTestServices.Empty();
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
        await using var services = McpTestServices.Empty();
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
        await using var services = McpTestServices.Empty();
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
        await using var services = McpTestServices.Empty();
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
        await using var services = McpTestServices.Empty();

        var tools = BizigoMcpServer.Tools(surface, typeof(global::Program).Assembly, services);

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
    // 8 · Menteşe (M06'nın takılacağı yer)
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Serbest <c>string</c> dönüşü yok</b> — ve log metni taşıyıcısının
    /// üretim yolu <b>boş</b>.
    ///
    /// <para>
    /// İki şeyi birden ölçüyor, çünkü ikisi ayrı ayrı yanıltıcı:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <see cref="McpToolResult"/> yalnızca yapısal yük ve
    /// <see cref="McpLogText"/> kabul ediyor. Bu <b>derleme zamanında</b>
    /// zorunlu; test yalnızca kaydı tutuyor.
    /// </item>
    /// <item>
    /// <see cref="McpLogText"/> üretmenin tek yolu <c>internal</c> bir fabrika
    /// ve bugün ürün tarafında <b>çağıranı yok</b>. Menteşe boş duruyor;
    /// M06 fabrikanın parametre tipini <c>RedactedPrompt</c> yapacak ve o gün
    /// log içeriği döndüren her yol derleyicide redaksiyon kapısına bağlanacak.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// Menteşenin <b>çalıştığı</b> ölçülüyor: taşıyıcı sonuca gerçekten
    /// ekleniyor ve tele iniyor. Ölçülmeseydi M06 boş bir yere takılırdı.
    /// </para>
    /// </summary>
    [Fact]
    public void Log_metni_yalnizca_mentese_uzerinden_giriyor()
    {
        var log = McpLogText.FromRedacted("[maskelenmiş]");
        var result = McpToolResult.Structured(new { ok = true }).WithLogText(log);

        var wire = BizigoMcpTool.ToProtocol(result);

        Assert.Contains(wire.Content, block => block is TextContentBlock { Text: "[maskelenmiş]" });

        // Ürün tarafında bugün ÇAĞIRANI YOK. Menteşe bilerek boş; gerekçesi
        // `McpLogText` belgesinde.
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
    private sealed class ArgumentDemandingTool : BizigoMcpTool
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
