using System.Reflection;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
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
    /// <param name="user">
    /// <b>Çağrı başına kimlik</b> (M13). <see langword="null"/> ise taşıma
    /// sarmalanmıyor ve oturum kimliksiz kalıyor — kimlik isteyen araçlar
    /// <c>unauthenticated</c> dönüyor, yani M13 öncesi davranış.
    ///
    /// <para>
    /// <b>Bir <c>ClaimsPrincipal</c> değil bir fonksiyon</b>, ve gerekçesi
    /// <see cref="McpIdentityTransport"/> belgesinde: belirtecin <c>exp</c>'si
    /// gerçek bir sınır ve bir kez damgalanan sabit bir kimlik onu aşardı.
    /// </para>
    ///
    /// <para>
    /// İsteğe bağlı olması bilinçli: simülatör yüzeyi kimlik istemiyor ve bu
    /// parametreyi vermek zorunda kalmamalı.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task RunAsync(
        McpSurface surface,
        McpBoundaryDeclaration boundary,
        IReadOnlyList<Assembly> toolAssemblies,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null,
        Func<ClaimsPrincipal?>? user = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolAssemblies);
        ArgumentNullException.ThrowIfNull(services);

        // ABONELİK YETENEĞİ YAYINCININ VARLIĞINA BAĞLI — ve ilk hâli öyle
        // değildi.
        //
        // İlk yazdığımda `subscriptionsDeliverable: true` sabitti ve gerekçesi
        // "stdio'da tek, uzun ömürlü bir sunucu var" idi. Doğru ama YETMİYOR:
        // defter DI'da kayıtlı değilse yayınlayacak kimse yok ve sunucu
        // `subscribe` ilan etmeye devam ederdi. Yani tam olarak kaçındığımız
        // şey — gönderilemeyecek bir bildirimin ilanı — bu satırın kendisinde
        // duruyordu. Şart artık iki parçalı: kanal var (stdio) VE yayıncı var.
        var updates = services.GetService(typeof(McpResourceUpdates)) as McpResourceUpdates;

        var options = BizigoMcpServer.CreateOptions(
            surface, boundary, toolAssemblies, services, subscriptionsDeliverable: updates is not null);

        var factory = loggerFactory ?? NullLoggerFactory.Instance;

        await using ITransport transport = user is null
            ? new StdioServerTransport(options, factory)
            : new McpIdentityTransport(new StdioServerTransport(options, factory), user);

        await using var server = McpServer.Create(transport, options, factory, services);

        // DEFTERE YAZ, VE `using` İLE ÇIKAR. Kabul kriteri 4'ün bu katmandaki
        // hâli: süreç kapandığında defterde kalan bir sunucu örneği, kapanmış
        // bir kanala yazmayı denemek demek — ve o deneme her koşum değişiminde
        // tekrarlanırdı. `using` bunu bir hatırlama işi olmaktan çıkarıyor.
        using var kayit = updates?.Register(server);

        await server.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
