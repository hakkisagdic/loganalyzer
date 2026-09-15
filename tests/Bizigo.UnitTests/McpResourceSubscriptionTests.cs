using System.IO.Pipelines;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Contracts.Security;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Resources;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>M07 · Aboneliğin ölçümü — ilan, onay, teslim ve sızıntı.</b>
///
/// <para>
/// Abonelik bu üründe dört ayrı iddia ve <b>dördü ayrı ayrı yanlış olabilir</b>:
/// yetenek ilan edildi mi, sunucu aboneliği <b>onayladı</b> mı, bildirim
/// <b>ulaştı</b> mı, ve abone gittiğinde kayıt <b>kapandı</b> mı. Tek bir
/// "abonelik çalışıyor" testi bunların hangisinin tuttuğunu söyleyemez.
/// </para>
///
/// <para>
/// <b>Ham JSON-RPC kullanılıyor ve sebebi ölçüldü:</b> çivilediğimiz revizyon
/// (<c>2026-07-28</c>, SEP-2575) <c>resources/subscribe</c>'ı <b>kaldırdı</b> ve
/// yerine <c>subscriptions/listen</c> + <c>resourceSubscriptions</c> koydu. SDK'nın
/// <b>sunucu</b> tarafı bunu uyguluyor ama <b>istemci</b> tarafı uygulamıyor:
/// <c>McpClient.SubscribeToResourceAsync</c> hâlâ kaldırılmış RPC'yi çağırıyor ve
/// sunucu onu göç ipucuyla reddediyor. Yani zinciri SDK istemcisiyle ölçmek
/// <b>mümkün değil</b>; ölçüm ham mesajla yapılıyor.
/// </para>
/// </summary>
public sealed class McpResourceSubscriptionTests
{
    private const string ListenIstegi = """
        {"jsonrpc":"2.0","id":1,"method":"subscriptions/listen","params":{"notifications":{"resourceSubscriptions":["bizigo://rca-runs"]},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"olcum","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
        """;

    /// <summary>
    /// Abonelik filtresinin kapsamı çözebilmesi için gereken kimlik. İçeriği
    /// önemsiz: uyum kapısının çözücüsü sabit (<c>network/core</c>); ölçülen şey
    /// kimliğin <b>var olması</b>.
    /// </summary>
    private static readonly ClaimsPrincipal Kimlik = new(
        new ClaimsIdentity([new Claim("sub", "abonelik-olcumu")], authenticationType: "olcum"));

    private static readonly McpBoundaryDeclaration Boundary =
        McpBoundaryDeclaration.Declare(DataBoundary.Internal, "abonelik ölçümü: bellek içi");

