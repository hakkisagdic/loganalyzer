using System.IO.Pipelines;
using System.Threading.Channels;
using System.Text.Json;
using Bizigo.Alerting;
using System.Security.Claims;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Mcp.Tools;
using Bizigo.Parsing.Dispatch;
using Bizigo.Query;
using Bizigo.Rca.Reasoning;
using Microsoft.EntityFrameworkCore;
using Bizigo.Simulators.Mcp;
using Bizigo.Simulators.Mcp.Tools;
using Bizigo.Commands;
using Bizigo.Commands.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.UnitTests;

/// <summary>
/// Süreç içi bir MCP oturumu: gerçek sunucu, gerçek istemci, gerçek
/// <c>initialize</c> el sıkışması — <b>iki bellek içi boru üzerinden</b>.
///
/// <para>
/// <b>Neden sahte bir sunucu değil.</b> Uyum kapısının sorusu "kodumuz kendi
/// beklentimize uyuyor mu" değil, <b>"sunucunun teldeki hâli spesifikasyona
/// uyuyor mu"</b>. Araç listesini doğrudan <c>McpServerOptions</c>'tan okumak
/// birinci soruyu sorardı: serileştirme, anlaşma ve hata dönüşümü hiç
/// koşmadan. Buradaki oturum ikinci soruyu soruyor.
/// </para>
///
/// <para>
/// <b>Konteyner yok, soket yok.</b> <c>StreamServerTransport</c> —
/// <c>StdioServerTransport</c>'un <b>taban sınıfı</b>, yani ölçülen kod yolu
/// stdio'nunkiyle aynı; fark yalnızca akışların nereden geldiği. Bu ilişki
/// varsayılmıyor, <c>McpComplianceTests</c> içinde ayrıca sınanıyor.
/// </para>
/// </summary>
internal sealed class McpTestSession : IAsyncDisposable
{
    private readonly Task serverLoop;
    private readonly CancellationTokenSource lifetime;

    private McpTestSession(McpServer server, McpClient client, Task serverLoop, CancellationTokenSource lifetime)
    {
        Server = server;
        Client = client;
        this.serverLoop = serverLoop;
        this.lifetime = lifetime;
    }

    public McpServer Server { get; }

    public McpClient Client { get; }

