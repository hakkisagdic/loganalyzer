using System.IO.Pipelines;
using System.Security.Claims;
using System.Threading.Channels;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Süreç içi bir MCP oturumu — <b>gerçek altyapıya bağlı</b> araçlarla.
///
/// <para>
/// <c>Bizigo.UnitTests</c>'teki kardeşiyle aynı fikir (iki bellek içi boru,
/// gerçek sunucu, gerçek istemci) ama ayrı bir dosya olmak zorunda: iki test
/// paketi birbirinin <c>internal</c>'ını görmüyor ve ortak bir yere taşımak
/// birim paketini bu paketin bağımlılıklarına bağlardı.
/// </para>
///
/// <para>
/// <b>Kimlik damgalanıyor.</b> M08 ürün araçlarını kimliksiz oturumda
/// <b>koşmadan</b> reddediyor; iptal ölçümünün öznesi ise koşan bir sorgu.
/// Damga, üretimde akışlanabilir HTTP taşımasının yaptığı şeyin süreç içi hâli
/// (<c>HttpContext.User</c> → <c>JsonRpcMessageContext.User</c>) ve SDK bu alanı
/// bilerek özel taşımalara açık bırakıyor.
/// </para>
/// </summary>
internal sealed class McpIntegrationSession : IAsyncDisposable
{
    private readonly Task serverLoop;
    private readonly CancellationTokenSource lifetime;

    private McpIntegrationSession(
        McpServer server,
        McpClient client,
        Task serverLoop,
        CancellationTokenSource lifetime)
    {
        Server = server;
        Client = client;
        this.serverLoop = serverLoop;
        this.lifetime = lifetime;
    }

    public McpServer Server { get; }

    public McpClient Client { get; }

    /// <summary>
    /// Oturumun kimliği: <c>sub</c> claim'i olan, doğrulanmış bir principal.
    /// Kapsam bunu <see cref="IAccessScopeResolver"/> ile çeviriyor.
    /// </summary>
    public static ClaimsPrincipal Identity { get; } = new(
        new ClaimsIdentity(
            [new Claim(BizigoClaims.Subject, "iptal-olcumu")],
            authenticationType: "integration"));

    public static async Task<McpIntegrationSession> StartAsync(
        McpServerOptions serverOptions,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var lifetime = new CancellationTokenSource();

        ITransport transport = new IdentityStampingTransport(
            new StreamServerTransport(
                toServer.Reader.AsStream(),
                toClient.Writer.AsStream(),
                serverOptions.ServerInfo?.Name ?? "test",
                NullLoggerFactory.Instance),
            Identity);

        var server = McpServer.Create(
            transport, serverOptions, NullLoggerFactory.Instance, services);

        var loop = server.RunAsync(lifetime.Token);

        var clientTransport = new StreamClientTransport(
            toServer.Writer.AsStream(),
            toClient.Reader.AsStream(),
            NullLoggerFactory.Instance);

        try
        {
            var client = await McpClient.CreateAsync(
                clientTransport, loggerFactory: NullLoggerFactory.Instance, cancellationToken: cancellationToken);

            return new McpIntegrationSession(server, client, loop, lifetime);
        }
        catch
        {
            // §3: başlattığın her prosesi temizle — bir görev de prosestir.
            await lifetime.CancelAsync();
            await server.DisposeAsync();
            await transport.DisposeAsync();
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
/// Gelen her mesaja bir kimlik damgalayan taşıma sarmalayıcısı. Gerekçe
/// <see cref="McpIntegrationSession"/> belgesinde.
/// </summary>
internal sealed class IdentityStampingTransport(ITransport inner, ClaimsPrincipal user) : ITransport
{
    private Channel<JsonRpcMessage>? stamped;

    public string? SessionId => inner.SessionId;

    public ChannelReader<JsonRpcMessage> MessageReader => Stamped().Reader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
        inner.SendMessageAsync(message, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>
    /// Okuyucu <b>bir kez</b> sarmalanıyor: her erişimde yeni bir kanal kurmak
    /// mesajları iki okuyucu arasında böler ve arıza <i>"bazı çağrılar
    /// cevapsız"</i> diye görünür.
    /// </summary>
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
                // Yutulan bir hata "istemci cevap beklerken asılı kaldı" diye
                // görünür ve sebebi hiçbir yerde durmaz.
                stamped.Writer.TryComplete(error);
            }
        });

        return stamped;
    }
}

/// <summary>
/// Sabit kapsam veren çözücü. Kimlikten kapsama çevrimin kendisi
/// <c>GroupMapping</c>'de ve orada sınanıyor; burada ölçülen şey iptal.
/// </summary>
internal sealed class IntegrationScopeResolver(AccessScope scope) : IAccessScopeResolver
{
    public AccessScope Resolve(ClaimsPrincipal? principal) => scope;
}
