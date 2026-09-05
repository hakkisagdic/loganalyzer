using System.IO.Pipelines;
using System.Text.Json;
using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Bizigo.Mcp;
using Bizigo.Mcp.Product.Tools;
using Bizigo.Mcp.Tools;
using Bizigo.Parsing.Dispatch;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    public static async Task<McpTestSession> StartAsync(
        McpServerOptions serverOptions,
        IServiceProvider services,
        McpClientOptions? clientOptions = null,
        CancellationToken cancellationToken = default)
    {
        // İki boru: biri istemciden sunucuya, biri sunucudan istemciye.
        var toServer = new Pipe();
        var toClient = new Pipe();
        var lifetime = new CancellationTokenSource();

        var serverTransport = new StreamServerTransport(
            toServer.Reader.AsStream(),
            toClient.Writer.AsStream(),
            serverOptions.ServerInfo?.Name ?? "test",
            NullLoggerFactory.Instance);

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
            lifetime.Dispose();

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await Client.DisposeAsync();
        await Server.DisposeAsync();

        try
        {
            await serverLoop;
        }
        catch (OperationCanceledException)
        {
            // Beklenen: döngüyü biz iptal ettik.
        }

        lifetime.Dispose();
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
internal sealed class TestOnlyTool : BizigoMcpTool
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
internal sealed class NeverEndingTool : BizigoMcpTool
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
            // Duvar saatiyle ölçmüyoruz: `Delay(Infinite)` yalnızca iptalle
            // dönüyor. Bir zaman aşımı yazsaydık test "iptal çalıştı" ile
            // "süre doldu"yu ayırt edemezdi.
            await Task.Delay(Timeout.Infinite, cancellationToken);
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
    public static ServiceProvider ForDiscoveredTools() =>
        new ServiceCollection().AddDiscoveredToolDependencies().BuildServiceProvider();

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

        return services;
    }
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
            McpSurface.Product =>
            [
                AlertRulesTool.ToolIdentifier,
                AlertTriggersTool.ToolIdentifier,
                CatalogParsersTool.ToolIdentifier,
                InventoryListTool.ToolIdentifier,
                LogsSearchTool.ToolIdentifier,
                ServerInfoTool.ToolIdentifier,
            ],

            // `bizigo-sim` ürün verisine dokunmuyor; M03'ün araçları geldiğinde
            // bu dal büyüyecek.
            _ => [ServerInfoTool.ToolIdentifier],
        };

        return [.. names.Order(StringComparer.Ordinal)];
    }
}