    /// <param name="serverOptions">Sunucu seçenekleri.</param>
    /// <param name="services">Araçların bağımlılıklarını çözecek sağlayıcı.</param>
    /// <param name="clientOptions">İstemci seçenekleri.</param>
    /// <param name="user">
    /// Oturumun <b>kimliği</b>. <see langword="null"/> ise oturum kimliksiz ve
    /// M08'in kapısı kimlik isteyen araçları <b>koşmadan</b> reddediyor —
    /// kimliksiz hâlin kendisi de ölçülmek istendiği için varsayılan bu.
    ///
    /// <para>
    /// <b>Neden kimlik verilebilir olması gerekiyordu.</b> M04 kimlik isteyen
    /// beş araç getirdi ve uyum kapısı örnek çıktıyı <c>tools/call</c> ile
    /// alıyor — yani kimliksiz bir oturumda o kapı artık şemayı değil
    /// <c>unauthenticated</c> retini ölçerdi. İki yol vardı: kapıyı
    /// <c>SampleAsync</c>'e çevirmek (serileştirme ve
    /// <c>structuredContent</c> dönüşümünü ölçümden düşürürdü — M01'in kapıyı
    /// protokolden geçirme kararının tam tersi) ya da <b>oturuma kimlik
    /// vermek</b>. İkincisi seçildi: kapı hem protokolden geçmeye devam ediyor
    /// hem üretime yaklaşıyor.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task<McpTestSession> StartAsync(
        McpServerOptions serverOptions,
        IServiceProvider services,
        McpClientOptions? clientOptions = null,
        ClaimsPrincipal? user = null,
        CancellationToken cancellationToken = default)
    {
        // İki boru: biri istemciden sunucuya, biri sunucudan istemciye.
        var toServer = new Pipe();
        var toClient = new Pipe();
        var lifetime = new CancellationTokenSource();

        ITransport serverTransport = new StreamServerTransport(
            toServer.Reader.AsStream(),
            toClient.Writer.AsStream(),
            serverOptions.ServerInfo?.Name ?? "test",
            NullLoggerFactory.Instance);

        if (user is not null)
        {
            serverTransport = new IdentityStampingTransport(serverTransport, user);
        }

        var server = McpServer.Create(
            serverTransport, serverOptions, NullLoggerFactory.Instance, services);

        var loop = server.RunAsync(lifetime.Token);

        var clientTransport = new StreamClientTransport(
            toServer.Writer.AsStream(),
            toClient.Reader.AsStream(),
            NullLoggerFactory.Instance);

        try
        {
            var client = await McpClient.CreateAsync(
                clientTransport, clientOptions, NullLoggerFactory.Instance, cancellationToken);

            return new McpTestSession(server, client, loop, lifetime);
        }
        catch
        {
            // El sıkışma düşerse sunucu döngüsü askıda kalmasın: bu deponun
            // §3'ü "başlattığın her prosesi temizle" diyor ve bir görev de
            // prosestir.
            await lifetime.CancelAsync();
            await server.DisposeAsync();
            await serverTransport.DisposeAsync();
            lifetime.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Sunucu döngüsünün kapanması için verilen üst sınır.
    ///
    /// <para>
    /// <b>Bu sayı bir ölçümden doğdu.</b> İlk hâl <c>await serverLoop</c> idi —
    /// sınırsız. İptal bekçisinin <b>kırmızı ölçümünde</b> (belirteç araca
    /// taşınmıyor kusuru) o satır <b>asıldı</b>: kusurlu sunucuda
    /// <c>test.never_ending</c> hiç dönmüyor, dolayısıyla döngü de boşalmıyor
    /// ve koşum 25 dakika sonra elle kesildi.
    /// </para>
    ///
    /// <para>
    /// <b>Asılan bir bekçi, kırmızı yanan bir bekçi değildir.</b> CI'da sonucu
    /// bir iş zaman aşımı olur ve mesajı ürün hakkında hiçbir şey söylemez —
    /// bu depoda "okunmayan kırmızı" diye adı konmuş şeyin daha kötü hâli,
    /// çünkü okunacak bir kırmızı bile yok. Sınır, kusurun testin <b>kendi
    /// iddiasında</b> görünmesini sağlıyor.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(10);

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await Client.DisposeAsync();
        await Server.DisposeAsync();

        try
        {
            await serverLoop.WaitAsync(ShutdownBudget);
        }
        catch (OperationCanceledException)
        {
            // Beklenen: döngüyü biz iptal ettik.
        }
        catch (TimeoutException)
        {
            // Döngü bütçe içinde boşalmadı — yani bir araç çağrısı hâlâ
            // askıda. Testi BURADA düşürmüyoruz: asıl iddia testin kendisinde
            // ve onu bir temizlik hatasıyla gölgelemek, kusurun sebebini
            // yanlış yere gösterirdi.
        }

        lifetime.Dispose();
    }
}

/// <summary>
/// M01'in <b>protokol mekaniğini</b> ölçen test araçlarının ortak tabanı:
/// kimlik istemiyorlar.
///
/// <para>
/// <b>Neden bir taban sınıf, neden her araçta ayrı bir satır değil.</b> Gerekçe
/// bir kez yazılsın diye. Bu araçların ölçtüğü şeyler — keşif, iptalin uca
/// ulaşması, araç hatası ile protokol hatasının ayrımı — <b>kimlikten
/// bağımsız</b>; süreç içi boru üzerinde koşan bir oturumda
/// <c>RequestContext.User</c> zaten <see langword="null"/> ve M08'in kapısı
/// onları koşmadan reddederdi. O hâlde <c>Iptal_bildirimi_araci_gercekten_iptal_ediyor</c>
/// iptali değil <b>kimlik retini</b> ölçerdi ve yeşilliği hiçbir şey ifade
/// etmezdi.
/// </para>
///
/// <para>
/// Kimlik yolunun kendisi ayrı bir yerde ölçülüyor: <c>McpIdentityTests</c>.
/// Bu muafiyet <b>üretim</b> muafiyet listesine girmiyor — o liste keşfi
/// <c>Bizigo.Api</c> kökünden yapıyor ve bu derlemeyi hiç görmüyor.
/// </para>
/// </summary>
internal abstract class ProtocolMechanicsTool : BizigoMcpTool
{
    /// <inheritdoc/>
    public sealed override bool RequiresCallerIdentity => false;
}

