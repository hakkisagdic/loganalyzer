namespace Bizigo.Mcp.Product.Resources;

/// <summary>
/// <b><c>bizigo</c> yüzeyindeki her kaynağın tabanı</b> — araç tarafındaki
/// <see cref="ProductReadTool"/>'un ikizi.
///
/// <para>
/// Yüzey <c>sealed</c>: bir ürün kaynağı <c>bizigo-sim</c>'e düşemiyor. Araç
/// tarafındaki gerekçe birebir geçerli (K6): simülatör yüzeyinin gevşekliği ürün
/// verisine uygulanamaz. Ve burada bedava değil — <b>yapısal</b>: bu derleme
/// simülatör yüzeyinin derleme listesinde hiç yok
/// (<c>McpCommandHandlers.ToolAssembliesFor</c>), yani keşif bu türleri
/// <c>bizigo-sim</c> için taramıyor bile. Bu satır ikinci kapı.
/// </para>
///
/// <para>
/// <b>Ömür uyarısı araç tarafıyla aynı ve aynı yerde ölçüldü:</b> kaynaklar da
/// sunucu kurulumunda <b>bir kez</b> örnekleniyor, yani yapıcıda tutulabilen tek
/// şey singleton. <c>EvidenceBundleStore</c> <b>scoped</b>
/// (<c>AddScoped&lt;EvidenceBundleStore&gt;</c>) — onu isteyen kaynak
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"/>
/// alıp <b>okuma başına</b> kapsam açıyor. <c>RcaReportStore</c> singleton ve
/// doğrudan duruyor. İkinci bir gösterim yazılmadı: kalıp
/// <see cref="ProductReadTool"/> belgesinde.
/// </para>
/// </summary>
public abstract class ProductResource : BizigoMcpResource
{
    /// <summary>Ürün yüzeyi — <c>sealed</c>, gerekçesi sınıf belgesinde.</summary>
    public sealed override McpSurface Surface => McpSurface.Product;

    /// <summary>
    /// Kaynağın gerçek okuması. Kapsam <b>verilmiş</b> geliyor.
    ///
    /// <para>
    /// <c>internal protected</c> ve gerekçesi <see cref="ProductReadTool"/>'unkiyle
    /// birebir aynı: türeyen kaynaklar uyguluyor, <b>birim testleri doğrudan
    /// çağırıyor</b>. İkincisi olmadan gövde ancak
    /// <see cref="BizigoMcpResource.SampleAsync"/> üzerinden görülürdü ve o yol
    /// şekli ölçüyor, <b>kapsam davranışını ölçmüyor</b> — yani kabul kriteri
    /// 5'in (<i>kapsam dışı bir kaynağın reddedildiği görüldü</i>) ölçülecek bir
    /// yüzeyi olmazdı.
    /// </para>
    /// </summary>
    protected internal abstract ValueTask<McpResourceBody?> ReadScopedAsync(
        McpResourceRead read,
        CancellationToken cancellationToken);

    /// <summary>
    /// Çekirdeğin kancası. <c>sealed</c>: bir ürün kaynağı okuma yolunu
    /// <b>geçersiz kılamıyor</b>, yalnızca yukarıdakini uygulayabiliyor — yani
    /// kapsam parametre olarak geliyor, aranmıyor.
    /// </summary>
    protected sealed override ValueTask<McpResourceBody?> ReadBodyAsync(
        McpResourceRead read,
        CancellationToken cancellationToken) =>
        ReadScopedAsync(read, cancellationToken);
}
