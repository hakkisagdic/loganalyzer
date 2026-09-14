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
    /// <param name="boundary">
    /// Yüzeyin <b>K6 beyanı</b> (M06) — çağıranın vermesi şart ve burada
    /// çıkarılmıyor.
    ///
    /// <para>
    /// <b>"stdio ⇒ iç ağ" çıkarımı BİLEREK yapılmadı.</b> İstemci aynı makinede
    /// bir süreç, dolayısıyla çıkarım mekanik olarak doğru görünüyor — ve K6
    /// açısından tam ters olabiliyor: bu taşımanın en olası gerçek istemcisi
    /// bir masaüstü MCP istemcisi (Claude Desktop gibi) ve o, aldığı her şeyi
    /// <b>buluta</b> gönderiyor. Sınırı buraya çivilemek, kurumun en büyük
    /// sözünü <b>bayrak yeşilken</b> boşa çıkaran bir hâl yazmak olurdu.
    /// Beyanı operatör veriyor: <c>bizigo mcp serve --data-boundary</c>.
    /// </para>
    /// </param>
    /// <param name="toolAssemblies">
    /// Araç taşıyan derlemeler; <c>Bizigo.Mcp</c> her zaman ekleniyor.
    /// Gerekçe <c>BizigoMcpSetup.AddBizigoMcpCore</c> belgesinde.
    /// </param>
    /// <param name="services">Araçların bağımlılıklarını çözecek sağlayıcı.</param>
    /// <param name="loggerFactory">Günlükler; <c>null</c> ise hiç günlük yok.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task RunAsync(
        McpSurface surface,
        McpBoundaryDeclaration boundary,
        IReadOnlyList<Assembly> toolAssemblies,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolAssemblies);
        ArgumentNullException.ThrowIfNull(services);

        var options = BizigoMcpServer.CreateOptions(surface, boundary, toolAssemblies, services);
        var factory = loggerFactory ?? NullLoggerFactory.Instance;

        await using var transport = new StdioServerTransport(options, factory);
        await using var server = McpServer.Create(transport, options, factory, services);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