/// <summary>
/// <b>Gelen her mesaja bir kimlik damgalayan taşıma sarmalayıcısı.</b>
///
/// <para>
/// Üretimde bunu akışlanabilir HTTP taşıması yapıyor:
/// <c>HttpContext.User</c> → <c>JsonRpcMessageContext.User</c>. stdio'da kimlik
/// <b>yok</b> ve olmaması doğru (MCP yetkilendirme spesifikasyonu stdio'yu
/// kapsam dışında bırakıyor). Süreç içi boru oturumu üçüncü bir hâl: taşıma
/// katmanı <i>bizim</i>, dolayısıyla kimliği <b>biz</b> koyuyoruz.
/// </para>
///
/// <para>
/// SDK bu alanı bilerek açık bırakıyor — belgesi <i>"should only be set when
/// implementing a custom ITransport"</i> diyor, ve burada yapılan tam olarak o.
/// Alternatif, uyum kapısını kimlik isteyen araçlar için körleştirmekti.
/// </para>
/// </summary>
internal sealed class IdentityStampingTransport(ITransport inner, ClaimsPrincipal user) : ITransport
{
    public string? SessionId => inner.SessionId;

    public ChannelReader<JsonRpcMessage> MessageReader => Stamped().Reader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
        inner.SendMessageAsync(message, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Okuyucuyu <b>bir kez</b> sarmalıyor: her erişimde yeni bir kanal kurmak,
    /// mesajların iki okuyucu arasında bölünmesi demek olurdu ve arıza
    /// "bazı çağrılar cevapsız" diye görünürdü.
    /// </summary>
    private Channel<JsonRpcMessage>? stamped;

    private Channel<JsonRpcMessage> Stamped()
    {
        if (stamped is not null)
        {
            return stamped;
        }

        stamped = Channel.CreateUnbounded<JsonRpcMessage>();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in inner.MessageReader.ReadAllAsync())
                {
                    message.Context ??= new JsonRpcMessageContext();
                    message.Context.User = user;

                    await stamped.Writer.WriteAsync(message);
                }

                stamped.Writer.TryComplete();
            }
            catch (Exception error)
            {
                // Sessizce yutmuyoruz: yutulan bir hata "istemci cevap
                // beklerken asılı kaldı" diye görünür ve sebebi hiçbir yerde
                // durmaz.
                stamped.Writer.TryComplete(error);
            }
        });

        return stamped;
    }
}

/// <summary>
/// <b>Kapının kendi sınavı için</b> var olan araç: uyum kapısı, <i>söylenmeden</i>
/// bulduğu bir aracı denetleyebiliyor mu.
///
/// <para>
/// Bu sınıf <c>Bizigo.UnitTests</c> derlemesinde yaşıyor, yani üretim keşfine
/// hiç girmiyor — kapı onu yalnızca bu derleme keşfe verildiğinde görüyor.
/// Ölçtüğü şey: <b>yeni bir <see cref="BizigoMcpTool"/> alt sınıfı eklendiğinde
/// keşif onu kendiliğinden buluyor mu.</b>
/// </para>
/// </summary>
internal sealed class TestOnlyTool : ProtocolMechanicsTool
{
    public override string ToolName => "test.only";

    public override McpSurface Surface => McpSurface.Product;

    public override string ToolTitle => "Yalnızca test";

    public override string ToolDescription => "Uyum kapısının keşfini sınayan araç.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """{ "type": "object", "properties": {}, "additionalProperties": false }""");

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": { "ok": { "type": "boolean" } },
          "required": ["ok"],
          "additionalProperties": false
        }
        """);

    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(new { ok = true }));
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(new { ok = true }));
    }
}

/// <summary>
/// İptal bekçisinin öznesi: iptal edilene kadar dönmeyen bir araç.
///
/// <para>
/// Ürünün sorguları ClickHouse'a iniyor ve uzun sürebiliyor;
/// <c>notifications/cancelled</c> <b>gerçekten</b> iptal etmezse MCP katmanı
/// kaynak sızdırır ve istemci "iptal ettim" sanır. Bu araç o zincirin
/// ClickHouse'suz hâli: belirteç uca ulaşıyor mu.
/// </para>
/// </summary>
internal sealed class NeverEndingTool : ProtocolMechanicsTool
{
    private readonly TaskCompletionSource started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Araç gerçekten koşmaya başladı mı.</summary>
    public Task Started => started.Task;

