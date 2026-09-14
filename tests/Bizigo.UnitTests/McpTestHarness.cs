using System.IO.Pipelines;
using System.Text.Json;
using Bizigo.Mcp;
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

/// <summary>Test oturumları için asgari servis sağlayıcısı.</summary>
internal static class McpTestServices
{
    public static ServiceProvider Empty() => new ServiceCollection().BuildServiceProvider();
}
