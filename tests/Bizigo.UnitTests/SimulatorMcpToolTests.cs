using System.Text.Json;
using Bizigo.Contracts.Security;
using Bizigo.Mcp;
using Bizigo.Simulators;
using Bizigo.Simulators.Mcp;
using Bizigo.Simulators.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>bizigo-sim</c> yüzeyinin bekçileri</b> (M03).
///
/// <para>
/// <b>Çağrılar TELDEN geçiyor</b>, <c>ExecuteAsync</c> doğrudan çağrılmıyor.
/// Ölçülen şey istemcinin gerçekten aldığı gövde: hata <b>kodu</b>
/// <c>structuredContent</c> içinde ayrıştırılabilir hâlde mi, yoksa yalnızca
/// metinde mi. Bu ayrım M01'in kararının kendisi — <i>"mesaj insana, kod
/// makineye"</i> — ve doğrudan çağrı onu hiç ölçmezdi.
/// </para>
///
/// <para>
/// <b>Durum dosyası her testte AYRI ve geçici.</b> Gerçek
/// <c>artifacts/bizigo-sim/state.json</c>'a yazsaydık iki şey birden bozulurdu:
/// testi koşturmak geliştiricinin simülatör durumunu değiştirirdi, ve paralel
/// koşumda testler birbirinin durumunu ezerdi — düşüş de kodda aranırdı.
/// </para>
/// </summary>
public sealed class SimulatorMcpToolTests
{
    /// <summary>
    /// Config yüzeyi olmayan cihaz — <b>yanlış yüzey ölçümünün öznesi</b>.
    /// <c>lb-web-01</c> bir nginx ve config'ini bu ürün çekmiyor; profilinde
    /// <c>config:</c> bloğu yok (ölçüldü).
    /// </summary>
    private const string SyslogOnlyDevice = "lb-web-01";

    /// <summary>Hem config hem syslog taklit eden cihaz.</summary>
    private const string BothSurfacesDevice = "fw-ankara-01";

    /// <summary>Config yüzeyini değiştiren gerçek bir senaryo.</summary>
    private const string ConfigScenario = "kural-eklendi";