    /// <summary>Araç iptali <b>gördü</b> mü — sızıntının tek dürüst kanıtı.</summary>
    public bool ObservedCancellation { get; private set; }

    public override string ToolName => "test.never_ending";

    public override McpSurface Surface => McpSurface.Product;

    public override string ToolTitle => "Hiç bitmeyen";

    public override string ToolDescription => "İptal bekçisinin öznesi.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """{ "type": "object", "properties": {}, "additionalProperties": false }""");

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": { "done": { "type": "boolean" } },
          "required": ["done"],
          "additionalProperties": false
        }
        """);

    protected override async ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation, CancellationToken cancellationToken)
    {
        started.TrySetResult();

        try
        {
            // Duvar saatiyle ÖLÇMÜYORUZ: bu bekleme yalnızca iptalle dönmeli,
            // yoksa test "iptal çalıştı" ile "süre doldu"yu ayırt edemezdi.
            //
            // Sınır yine de var ve bir kaçak önlüyor: `Timeout.Infinite`
            // yazıldığında, belirteci taşımayan KUSURLU bir sunucuda bu görev
            // süreç ömrü boyunca yaşıyordu. Sınır testin iddiasının çok
            // üstünde (30 sn bütçe ↔ 5 dk), yani ölçünün anlamına dokunmuyor;
            // yalnızca kusurlu koşumun arkasında görev bırakmamasını sağlıyor.
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ObservedCancellation = true;

            throw;
        }

        return McpToolResult.Structured(new { done = true });
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(new { done = true }));
    }
}

