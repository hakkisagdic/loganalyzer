using Bizigo.Contracts;

namespace Bizigo.Mcp.Product;

/// <summary>
/// <b><c>bizigo</c> yüzeyinde ürünün durumunu DEĞİŞTİREN araçların tabanı — ve
/// dört şartı karşılamayan bir yazma bu tabandan türemiyor.</b>
///
/// <h3>Bu taban M10'un kararını GENİŞLETMİYOR, DARALTIYOR</h3>
///
/// <para>
/// M10 <c>alerts.maintenance</c>'ı okuma tarafında bırakırken kararı şöyle
/// yazmıştı: <i>"bu ürün MCP üzerinden yazma yapmıyor"</i>. O cümle fazla geniş
/// çıktı ve M14'te ölçülerek daraltıldı — çünkü kararı taşıyan gerekçe
/// <b>yazmanın kendisi değildi</b>, dördüncü maddesiydi: <i>"yanlış açılmış bir
/// pencerenin belirtisi bir hata değil SESSİZLİK."</i>
/// </para>
///
/// <para>
/// Daraltılmış hâli <b>dört şart</b>, ve dördü birden gerekiyor:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>İdempotans</b> — aynı istek iki kez yapıldığında ikinci kez iş
/// üretmiyor, ve anahtar <b>çağırandan gelmiyor</b>. Modelin uydurabildiği bir
/// anahtar, idempotency'yi korunmak istenen tarafa vermek olurdu
/// (<c>McpIdempotency</c>).
/// </item>
/// <item>
/// <b>Her sonucun bir kaydı</b> — kabul, ret, kota, derinlik: hepsi bir satır.
/// M10'un reddettiği yazmanın kaybı tam olarak buydu; bakım penceresinin yanlış
/// açılması hiçbir satır üretmiyor.
/// </item>
/// <item>
/// <b>Maliyet görünürlüğü</b> — işin bedeli okunabiliyor
/// (<c>rca_runs.counts_against_quota</c>). Görünmeyen maliyet, modelin
/// tetikleyip kimsenin göremediği bir tüketim demek.
/// </item>
/// <item>
/// <b>Aktörün kimliği</b> — kayıt <i>"insan karar verdi"</i> ile <i>"model
/// karar verdi"</i> arasında ayrım taşıyor (<c>Source</c> + <c>RequestedBy</c>).
/// M10'un ikinci gerekçesi buydu ve dördüncü şart olarak duruyor: onsuz sınır
/// <i>"kaydı olan her yazma"</i> diye gevşer ve kaydın <b>neyi</b> kaydettiği
/// düşer.
/// </item>
/// </list>
///
/// <para>
/// <b>Dördü bir yorum değil, iki mekanizmaya bağlı.</b> Bu tabandan türeyen her
/// araç <c>McpReadToolTests.Hicbir_urun_araci_yazma_cagirmiyor</c>'un
/// <b>sayılı muafiyet</b> listesine gerekçesiyle girmek zorunda — kalıp
/// <c>ProducesContractTests.ExpectedExemptCount</c>'tan, yani muafiyet
/// <b>iki bilinçli hareket</b> gerektiriyor (§8). Bekçiyi tümden kaldırmak
/// elendi: o zaman <c>alerts.maintenance</c> kararını tutan hiçbir şey
/// kalmazdı.
/// </para>
///
/// <h3>Kapsam kapısı İKİNCİ KEZ YAZILMIYOR</h3>
///
/// <para>
/// <see cref="ProductReadTool.ScopeRejection"/> <c>protected internal</c>, yani
/// <c>internal</c> tarafı bu derlemeden erişilebilir ve <b>aynı statik</b>
/// çağrılıyor. K17'nin yolunda ikinci bir kapı açmak §9'un adıyla yasakladığı
/// şey; ve o kapının kendisi zaten <c>McpReadToolTests</c>'te ölçülüyor —
/// kopyalasaydık ölçüm yalnızca birinci kopyayı tutardı.
/// </para>
///
/// <para>
/// <b>Yazma araçları kapsamı her zaman okuyor</b> (<c>ReadsScopedData</c>
/// karşılığı bir bayrak yok): bir şey değiştiren araç, neyi değiştirmeye
/// yetkili olduğunu bilmek zorunda. Katalog gibi <i>yapılandırma</i> istisnası
/// burada anlamsız.
/// </para>
/// </summary>
public abstract class ProductWriteTool : BizigoMcpTool
{
    /// <summary>
    /// Yüzey <c>sealed</c> — gerekçe <see cref="ProductReadTool.Surface"/>
    /// ile aynı: simülatör yüzeyinin gevşekliği ürün verisine uygulanamaz (K6).
    /// </summary>
    public sealed override McpSurface Surface => McpSurface.Product;

    /// <summary>
    /// <c>false</c> ve <c>sealed</c>. <c>ReadOnlyHint</c> ile yalan söylemek
    /// modele <i>"bunu serbestçe deneyebilirsin"</i> demek olurdu ve bu tabandan
    /// türeyen her araç tanım gereği bir şey değiştiriyor.
    ///
    /// <para>
    /// <see cref="BizigoMcpTool"/> bunu <c>DestructiveHint</c>'e çeviriyor
    /// (<c>!IsReadOnly</c>), yani model aracı <b>yıkıcı</b> olarak görüyor.
    /// <c>rca.trigger</c> için bu fazla sert değil: kota tüketiyor ve o tüketim
    /// geri alınamıyor.
    /// </para>
    /// </summary>
    public sealed override bool IsReadOnly => false;

    /// <summary>
    /// Aracın gerçek işi. Kapsam <b>verilmiş</b> geliyor — aranmıyor.
    /// </summary>
    protected internal abstract ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken);

    /// <summary>
    /// M01'in kancası. <c>sealed</c>: kapsam kapısı atlanamıyor — gerekçe
    /// <see cref="ProductReadTool"/> belgesinde ve burada tekrarlanmıyor.
    /// </summary>
    protected sealed override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scope = ProductToolScope.Of(invocation);

        // AYNI STATİK — ikinci bir kapı değil. Yazma araçlarında
        // `readsScopedData` her zaman `true`.
        if (ProductReadTool.ScopeRejection(scope, readsScopedData: true) is { } rejection)
        {
            return ValueTask.FromResult(McpToolResult.Failure(rejection));
        }

        return ExecuteScopedAsync(invocation, scope, cancellationToken);
    }
}