    /// <summary>Syslog yüzeyini değiştiren gerçek bir senaryo.</summary>
    private const string SyslogScenario = "saat-kaymasi";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------
    // 1 · Bitti tanımının 8. maddesi — hata YÜZEYİ söylüyor
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Yanlış yüzeye uygulanmış senaryo → <c>wrong_surface</c>, ve mesaj
    /// yüzeyi adlandırıyor.</b>
    ///
    /// <para>
    /// Bitti tanımının 8. maddesi. <c>not_found</c> dönmek, S04'ün düzelttiği
    /// yanlış cümleyi (<i>"profilde böyle bir senaryo yok"</i>) geri getirirdi
    /// ve okuyan kişi arızayı <b>profil dosyasında</b> arardı — bu depoda bir
    /// teşhis turunu tümden harcamış bir sınıf.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yanlis_yuzeye_uygulanan_senaryo_yuzeyi_soyluyor()
    {
        await using var fixture = SimulatorFixture.Create();

        var error = await fixture.CallExpectingErrorAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = ConfigScenario });

        Assert.Equal(McpToolError.WrongSurface, error.Code);

        // Mesaj YÜZEYİ adlandırıyor, profili değil.
        Assert.Contains("config", error.Message, StringComparison.Ordinal);
        Assert.Contains("syslog", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("profilde", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>Mesajın KAYNAĞI <c>Scenarios.Reject</c>.</b>
    ///
    /// <para>
    /// Yalnızca <i>"mesaj 'config' geçiyor"</i> demek yetmezdi: MCP katmanında
    /// elle yazılmış yeni bir dizge de o iddiayı geçerdi ve <b>ayrıştığı gün
    /// sessiz kalırdı</b> — S04 motorun cümlesini düzeltir, MCP eski cümleyi
    /// döndürmeye devam eder, ve SSH yolundan bakan kimse fark etmezdi (§9:
    /// ikinci kopya yazma).
    /// </para>
    ///
    /// <para>
    /// Bu iddia birebir eşitlik istiyor, yani mesaj <b>taşınmak</b> zorunda.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yanlis_yuzey_mesaji_motorun_cumlesinin_ta_kendisi()
    {
        await using var fixture = SimulatorFixture.Create();

        var error = await fixture.CallExpectingErrorAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = ConfigScenario });

        Assert.Equal(Scenarios.Reject(ConfigScenario, ScenarioSurface.Syslog), error.Message);
    }

    /// <summary>
    /// <b>TERS YÖN: var olmayan senaryo → <c>not_found</c>.</b>
    ///
    /// <para>
    /// <b>Bu test olmadan yukarıdaki ölçüm işe yaramaz.</b> <i>"Her şeye
    /// <c>wrong_surface</c> de"</i> diyen bir uygulama tek yönlü testi
    /// <b>geçerdi</b> ve kapı yeşil kalırdı — bu deponun T48'in kontrol
    /// satırında ve T50'nin A/B çiftinde iki kez öğrendiği şey.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Var_olmayan_senaryo_wrong_surface_degil_not_found()
    {
        await using var fixture = SimulatorFixture.Create();

        var error = await fixture.CallExpectingErrorAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = "boyle-bir-senaryo-yok" });

        Assert.Equal(McpToolError.NotFound, error.Code);
        Assert.NotEqual(McpToolError.WrongSurface, error.Code);

        // Ve mesaj bilinenleri sayıyor: adı yanlış yazılmış bir çağrı,
        // doğrusunu görmeden düzeltilemez.
        Assert.Contains(SyslogScenario, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Altyapı senaryosu simülatörün elinde değil</b> ve mesaj bunu
    /// söylüyor: koordinatör servisi durduruyor.
    /// </summary>
    [Fact]
    public async Task Altyapi_senaryosu_wrong_surface_ve_koordinatoru_isaret_ediyor()
    {
        await using var fixture = SimulatorFixture.Create();

        var error = await fixture.CallExpectingErrorAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = "sidecar-yok" });

        Assert.Equal(McpToolError.WrongSurface, error.Code);
        Assert.Contains("altyapı", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 2 · Dördüncü hâl — `Scenarios.Reject`'in bilmediği ret
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Config yüzeyi çalışırken değiştirilemiyor → <c>unavailable</c>, ve
    /// mesaj NE YAPILACAĞINI söylüyor.</b>
    ///
    /// <para>
    /// <c>wrong_surface</c> <b>olmamalı</b>: senaryo doğru yüzeyde ve cihaz o
    /// yüzeyi taklit ediyor. Yanlış olan yüzey değil, bu katmanın erişimi —
    /// senaryo <c>ssh-sim</c> container'ında açılışta sabitleniyor.
    /// </para>
    ///
    /// <para>
    /// Ve <b>başarılı</b> da olmamalı: niyeti kaydedip <i>"set edildi"</i>
    /// demek, yürürlükte olmayan bir senaryoyu uygulanmış göstermek olurdu —
    /// sessiz-yanlış sınıfının simülatörün kendisinde doğması.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Config_yuzeyi_calisirken_degistirilemiyor_ve_cozumu_soyluyor()
    {
        await using var fixture = SimulatorFixture.Create();

        var error = await fixture.CallExpectingErrorAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = BothSurfacesDevice, ["scenario"] = ConfigScenario });

        Assert.Equal(McpToolError.Unavailable, error.Code);
        Assert.NotEqual(McpToolError.WrongSurface, error.Code);

        // ÇÖZÜM MESAJIN İÇİNDE: doğru yüzeyi işaret etmek yetmiyor, kapıyı
        // nerede açacağını da söylemeli.
        Assert.Contains($"SIM_SCENARIO={ConfigScenario}", error.Message, StringComparison.Ordinal);
        Assert.Contains("yeniden yaratılmalı", error.Message, StringComparison.Ordinal);

        // Ve HİÇBİR ŞEY YAZILMADI: reddedilen bir istek duruma sızmamalı.
        Assert.Null(fixture.State.Read().For(BothSurfacesDevice).Scenario);
    }

    // ---------------------------------------------------------------------
    // 3 · Niyet ile etki ayrı — durum katmanının taşıyıcı kararı
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Senaryo ayarlamak niyeti kaydediyor, etkiyi değil.</b>
    ///
    /// <para>
    /// <c>scenario_set_at</c> dolu, <c>last_applied_at</c> <b>boş</b>. Tek bir
    /// <c>updated_at</c> alanı olsaydı bu iki hâl aynı kutuya düşerdi ve okuyan
    /// ikincisini varsayardı — cihaz hâlâ eski davranıştayken <i>"saat kaydırıyor"</i>
    /// diye okunurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Senaryo_ayarlamak_niyeti_kaydediyor_etkiyi_degil()
    {
        await using var fixture = SimulatorFixture.Create();

        var payload = await fixture.CallExpectingSuccessAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = SyslogScenario });

        Assert.Equal(SyslogScenario, payload.GetProperty("scenario").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("last_applied_at").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, payload.GetProperty("scenario_set_at").ValueKind);

        var stored = fixture.State.Read().For(SyslogOnlyDevice);

        Assert.Equal(SyslogScenario, stored.Scenario);
        Assert.Equal(SimulatorFixture.Moment, stored.ScenarioSetAt);
        Assert.Null(stored.LastAppliedAt);
    }

    /// <summary>
    /// <b><c>sim.state</c> ayrışmayı SESSİZ bırakmıyor.</b>
    ///
    /// <para>
    /// İstenmiş ama hiç uygulanmamış bir senaryo, aracın <c>divergences</c>
    /// listesinde adıyla duruyor. Yalnızca iki damgayı yan yana basmak
    /// yetmezdi: okuyanın farkı kendi görmesi gerekirdi, ve bu <b>hatırlamaya
    /// dayanan bir mekanizma</b> olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sim_state_istendi_ama_uygulanmadi_halini_soyluyor()
    {
        await using var fixture = SimulatorFixture.Create();

        await fixture.CallExpectingSuccessAsync(
            ScenarioSetTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["scenario"] = SyslogScenario });

        var payload = await fixture.CallExpectingSuccessAsync(StateTool.ToolIdentifier, []);

        var divergences = payload.GetProperty("divergences")
            .EnumerateArray()
            .Select(entry => entry.GetString() ?? string.Empty)
            .ToArray();

        Assert.Contains(divergences, entry =>
            entry.Contains(SyslogOnlyDevice, StringComparison.Ordinal)
            && entry.Contains("hiç uygulanmadı", StringComparison.Ordinal));

        // Ve aracın vaadi yanıtın İÇİNDE: bu, filonun gerçeği değil niyet.
        Assert.Equal(StateTool.IntentNotTruth, payload.GetProperty("note").GetString());
    }

    // ---------------------------------------------------------------------
    // 4 · Susturma bir bayrak değil bir davranış
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Susturulmuş cihaz basmayı reddediyor.</b>
    ///
    /// <para>
    /// <c>sim.device.silence</c> bir bayrak yazıyor; onu bir <b>davranışa</b>
    /// çeviren yer <c>sim.syslog.burst</c>. Bayrağı yazıp orada okumamak,
    /// susturmayı <i>"kaydedilmiş ama etkisiz"</i> yapardı — <c>sim.state</c>
    /// <i>"susturuldu"</i> derken cihaz basmaya devam ederdi ve sessizlik
    /// korelasyonu ölçümü <b>yanlış sebeple</b> düşerdi.
    /// </para>
    ///
    /// <para>
    /// Ret <c>unavailable</c>: cihaz var, yüzeyi doğru, yalnızca şu an
    /// susturulmuş. Test soket açmıyor — ret basımdan <b>önce</b> veriliyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Susturulmus_cihaz_basmiyor()
    {
        await using var fixture = SimulatorFixture.Create();

        var silenced = await fixture.CallExpectingSuccessAsync(
            DeviceSilenceTool.ToolIdentifier, new() { ["device"] = SyslogOnlyDevice });

        Assert.True(silenced.GetProperty("silenced").GetBoolean());

        var error = await fixture.CallExpectingErrorAsync(
            SyslogBurstTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["count"] = 1 });

        Assert.Equal(McpToolError.Unavailable, error.Code);
        Assert.Contains("susturulmuş", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Susturma kaldırılabiliyor ve damga <b>temizleniyor</b>: açık bir cihazın
    /// yanında duran eski bir <i>"susturuldu"</i> damgası, hangisinin geçerli
    /// olduğunu belirsiz yapardı.
    /// </summary>
    [Fact]
    public async Task Susturma_kaldirilinca_damga_temizleniyor()
    {
        await using var fixture = SimulatorFixture.Create();

        await fixture.CallExpectingSuccessAsync(
            DeviceSilenceTool.ToolIdentifier, new() { ["device"] = SyslogOnlyDevice });

        var resumed = await fixture.CallExpectingSuccessAsync(
            DeviceSilenceTool.ToolIdentifier,
            new() { ["device"] = SyslogOnlyDevice, ["silenced"] = false });

        Assert.False(resumed.GetProperty("silenced").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resumed.GetProperty("silenced_at").ValueKind);
    }

    // ---------------------------------------------------------------------
    // 5 · Yüzey beyanı ve kimlik
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>Yedi aracın hepsi <c>Simulator</c> beyan ediyor ve kimlik
    /// İSTEMİYOR.</b>
    ///
    /// <para>
    /// İkinci yarı gerekli: <c>RequiresCallerIdentity</c> ürün yüzeyinde
    /// varsayılan <see langword="true"/>, ve bir <c>sim.*</c> aracı yanlışlıkla
    /// <c>Product</c> beyan etseydi stdio'da <b>hiç koşmadan</b>
    /// <c>unauthenticated</c> dönerdi — yani ilk belirti <i>"araç çalışmıyor"</i>
    /// olurdu, sebebi değil.
    /// </para>
    ///
    /// <para>
    /// Kimliksiz mutasyon kararının <b>gerekçesi</b> <c>SimulatorTool</c>
    /// belgesinde yazılı; burada ölçülen şey kararın gerçekten yürürlükte
    /// olduğu.
    /// </para>
    /// </summary>
    [Fact]
    public void Butun_sim_araclari_simulator_yuzeyi_ve_kimliksiz()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var tools = BizigoMcpServer.Tools(
            McpSurface.Simulator, Bizigo.Cli.McpCommandHandlers.ToolAssembliesFor(McpSurface.Simulator), services);

        var sim = tools.Where(tool => tool.ToolName.StartsWith("sim.", StringComparison.Ordinal)).ToArray();

        Assert.Equal(7, sim.Length);
        Assert.All(sim, tool => Assert.Equal(McpSurface.Simulator, tool.Surface));
        Assert.All(sim, tool => Assert.False(
            tool.RequiresCallerIdentity,
            $"`{tool.ToolName}` kimlik istiyor. stdio'da kimlik YOK — bu araç hiç koşmadan "
            + "`unauthenticated` dönerdi."));
    }

    /// <summary>
    /// <b>Durum değiştiren araçlar bunu ilan ediyor.</b>
    ///
    /// <para>
    /// <c>ReadOnlyHint</c> istemcinin onay isteyip istemeyeceğine bakıyor;
    /// yanlış ilan, bir ajanın filoyu <b>sormadan</b> değiştirmesi demek.
    /// </para>
    /// </summary>
    [Fact]
    public void Mutasyon_araclari_salt_okunur_ilan_etmiyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var tools = BizigoMcpServer
            .Tools(McpSurface.Simulator, Bizigo.Cli.McpCommandHandlers.ToolAssembliesFor(McpSurface.Simulator), services)
            .ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);

        Assert.False(tools[ScenarioSetTool.ToolIdentifier].IsReadOnly);
        Assert.False(tools[DeviceSilenceTool.ToolIdentifier].IsReadOnly);
        Assert.False(tools[SyslogBurstTool.ToolIdentifier].IsReadOnly);
        Assert.False(tools[WebhookEmitTool.ToolIdentifier].IsReadOnly);

        Assert.True(tools[FleetListTool.ToolIdentifier].IsReadOnly);
        Assert.True(tools[ScenarioListTool.ToolIdentifier].IsReadOnly);
        Assert.True(tools[StateTool.ToolIdentifier].IsReadOnly);
    }

    /// <summary>
    /// <b>Hiçbir <c>sim.*</c> aracı cihaz metni döndürmüyor</b> — ticket'ın 3.
    /// kabul kriterinin bu koldaki cevabı, bir <b>ölçüm</b> olarak.
    ///
    /// <para>
    /// Kriter kapının neye baktığının yazılı olmasını istiyor; yazı
    /// <c>SyslogBurstTool</c> belgesinde. Ama yazı tek başına bir gün eskir —
    /// bu test, cevabın <b>hâlâ doğru</b> olduğunu tutuyor: örnek çıktıların
    /// hiçbirinde bir syslog satırı yok.
    /// </para>
    ///
    /// <para>
    /// M06 ölçtü ki yapısal kanal da redaksiyon kapısını atlıyor
    /// (<c>Structured&lt;T&gt;</c> içindeki <c>string</c> alanlar modele
    /// kapıdan geçmeden iniyor). O hâlde bu yüzeyin güvencesi kapı değil
    /// <b>içerik</b>: taşınan metin yok.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Hicbir_sim_araci_cihaz_metni_dondurmuyor()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var tools = BizigoMcpServer.Tools(
            McpSurface.Simulator, Bizigo.Cli.McpCommandHandlers.ToolAssembliesFor(McpSurface.Simulator), services);

        // Gerçek bir syslog örneğinin ayırt edici parçası: öncelik göstergesi
        // ve tesis adı. Örnek çıktıların hiçbirinde bulunmamalı.
        foreach (var tool in tools)
        {
            var sample = await tool.SampleAsync(Ct);
            var text = sample.Payload.GetRawText();

            Assert.DoesNotContain("<1", text, StringComparison.Ordinal);
            Assert.Empty(sample.LogText);
        }
    }

    // ---------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------

    /// <summary>
    /// Gerçek katalog + <b>geçici</b> durum dosyası + sabit saat.
    ///
    /// <para>
    /// Katalog gerçek çünkü ölçülen şey araçların <b>gerçek filoyla</b> nasıl
    /// davrandığı; durum geçici çünkü testin yan etkisi olmamalı; saat sabit
    /// çünkü iddialar damgalara bakıyor ve <c>UtcNow</c> onları duvar saatine
    /// bağlardı (§6).
    /// </para>
    /// </summary>
    private sealed class SimulatorFixture : IAsyncDisposable
    {
        public static readonly DateTimeOffset Moment = new(2026, 8, 18, 9, 19, 47, TimeSpan.Zero);

        private readonly ServiceProvider services;
        private readonly McpServerOptions options;
        private readonly string directory;

        private SimulatorFixture(ServiceProvider services, McpServerOptions options, string directory)
        {
            this.services = services;
            this.options = options;
            this.directory = directory;
            State = services.GetRequiredService<SimulatorStateStore>();
        }

        public SimulatorStateStore State { get; }

        public static SimulatorFixture Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"bizigo-sim-{Guid.NewGuid():N}");

            var provider = new ServiceCollection()
                .AddBizigoSimulatorTools(
                    repositoryRoot: RepositoryLayout.Root,
                    statePath: Path.Combine(directory, "state.json"),
                    clock: new FakeTimeProvider(Moment))
                .BuildServiceProvider();

            // Kök CLI derlemesi — üretimde `bizigo mcp serve --surface bizigo-sim`
            // tam olarak burayı veriyor.
            var options = BizigoMcpServer.CreateOptions(
                McpSurface.Simulator,
                McpBoundaryDeclaration.Declare(DataBoundary.Internal, "sim araç testi"),
                Bizigo.Cli.McpCommandHandlers.ToolAssembliesFor(McpSurface.Simulator),
                provider);

            return new SimulatorFixture(provider, options, directory);
        }

        /// <summary>Başarılı bir çağrının yapısal yükü.</summary>
        public async Task<JsonElement> CallExpectingSuccessAsync(
            string tool,
            Dictionary<string, object?> arguments)
        {
            var result = await CallAsync(tool, arguments);

            Assert.True(
                result.IsError is not true,
                $"`{tool}` beklenmedik şekilde hata döndürdü: {result.StructuredContent?.GetRawText()}");

            return result.StructuredContent!.Value;
        }

        /// <summary>
        /// Hata dönen bir çağrının <b>ayrıştırılmış</b> hatası.
        ///
        /// <para>
        /// Hata gövdesi <c>structuredContent</c>'ten okunuyor, metinden değil:
        /// M01'in kararı <i>"mesaj insana, kod makineye"</i> ve istemcinin
        /// dallanabildiği tek yer kod. Metinden okusaydık, kodu hiç
        /// döndürmeyen bir uygulama testi geçerdi.
        /// </para>
        /// </summary>
        public async Task<McpToolError> CallExpectingErrorAsync(
            string tool,
            Dictionary<string, object?> arguments)
        {
            var result = await CallAsync(tool, arguments);

            Assert.True(
                result.IsError is true,
                $"`{tool}` hata döndürmesi gerekirken başarılı oldu: "
                + $"{result.StructuredContent?.GetRawText()}");

            var error = result.StructuredContent!.Value.GetProperty("error");

            return new McpToolError(
                error.GetProperty("code").GetString()!,
                error.GetProperty("message").GetString()!);
        }

        private async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(
            string tool,
            Dictionary<string, object?> arguments)
        {
            await using var session = await McpTestSession.StartAsync(
                options, services, cancellationToken: Ct);

            return await session.Client.CallToolAsync(tool, arguments, cancellationToken: Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();

            // §3: başlattığın her şeyi topla — bir dizin de bir kalıntı.
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