/// <summary>
/// Test oturumları için servis sağlayıcısı.
///
/// <para>
/// <b>Boş bir kap yetmiyor ve bu bir kusur değil, kapının çalıştığının
/// kanıtı.</b> M01'de bu metot gerçekten boş bir <c>ServiceCollection</c>
/// döndürüyordu — o gün ilan edilen tek araç (<c>server.info</c>) hiçbir şeye
/// bağımlı değildi. M04'ün araçları ürün servislerine bağımlı ve
/// <c>McpToolDiscovery.Instantiate</c> kurulamayan aracı <b>atlamıyor,
/// patlıyor</b>. Yani boş kap bugün kapıyı düşürüyor; düşürmesi gerekiyor.
/// </para>
///
/// <para>
/// <b>Kaydedilenler sahte, ama kapının ölçtüğü şey veri değil.</b> Uyum kapısı
/// şemaları, anlaşmayı, hata dönüşümünü ve iptali ölçüyor; ClickHouse'un ne
/// cevap verdiği entegrasyon testinin işi (§2). Sahteler bu depoda <b>zaten
/// var</b> ve ikinci kopyaları yazılmadı (§9): <c>FakeScopedQuery</c>,
/// <c>InMemoryControlPlaneFactory</c>.
/// </para>
/// </summary>
internal static class McpTestServices
{
    /// <summary>
    /// Keşfedilen araçların <b>kurulabilmesi</b> için gereken asgari kap.
    ///
    /// <para>
    /// Yeni bir araç yeni bir bağımlılık getirdiğinde bu metot <b>kırmızı
    /// yanıyor</b> ("MCP aracı ... kurulamadı") — ve o kırmızı doğru soruyu
    /// soruyor: <i>üretimde bu servis kayıtlı mı?</i>
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>Gerçekten boş</b> kap. Kapsamı dar ve bilinçli: yalnızca keşfin
    /// <b>belirli</b> türlere uygulandığı ya da yokluğun kendisinin ölçüldüğü
    /// testler için (örn. çözücü kayıtlı değilse kurulum patlıyor mu).
    ///
    /// <para>
    /// Ürün derlemesinin tamamı keşfe verildiğinde bu kap <b>yetmiyor</b> ve
    /// yetmemesi doğru: M04'ün araçları ürün servislerine bağımlı ve
    /// <c>Instantiate</c> kurulamayan aracı atlamıyor, patlıyor. O testler
    /// <see cref="ForDiscoveredTools"/> kullanıyor.
    /// <b>Artık boş DEĞİL — ve adı bilerek değişti.</b>
    ///
    /// <para>
    /// M01'de boş bir grafik yetiyordu: <c>server.info</c> bağımlılık
    /// istemiyor. M02 komut araçlarını getirince yetmez oldu ve
    /// <c>McpToolDiscovery.Instantiate</c> kurulamayan aracı ATLAMIYOR,
    /// patlıyor — yani kapı sessizce eksik bir kümeyi denetlemeye başlamıyor,
    /// koşmayı reddediyor. M01'in o kararı burada karşılığını buldu.
    /// </para>
    ///
    /// <para>
    /// <b>Üretimle AYNI uzantıdan besleniyor</b> (<c>AddBizigoCommandTools</c>).
    /// Test grafiğini elle kurmak, kapının ölçtüğü sunucu ile üretimde koşan
    /// sunucuyu ayırırdı — kapının anlamını yok eden tek hareket bu olurdu.
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>Gerçekten boş graf</b> — ve geri geldi, çünkü iki farklı soru var.
    ///
    /// <para>
    /// M02'de <c>Empty()</c> <c>Production()</c>'a çevrilmişti: komut araçları
    /// bağımlılık istiyor ve boş bir grafla <c>Instantiate</c> patlıyordu. Ama
    /// M08 ile birlikte ölçüldü ki <b>her test aynı şeyi sormuyor</b>:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>Bu fabrika</b> — <i>"hiçbir kayıt yokken ne oluyor"</i>. Keşfin
    /// belirli tipleri kurabildiğini sınayan test bunu istiyor; dolu bir graf
    /// iddiayı zayıflatırdı, çünkü gizli bir bağımlılığın olmadığını
    /// kanıtlayamazdı.</item>
    /// <item><see cref="Production"/> — <i>"üretimdeki graf ne yapıyor"</i>.
    /// Bütün araçları keşfeden testler bunu istiyor.</item>
    /// </list>
    ///
    /// <para>
    /// İkisini tek fabrikaya indirmek, iki farklı soruyu aynı yere sormak
    /// olurdu — bu depoda <c>Debounce</c>/<c>Lineage</c> anahtarlarıyla bir kez
    /// ödenmiş şekil.
    /// </para>
    /// </summary>
    public static ServiceProvider Empty() => new ServiceCollection().BuildServiceProvider();

    public static ServiceProvider ForDiscoveredTools() =>
        new ServiceCollection()
            .AddDiscoveredToolDependencies()
            .AddComplianceScopeResolver()
            .BuildServiceProvider();

    /// <summary>
    /// Araç bağımlılıkları <b>ama çözücü YOK</b>.
    ///
    /// <para>
    /// Tek tüketicisi M08'in <i>"çözücü kayıtlı değilse kurulum patlıyor"</i>
    /// testi. Ayrı bir aşırı yükleme olması şart: çözücüyü de kaydeden bir kap
    /// o testi <b>hiçbir şey ölçmeyen</b> hâle getirirdi, ve araç bağımlılıkları
    /// olmayan bir kap ise kurulumu <b>başka bir sebeple</b> düşürüp aynı testi
    /// yanlış sebeple yeşil... hayır, yanlış sebeple KIRMIZI yapardı — mesaj
    /// kimlikten değil DI'dan şikâyet ederdi.
    /// </para>
    /// </summary>
    public static ServiceProvider ForDiscoveredToolsWithoutResolver() =>
        new ServiceCollection().AddDiscoveredToolDependencies().BuildServiceProvider();

    /// <summary>
    /// Uyum kapısının kimliği: <b>oturuma</b> konan principal.
    ///
    /// <para>
    /// Kapsam çözücüsüyle <b>tutarlı</b> olması gerekiyor; ikisi ayrışsaydı kapı
    /// kimlik hakkında bir şey söylüyor gibi görünüp aslında sabit bir kapsam
    /// ölçüyor olurdu.
    /// </para>
    /// </summary>
    public static ClaimsPrincipal ComplianceIdentity { get; } = new(
        new ClaimsIdentity(
            [new Claim(BizigoClaims.Subject, "uyum-kapisi")],
            authenticationType: "uyum-kapisi-test"));

