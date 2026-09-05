using Bizigo.Contracts;

namespace Bizigo.Mcp.Product;

/// <summary>
/// <b>Kapsamın araç gövdesine girdiği TEK yer.</b>
///
/// <para>
/// Gövde bir satır, ve bu dosyanın var olma sebebi o satırın <b>tek</b> olması.
/// M04 M08'den önce yazıldı: o gün <see cref="McpToolInvocation"/> kapsam
/// taşımıyordu ve buradan <c>unavailable</c> dönüyordu. Kapsam bağı gelince
/// değişen şey <b>yalnızca bu metot</b> oldu — beş aracın hiçbirine
/// dokunulmadı, çünkü kapsam onlara zaten parametre olarak geliyor
/// (<see cref="ProductReadTool.ExecuteScopedAsync"/>).
/// </para>
///
/// <para>
/// <b>Neden hâlâ ayrı bir dosya.</b> Satır <see cref="ProductReadTool"/>'un
/// içine gömülebilirdi. Ayrı durmasının değeri M04'te ölçüldü: bir bağımlılık
/// henüz yokken <b>tek noktada</b> beklemek, o bağımlılık geldiğinde tek
/// noktada birleşmek demek. Aynı şey M07/M08'in bir sonraki hamlesi için de
/// geçerli — kapsamın nereden geldiği bir gün yine değişecek.
/// </para>
///
/// <para>
/// <b>Kimlik reddi burada DEĞİL.</b> M08 onu mühürlü <c>InvokeAsync</c>'e koydu:
/// <c>RequiresCallerIdentity</c> ürün yüzeyinde varsayılan <see langword="true"/>
/// ve kimliksiz oturumda araç <b>koşmadan</b> reddediliyor
/// (<c>unauthenticated</c>). Yani buraya ulaşan bir çağrının kimliği <b>var</b>;
/// buradaki soru yalnızca <i>o kimliğin kapsamı nedir</i>.
/// </para>
///
/// <para>
/// Kapsamın <b>boş</b> olması ayrı bir hâl ve cevabı
/// <see cref="ProductReadTool.ScopeRejection"/>'da: kimlik doğrulanmış ama
/// hiçbir <c>owner_group</c>'a çevrilmiyor. <c>not_found</c> dönüyor, boş liste
/// değil — sıfır satır <i>"eşleşme yok"</i> diye okunurdu.
/// </para>
/// </summary>
internal static class ProductToolScope
{
    /// <summary>
    /// Çağrının kapsamı.
    ///
    /// <para>
    /// <b>Araç kapsamı ARAMIYOR, alıyor.</b> Çözücü çağrısı burada da değil:
    /// M08 onu <c>InvokeAsync</c>'e koydu ve o metot <c>sealed</c>. Bu metot
    /// yalnızca <i>taşıyor</i> — ve taşımaktan başka bir şey yapmadığı
    /// görülebilir olsun diye tek satır kaldı.
    /// </para>
    /// </summary>
    internal static AccessScope Of(McpToolInvocation invocation) => invocation.Scope;
}
