using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Bizigo.Mcp;

/// <summary>
/// stdio taşıması. İkinci taşıma, <b>aynı</b> araç kümesi.
///
/// <para>
/// <b>stdout kutsal.</b> Bu taşımada standart çıkış protokolün kendisi:
/// oraya yazılan tek bir <c>Console.WriteLine</c> JSON-RPC akışını bozuyor ve
/// istemci bunu <i>ayrıştırma hatası</i> olarak görüyor — yani ürün hakkında
/// hiçbir şey söylemeyen bir arıza. Günlükler <b>stderr</b>'e gidiyor ve
/// varsayılan olarak kapalı.
/// </para>
/// </summary>
public static class McpStdioHost
{
    /// <summary>
    /// Sunucuyu stdio üzerinden koşturur; süreç kapanana ya da
    /// <paramref name="cancellationToken"/> iptal edilene kadar döner.
    /// </summary>
    /// <param name="surface">Hangi yüzey sunulacak.</param>
    /// <param name="compositionRoot">Araçların aranacağı kök derleme.</param>
    /// <param name="services">Araçların bağımlılıklarını çözecek sağlayıcı.</param>
    /// <param name="loggerFactory">Günlükler; <c>null</c> ise hiç günlük yok.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task RunAsync(
        McpSurface surface,
        Assembly compositionRoot,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compositionRoot);
        ArgumentNullException.ThrowIfNull(services);

        var options = BizigoMcpServer.CreateOptions(surface, compositionRoot, services);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;

        await using var transport = new StdioServerTransport(options, factory);
        await using var server = McpServer.Create(transport, options, factory, services);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