    /// <summary>
    /// Aynı kayıtların <see cref="IServiceCollection"/> hâli.
    ///
    /// <para>
    /// Ayrı bir aşırı yükleme, çünkü iki tüketicisi var ve ikisi kabı kendi
    /// kuruyor: uyum kapısı (yalın bir kap) ve <c>McpHttpTransportTests</c>
    /// (gerçek bir <c>WebApplication</c>). İkinci bir kopya, bir gün birinde
    /// olup diğerinde olmayan bir kayıt demek olurdu (§9).
    /// </para>
    /// </summary>
    public static IServiceCollection AddDiscoveredToolDependencies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // M04 — okuma araçlarının bağımlılıkları.
        //
        // SCOPED, singleton DEĞİL: üretimdeki kayıt da scoped
        // (`QueryServiceCollectionExtensions`). Burada singleton yazmak,
        // araçların çağrı başına kapsam açmasını ölçülmez kılardı — esir
        // bağımlılık testte görünmezdi ve üretimde patlardı.
        services.AddScoped<IScopedQuery>(static _ => new FakeScopedQuery());

        services.AddSingleton<IDbContextFactory<ControlPlaneDbContext>>(new InMemoryControlPlaneFactory());
        services.AddSingleton(new AlertingOptions());
        services.AddSingleton<AlertRuleService>();
        services.AddSingleton(new ParserCatalog());

        // M07 — KAYNAKLARIN bağımlılıkları, ve ömürleri ÜRETİMDEKİYLE aynı.
        //
        // `EvidenceBundleStore` scoped (`AddScoped<EvidenceBundleStore>` —
        // `EvidenceServiceCollectionExtensions`), `RcaReportStore` singleton
        // (`AddSingleton<RcaReportStore>` — `RcaServiceCollectionExtensions`).
        // Ömürleri burada eşitlemek — ikisini de singleton yazmak — kaynakların
        // okuma başına kapsam açtığını ÖLÇÜLMEZ kılardı: esir bağımlılık testte
        // görünmez, üretimde patlar. Araç tarafında aynı hata için yazılmış
        // yukarıdaki gerekçenin kaynak kanalındaki hâli.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<EvidenceBundleStore>();
        services.AddSingleton<RcaReportStore>();

        // M02 — komut araçlarının bağımlılıkları, ÜRETİMİN kendi uzantısından
        // (`AddBizigoCommandTools`). Elle kurmak, ölçülen sunucu ile koşan
        // sunucuyu ayırırdı.
        services.AddBizigoCommandTools();

        // M03 — simülatör araçlarının bağımlılıkları, ÜRETİMDEKİ uzantıdan.
        // İkinci bir kayıt listesi yazmak kapının ölçtüğü sunucu ile üretimde
        // koşanı ayırırdı (§9). Kayıt yüzeye bağlı DEĞİL: `Instantiate` bulduğu
        // her aracı kuruyor, yüzeye göre ancak kurduktan SONRA eliyor.
        //
        // Durum dosyası geçici dizine gidiyor — gerçek
        // `artifacts/bizigo-sim/state.json`'a yazsaydı testi koşturmak
        // geliştiricinin simülatör durumunu değiştirirdi.
        services.AddBizigoSimulatorTools(
            repositoryRoot: RepositoryLayout.Root,
            statePath: Path.Combine(
                Path.GetTempPath(), $"bizigo-sim-gate-{Guid.NewGuid():N}", "state.json"));

        return services;
    }

    /// <summary>
    /// Sabit kapsam veren çözücü. Gerekçesi <see cref="FixedScopeResolver"/>'da.
    /// </summary>
    public static IServiceCollection AddComplianceScopeResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAccessScopeResolver>(new FixedScopeResolver(
            AccessScope.ForGroups("uyum-kapisi", ["network/core"])));

        return services;
    }
}

