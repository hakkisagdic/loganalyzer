using Bizigo.Contracts;

namespace Bizigo.Mcp.Product;

/// <summary>
/// <b>Kapsamın araç çağrısına girdiği TEK yer — ve M08'in bağlayacağı tek satır.</b>
///
/// <para>
/// <b>Bugün ne yapıyor.</b> Bu dalın tabanı M01; M01'in
/// <see cref="McpToolInvocation"/>'ı yalnızca <c>Arguments</c> taşıyor. Kimliği
/// MCP oturumundan uca taşıyan iş M08'de ve o dal <b>burada yok</b>. Dolayısıyla
/// bugün kapsam çözülemiyor ve bu metot <c>unavailable</c> döndürüyor.
/// </para>
///
/// <para>
/// <b>Neden boş bir kapsam ya da <c>AccessScope.System</c> değil.</b> İkisi de
/// derlenirdi ve ikisi de sessiz bir yanlış üretirdi:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>AccessScope.Denied</c> döndürmek — araçlar <b>boş sonuç</b> verirdi.
/// <c>logs.search</c>'ün sıfır satırı model tarafından *"eşleşme yok"* diye
/// okunur; hata yok, sayaç yok, belirti yok. §7'nin tarif ettiği sınıfın tam
/// örneği.
/// </item>
/// <item>
/// <c>AccessScope.System(...)</c> döndürmek — araçlar <b>her grubun verisini</b>
/// görürdü, ve M04'ün en kötü hâli tam olarak bu (K6). Ayrıca
/// <c>AccessScopeResolver</c>'ın <c>admin</c> kararını atlamak olurdu: o karar
/// bilinçli ve <b>tek yerde</b>; ikinci bir yer açmak onu karar olmaktan
/// çıkarır.
/// </item>
/// </list>
///
/// <para>
/// Geriye tek dürüst hâl kalıyor: <b>koşmadan reddetmek</b>. <c>unavailable</c>
/// istemciye *"altyapı hazır değil"* diyor — <c>not_found</c> deseydi
/// *"yanlış sordum"* ile karışırdı (M02'nin <c>CommandFailureKind</c> ayrımıyla
/// aynı gerekçe).
/// </para>
///
/// <para>
/// <b>M08 geldiğinde değişecek olan şey.</b> Gövdedeki tek dönüş, kapsamı
/// <see cref="McpToolInvocation"/>'dan okumaya döner (M08 onu <b>zorunlu</b> bir
/// alan yaptı ve kuran tek yer mühürlü <c>InvokeAsync</c>). Araçların hiçbirine
/// dokunulmuyor: kapsam onlara zaten parametre olarak geliyor
/// (<see cref="ProductReadTool.ExecuteScopedAsync"/>). Bu dosya bilerek tek
/// metot: birleştirme tek noktada çakışıyor.
/// </para>
/// </summary>
internal static class ProductToolScope
{
    /// <summary>
    /// Çağrının kapsamını verir. <see langword="false"/> dönerse araç
    /// <b>koşmuyor</b> ve <paramref name="error"/> istemciye gidiyor.
    /// </summary>
    internal static bool TryResolve(
        McpToolInvocation invocation,
        out AccessScope scope,
        out McpToolError error)
    {
        // M08'İN BAĞLAYACAĞI SATIR. Bugün kimlik taşıyıcısı yok.
        //
        // `invocation` bilerek kullanılıyor gibi durmuyor: M08 kapsamı ORAYA
        // koyuyor, dolayısıyla imza şimdiden onu alıyor. Parametreyi sonra
        // eklemek her çağıranı tekrar açmak olurdu.
        _ = invocation;

        scope = AccessScope.Denied;

        error = new McpToolError(
            McpToolError.Unavailable,
            "MCP oturumundan kimlik taşınmıyor (M08); kapsam çözülemedi ve sorgu koşturulmadı.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Boş sonuç DÖNMÜYORUZ ve bunu söylüyoruz: sıfır satır
                // "eşleşme yok" diye okunurdu.
                ["reason"] = "identity_not_carried",
                ["blocked_by"] = "M08",
            });

        return false;
    }
}