    /// <summary>
    /// <b>Abonelik yalnızca <c>rca-runs</c> kaynağında.</b>
    ///
    /// <para>
    /// Hepsini açmak, ilan edilip bildirimi hiç gönderilmeyen bir yetenek
    /// bırakırdı: kanıt paketi ve RCA raporu <b>değişmiyor</b> (bir kez yazılıp
    /// okunuyorlar), parser tanımı ise operatör olayı. Küme <b>harfi harfine</b>
    /// yazılı — sabitlerden türetilseydi bir kaynağın bayrağı değiştiğinde test
    /// kendisiyle birlikte değişir ve <b>hiç kırmızı yanamazdı</b> (M07'nin
    /// birinci turunda tam bu kusur ölçüldü).
    /// </para>
    /// </summary>
    [Fact]
    public void Abonelik_destekleyen_tek_kaynak_var()
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var abone = BizigoMcpServer
            .Resources(McpSurface.Product, McpComplianceTests.DeclaredAssemblies(McpSurface.Product), services)
            .Where(static resource => resource.SupportsSubscription)
            .Select(static resource => resource.Kind)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["rca-runs"], abone);
    }

    /// <summary>
    /// <b>Taşıma gönderemiyorsa yetenek ilan edilmiyor.</b>
    ///
    /// <para>
    /// İki yön birden ölçülüyor, çünkü tek yön yanıltıcı: yalnızca "kapalıyken
    /// kapalı" ölçülse her zaman kapalı bir bayrak da geçerdi.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Yetenek_tasimanin_gonderebilmesine_bagli(bool deliverable, bool beklenen)
    {
        using var services = McpTestServices.ForDiscoveredTools();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services,
            subscriptionsDeliverable: deliverable);

        Assert.Equal(beklenen, options.Capabilities?.Resources?.Subscribe ?? false);
    }

    /// <summary>
    /// <b>Kaldırılmış RPC gerçekten kaldırılmış</b> — ve sunucu göç ipucu veriyor.
    ///
    /// <para>
    /// Bu bir uyum ölçümü: bir istemci eski yolu denerse cevap <i>"böyle bir
    /// metot yok"</i> değil, <b>ne yapması gerektiği</b> olmalı. Aksi hâlde
    /// istemci yazarı revizyon farkını arar ve bulamaz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kaldirilmis_abonelik_rpcsi_goc_ipucu_donduruyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: false, ekIstek: """
            {"jsonrpc":"2.0","id":2,"method":"resources/subscribe","params":{"uri":"bizigo://rca-runs"}}
            """);

        var cevap = satirlar.FirstOrDefault(s => s.Contains("\"id\":2", StringComparison.Ordinal));

        Assert.NotNull(cevap);
        Assert.Contains("subscriptions/listen", cevap, StringComparison.Ordinal);
        Assert.Contains("resourceSubscriptions", cevap, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Yetenek kapalıyken sunucu aboneliği ONAYLAMIYOR.</b>
    ///
    /// <para>
    /// Ölçülen hâl: <c>subscriptions/acknowledged</c>'ın <c>notifications</c>'ı
    /// <b>boş</b> dönüyor — yani sunucu <i>"bu aboneliği taşımıyorum"</i> diyor.
    /// Bu, <c>SupportsSubscription</c> bayrağının bir süsleme <b>olmadığının</b>
    /// kanıtı: bayrak aboneliğin kabul edilmesinin şartı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Yetenek_kapaliyken_abonelik_onaylanmiyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: false, yayinla: false);

        var ack = satirlar.Single(s => s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));

        Assert.DoesNotContain("resourceSubscriptions", ack, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Zincirin tamamı: onay + teslim.</b>
    ///
    /// <para>
    /// İki iddia birden ve ikisi ayrı: sunucu aboneliği <b>onaylıyor</b>
    /// (<c>resourceSubscriptions</c> geri dönüyor) <b>ve</b> yayın istemciye
    /// <b>ulaşıyor</b>. Onay olmadan teslim anlamsız (spesifikasyon istenmeyen
    /// bildirim göndermeyi yasaklıyor), teslim olmadan onay boş bir söz.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Abonelik_onaylaniyor_ve_bildirim_ulasiyor()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: true);

        var ack = satirlar.Single(s => s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));

        Assert.Contains("bizigo://rca-runs", ack, StringComparison.Ordinal);

        var bildirim = satirlar.SingleOrDefault(s =>
            s.Contains("notifications/resources/updated", StringComparison.Ordinal));

        Assert.NotNull(bildirim);
        Assert.Contains("bizigo://rca-runs", bildirim, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Akış kapanınca abonelik defterden düşüyor</b> (kabul kriteri 4).
    ///
    /// <para>
    /// Sızıntının bu katmandaki hâli: kapanmış bir akış defterde kalırsa her
    /// koşum değişiminde kapanmış bir kanala yazmayı deniyor. Ölçüt defterin
    /// <b>sayısı</b> — <i>"bildirim gitmedi"</i> ölçütü, hiç abone olmayan bir
    /// kurulumda da geçerdi.
    /// </para>
    ///
    /// <para>
    /// Kayıt <c>using</c> ile silindiği için iptal ve istisna yollarında da
    /// kapanıyor; <c>ZincirAsync</c> akışı iptalle bitiriyor, yani ölçülen yol
    /// tam olarak o.
    /// </para>
    ///
    /// <para>
    /// <b>Bu bekçi bir kez SİLİNDİ ve kırmızı ölçümü onu yakaladı</b> — ama
    /// doğrudan değil: ölçüm <i>"yeşil kaldı"</i> dedi, oysa doğrusu
    /// <i>"böyle bir test yok"</i>ydu. Ortak yordamın <c>test_kos</c>'una pozitif
    /// kontrol o yüzden eklendi: eşleşen test bulunamayan bir filtre artık
    /// <b>ölçüm yapılmadı</b> sayılıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Akis_kapaninca_abonelik_defterden_dusuyor()
    {
        var (_, registry) = await ZincirAsync(deliverable: true, yayinla: true);

        Assert.Equal(0, registry.Count);
    }

    /// <summary>
    /// <b>M20 · Bildirim artık abonelik kimliğiyle ETİKETLİ.</b>
    ///
    /// <para>
    /// M07 bu sınırı ölçüp kilitlemişti: onay bildirimi etiketli geliyordu, bizim
    /// yayınımız değil. Sebebi erişilebilen tek yayın ilkelinin oturum geneline
    /// yazması ve SDK'nın yönlendirmesinin <c>internal</c> olmasıydı.
    /// </para>
    ///
    /// <para>
    /// M20 yönlendirmeyi devralmadan çözdü: defter aboneliğin <b>kimliğini</b>
    /// biliyor (<c>subscriptions/listen</c> isteğinin id'si), yayın da her
    /// aboneliğe kendi etiketiyle gidiyor. SDK'nın işleyicisi yerinde kaldı —
    /// yani <c>*/list_changed</c> yayılımı hiç dokunulmadı.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Bildirim_abonelik_kimligiyle_etiketli()
    {
        var (satirlar, _) = await ZincirAsync(deliverable: true, yayinla: true);

        var bildirim = satirlar.Single(s =>
            s.Contains("notifications/resources/updated", StringComparison.Ordinal));

        Assert.Contains("io.modelcontextprotocol/subscriptionId", bildirim, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>M20 · İki abonelik ayırt edilebiliyor</b> — etiketlemenin asıl sınavı.
    ///
    /// <para>
    /// Etiketin <b>var olması</b> ile <b>işe yaraması</b> ayrı iki şey. Ölçüm:
    /// aynı kanalda iki <c>subscriptions/listen</c> açılıyor, biri
    /// <c>rca-runs</c>'a biri başka bir adrese abone; yayın <c>rca-runs</c>'a
    /// yapılıyor ve gelen <b>tek</b> bildirimin etiketi <b>birinci</b>
    /// aboneliğin kimliğini taşıyor.
    /// </para>
    ///
    /// <para>
    /// İkinci abonelik farklı bir adrese bakıyor, yani ölçüm aynı anda ikinci
    /// bir şeyi de söylüyor: <b>istenmeyen adres için bildirim gitmiyor</b>.
    /// Spesifikasyonun kuralı bu — <i>"the server MUST NOT send notification
    /// types the client has not explicitly requested"</i>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Iki_abonelik_etiketle_ayirt_edilebiliyor()
    {
        var (satirlar, _) = await ZincirAsync(
            deliverable: true,
            yayinla: true,
            ikinciAdres: "bizigo://parser/ornek");

        var bildirimler = satirlar
            .Where(s => s.Contains("notifications/resources/updated", StringComparison.Ordinal))
            .ToArray();

        // TEK bildirim: ikinci abonelik başka bir adrese bakıyor.
        Assert.Single(bildirimler);

        // Etiket BİRİNCİ aboneliğin kimliği (`"id":1`), ikincisinin değil.
        Assert.Contains("\"io.modelcontextprotocol/subscriptionId\":\"1\"", bildirimler[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\"io.modelcontextprotocol/subscriptionId\":\"2\"", bildirimler[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>M20 · Kapsam dışı bir grubun değişikliği aboneye GİTMİYOR.</b>
    ///
    /// <para>
    /// Yan kanalın kapandığının uçtan uca ölçümü. Abonenin kapsamı
    /// <c>network/core</c> (uyum kapısının sabit çözücüsü); yayın
    /// <c>network/edge</c> grubundan yapılıyor ve tele <b>hiçbir bildirim</b>
    /// çıkmıyor.
    /// </para>
    ///
    /// <para>
    /// Süzgecin kararı ayrıca saf fonksiyon olarak ölçülüyor
    /// (<c>McpSubscriptionSideChannelTests</c>); bu test <b>akışın</b> o kararı
    /// gerçekten uyguladığını söylüyor. İkisi ayrı soru: biri kararın
    /// doğruluğu, diğeri kararın <i>bağlanmış</i> olması.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kapsam_disi_grubun_degisikligi_aboneye_gitmiyor()
    {
        var (satirlar, _) = await ZincirAsync(
            deliverable: true,
            yayinla: true,
            yayinGrubu: "network/edge");

        Assert.DoesNotContain(
            satirlar,
            s => s.Contains("notifications/resources/updated", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>M20 · Köprü değişikliğin GRUBUNU taşıyor.</b>
    ///
    /// <para>
    /// Doğrudan yayın (<c>PublishAsync</c>) köprünün grubu geçirdiğini
    /// ölçmüyor — köprü sabit bir grup yazsa da o testler geçerdi. Bu ölçüm
    /// zinciri <c>IRcaRunChangeListener</c>'dan başlatıyor: kapsam dışı bir
    /// grupta değişiklik <b>hiç bildirim üretmiyor</b>, aynı grupta üretiyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("network/core", true)]
    [InlineData("network/edge", false)]
    public async Task Kopru_degisikligin_grubunu_tasiyor(string grup, bool bildirimBekleniyor)
    {
        var (satirlar, _) = await ZincirAsync(
            deliverable: true,
            yayinla: true,
            yayinGrubu: grup,
            koprudenYayinla: true);

        var geldi = satirlar.Any(s =>
            s.Contains("notifications/resources/updated", StringComparison.Ordinal));

        Assert.Equal(bildirimBekleniyor, geldi);
    }

    /// <summary>
    /// <b>M20 · <c>*/list_changed</c> yayılımı DOKUNULMADAN duruyor.</b>
    ///
    /// <para>
    /// Devralmanın bedeli buydu: SDK'nın <c>subscriptions/listen</c> işleyicisi
    /// aynı akışta katalog bildirimlerini de taşıyor. Filtre yoluyla girmenin
    /// bütün gerekçesi o yayılıma dokunmamaktı, ve <b>dokunulmadığı ölçülüyor</b>:
    /// istemci <c>toolsListChanged</c> isteyerek abone oluyor ve sunucu onu
    /// onayında <b>geri veriyor</b>.
    /// </para>
    ///
    /// <para>
    /// Bu bekçi olmadan abonelik kazanılırken katalog bildirimleri <b>sessizce</b>
    /// kaybolabilirdi — kaybın belirtisi olmayan bir kayıp.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Katalog_bildirimi_yayilimi_korunuyor()
    {
        var (satirlar, _) = await ZincirAsync(
            deliverable: true,
            yayinla: false,
            ekIstek: """
                {"jsonrpc":"2.0","id":9,"method":"subscriptions/listen","params":{"notifications":{"toolsListChanged":true},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"olcum","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
                """);

        var ack = satirlar.Last(s =>
            s.Contains("subscriptions/acknowledged", StringComparison.Ordinal));

        Assert.Contains("toolsListChanged", ack, StringComparison.Ordinal);
    }

    private static string IkinciListen(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"subscriptions/listen\",\"params\":{\"notifications\":"
        + "{\"resourceSubscriptions\":[\"" + uri + "\"]},\"_meta\":{"
        + "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\","
        + "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"olcum\",\"version\":\"1\"},"
        + "\"io.modelcontextprotocol/clientCapabilities\":{}}}}";

    /// <summary>
    /// Ham JSON-RPC ile zinciri koşturur: <c>subscriptions/listen</c> aç,
    /// istenirse yayın yap, gelen satırları döndür.
    /// </summary>
    private static async Task<(List<string> Satirlar, McpSubscriptionRegistry Registry)> ZincirAsync(
        bool deliverable,
        bool yayinla,
        string? ekIstek = null,
        string yayinGrubu = "network/core",
        string? ikinciAdres = null,
        bool koprudenYayinla = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = new McpSubscriptionRegistry();
        var updates = new McpResourceUpdates(registry);

        // Sağlayıcı ÜRETİMİN kayıt uzantılarından kuruluyor; eklenen tek şey
        // defterin AYNI örneği — testin yayın yaptığı defter ile sunucunun
        // filtresinin yazdığı defter aynı olmak zorunda, yoksa eşleşme hiç
        // olmaz ve test "bildirim gitmedi" diye okunur.
        await using var services = new ServiceCollection()
            .AddDiscoveredToolDependencies()
            .AddComplianceScopeResolver()
            .AddSingleton(registry)
            .BuildServiceProvider();

        var options = BizigoMcpServer.CreateOptions(
            McpSurface.Product,
            Boundary,
            McpComplianceTests.DeclaredAssemblies(McpSurface.Product),
            services,
            subscriptionsDeliverable: deliverable);

        var toServer = new Pipe();
        var toClient = new Pipe();

        // KİMLİK DAMGALANIYOR — ve bu ölçülerek eklendi.
        //
        // İlk hâlde düz `StreamServerTransport` kullanılıyordu ve hiçbir bildirim
        // ulaşmıyordu. Sebep: abonelik filtresi kapsamı çözmek için kimlik
        // istiyor, bellek içi taşımada `User` yok, dolayısıyla abonelik deftere
        // HİÇ girmiyordu. Bu bir test kusuru değil ölçümün kendisi: kapsam
        // süzgeci kimliksiz bir aboneliği KAPALI sayıyor ve o karar burada
        // görünür oldu. Üretimde kimliği HTTP taşıması veriyor
        // (`McpCallerScope` belgesinde ölçülmüş).
        await using ITransport transport = new McpIdentityTransport(
            new StreamServerTransport(
                toServer.Reader.AsStream(), toClient.Writer.AsStream(), "olcum", NullLoggerFactory.Instance),
            static () => Kimlik);

        await using var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, services);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = server.RunAsync(lifetime.Token);

        var yaz = toServer.Writer.AsStream();
        using var oku = new StreamReader(toClient.Reader.AsStream(), Encoding.UTF8);

        await GonderAsync(yaz, ListenIstegi, ct);

        var satirlar = new List<string> { await OkuAsync(oku, ct) };

        if (ikinciAdres is not null)
        {
            await GonderAsync(yaz, IkinciListen(ikinciAdres), ct);
            satirlar.Add(await OkuAsync(oku, ct));
        }

        if (ekIstek is not null)
        {
            await GonderAsync(yaz, ekIstek, ct);
            satirlar.Add(await OkuAsync(oku, ct));
        }

        if (yayinla)
        {
            if (koprudenYayinla)
            {
                // KÖPRÜDEN: `IRcaRunChangeListener` → adres + grup. Doğrudan
                // yayın köprünün grubu TAŞIDIĞINI ölçmüyor; bu yol ölçüyor.
                await new McpRcaRunChangeListener(updates).RunChangedAsync(
                    new RcaRunChange(Guid.NewGuid(), yayinGrubu, "Running"),
                    ct);
            }
            else
            {
                await updates.PublishAsync(RcaRunsResource.Uri, yayinGrubu, ct);
            }

            // BÜTÜN satırlar okunuyor, SABİT SAYIDA DEĞİL — ve bu ölçülerek
            // düzeltildi.
            //
            // İlk hâl yayından sonra TEK satır okuyordu. Kırmızı ölçümü kusuru
            // yakaladı: adres süzgecini kaldırınca İKİ bildirim gidiyor ama test
            // yalnızca birini okuyor, dolayısıyla `Assert.Single` yeşil kalıyordu.
            // Yani bekçi "fazla bildirim gitti" hâlini GÖREMİYORDU — ölçülmesi
            // gereken şeyin tam tersi.
            satirlar.AddRange(await BosaltAsync(oku, ct));
        }

        await lifetime.CancelAsync();

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Beklenen: sunucu döngüsü iptalle kapanıyor.
        }

        return (satirlar, registry);
    }

    private static async Task GonderAsync(Stream yaz, string satir, CancellationToken ct)
    {
        await yaz.WriteAsync(
            Encoding.UTF8.GetBytes(satir.Replace("\n", string.Empty, StringComparison.Ordinal) + "\n"),
            ct);

        await yaz.FlushAsync(ct);
    }

    /// <summary>
    /// Bir satır okur. <b>Zaman aşımı bir iddia değil bir sınır:</b> asılı kalan
    /// bir ölçüm CI'da ürün hakkında hiçbir şey söylemeyen bir iş zaman aşımına
    /// dönüşüyor — M01'de ölçülmüş bir kusur.
    /// </summary>
    /// <summary>
    /// Akışta bekleyen <b>bütün</b> satırları boşaltır. Sessizlik ölçütü kısa bir
    /// zaman aşımı: <i>"bir satır daha var mı"</i> sorusunun tel üzerinde başka
    /// cevabı yok.
    /// </summary>
    private static async Task<List<string>> BosaltAsync(StreamReader oku, CancellationToken ct)
    {
        var satirlar = new List<string>();

        while (true)
        {
            var read = oku.ReadLineAsync(ct).AsTask();
            var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2), ct));

            if (done != read)
            {
                return satirlar;
            }

            var satir = await read;

            if (satir is null)
            {
                return satirlar;
            }

            satirlar.Add(satir);
        }
    }

    private static async Task<string> OkuAsync(StreamReader oku, CancellationToken ct)
    {
        var read = oku.ReadLineAsync(ct).AsTask();
        var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), ct));

        return done == read ? await read ?? "(null)" : "(ZAMAN AŞIMI)";
    }

    private sealed class SayanDinleyici : IRcaRunChangeListener
    {
        public int Sayi { get; private set; }

        public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken)
        {
            Sayi++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PatlayanDinleyici : IRcaRunChangeListener
    {
        public ValueTask RunChangedAsync(RcaRunChange change, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ölçüm: dinleyici patlıyor");
    }
}