/// <summary>
/// Sabit kapsam döndüren çözücü — <b>yalnızca protokol kapıları için</b>.
///
/// <para>
/// M08'in kapısı bir çözücü istiyor ve istemesi doğru: kimlik isteyen bir araç
/// ilan edilip çözücü kaydedilmemişse kurulum patlıyor (<i>"kapsam çözücüsüz bir
/// ürün yüzeyi ya her şeyi açar ya hiçbir şeyi döndürmez; ikisi de sessizce
/// yanlıştır"</i>).
/// </para>
///
/// <para>
/// <b>Bu bir gevşetme değil.</b> Uyum kapısının sorusu PROTOKOL: şema geçerli
/// mi, örnek çıktı ona uyuyor mu, iptal iletiliyor mu. Kimliğin zorunlu olduğu,
/// kimliksiz oturumun koşmadan reddedildiği ve kapsamın REST'le aynı kapıdan
/// geldiği <c>McpIdentityTests</c>'te <b>ayrıca</b> ölçülüyor. Aynı şeyi burada
/// bir kez daha ölçmeye çalışmak iki kapıyı birbirine bağlar ve ikisini de
/// bulanıklaştırır.
/// </para>
///
/// <para>
/// Kimlikten kapsama çevrimin kendisi <c>GroupMapping</c>'de ve orada
/// sınanıyor; burada çevrilecek bir kimlik yok, çünkü uyum kapısı bir kimlik
/// kapısı değil. Kapsam <b>boş olmayan</b> seçiliyor: boş kapsam
/// <c>ProductReadTool</c>'un <c>not_found</c> dalını tetikler ve kapı şemayı
/// değil o reddi ölçerdi.
/// </para>
/// </summary>
internal sealed class FixedScopeResolver(AccessScope scope) : IAccessScopeResolver
{
    public AccessScope Resolve(ClaimsPrincipal? principal) => scope;
}

/// <summary>
/// <b>Yüzey başına ilan edilmesi BEKLENEN araç kümesi — elle yazılı, TEK yerde.</b>
///
/// <para>
/// Elle olması bilinçli: keşif kümeyi kendisi buluyor, bu liste onun
/// <b>beklentisi</b>. Keşif azını bulursa bir araç sessizce düşmüş, fazlasını
/// bulursa yeni bir araç gelmiş ve buraya yazılması <b>bilinçli bir hareket</b>.
/// </para>
///
/// <para>
/// <b>Tek yerde olması da bilinçli ve M04'te ölçüldü.</b> Küme iki testte ayrı
/// ayrı yazılıydı (uyum kapısı ve HTTP taşıması) ve M04'ün beş aracı geldiğinde
/// ikisi <b>ayrıştı</b>: biri güncellendi, diğeri eski hâliyle kırmızı yandı.
/// İki liste bu depoda hep ayrışıyor (§9).
/// </para>
///
/// <para>
/// <b>Ürün araçları yalnızca <c>bizigo</c>'da.</b> Tek bir küme yazmak,
/// bir ürün aracının simülatör yüzeyine sızmasını <b>ölçülmez</b> kılardı (K6).
/// </para>
/// </summary>
internal static class McpExpectedTools
{
    /// <summary>Yüzeyin ilan etmesi beklenen araç adları, ada göre sıralı.</summary>
    public static string[] For(McpSurface surface)
    {
        string[] names = surface switch
        {
            // M02'nin komut araçları TÜRETİLİYOR (`CommandCatalog`), elle
            // yazılmıyor: `bizigo` komutlarının araç karşılığı zaten orada ilan
            // ediliyor ve ikinci bir liste ayrışırdı (§9). Parite iddiası bu:
            // stdio'da görünen her komut aracı HTTP'de de görünüyor.
            McpSurface.Product =>
            [
                .. CommandCatalog.Tools.Select(static c => c.Name),
                AlertRulesTool.ToolIdentifier,
                AlertTriggersTool.ToolIdentifier,
                CatalogParsersTool.ToolIdentifier,
                InventoryListTool.ToolIdentifier,
                LogsSearchTool.ToolIdentifier,
                ServerInfoTool.ToolIdentifier,
            ],

            // `bizigo-sim` ürün verisine dokunmuyor — M03'ün yedi aracı ve
            // çekirdeğin `server.info`'su. Ürün araçlarının BURADA olmaması
            // iddianın kendisi (K6).
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

            _ => throw new ArgumentOutOfRangeException(
                nameof(surface), surface, "Beyan edilmemiş yüzey."),
        };

        return [.. names.Order(StringComparer.Ordinal)];
    }
}
