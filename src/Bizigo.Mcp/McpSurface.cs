namespace Bizigo.Mcp;

/// <summary>
/// Ürünün iki MCP yüzeyi. <b>Aynı protokol, aynı çekirdek, ayrı araç kümesi.</b>
///
/// <para>
/// Ayrımın sebebi risk: <c>bizigo-sim</c>'in en kötü hâli yanlış bir simülatör
/// durumu, <c>bizigo</c>'nun en kötü hâli <b>log verisinin kurumdan çıkması</b>
/// (K6). İki kümeyi tek sunucuda birleştirmek, ikisini de ikinci riskin
/// kurallarına tabi kılardı — ya da daha kötüsü, birincinin gevşekliğini
/// ikinciye taşırdı.
/// </para>
/// </summary>
public enum McpSurface
{
    /// <summary>
    /// <b>Beyan edilmemiş — reddediliyor.</b>
    ///
    /// <para>
    /// Sıfır değeri bilerek geçersiz. Varsayılanı <see cref="Product"/> yapmak,
    /// yüzeyini yazmayı unutan bir aracı sessizce <b>ürün verisi döndüren</b>
    /// kümeye koyardı — yani unutkanlığın bedeli en tehlikeli tarafa düşerdi.
    /// Kalıp T42'nin <c>ModelEndpoint</c>'inden: ağ sınırı da beyan edilmeden
    /// geçilmiyor.
    /// </para>
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// <c>bizigo</c> — ürünün kendisi. K6'nın, kapsam filtresinin ve
    /// redaksiyonun alanı.
    /// </summary>
    Product = 1,

    /// <summary>
    /// <c>bizigo-sim</c> — simülatör kontrolü. Ürün verisi döndürmüyor,
    /// simülatör durumu döndürüyor.
    /// </summary>
    Simulator = 2,
}

/// <summary>Yüzeylerin tel üzerindeki adları.</summary>
public static class McpSurfaces
{
    /// <summary>Ürün yüzeyinin sunucu adı.</summary>
    public const string ProductName = "bizigo";

    /// <summary>Simülatör yüzeyinin sunucu adı.</summary>
    public const string SimulatorName = "bizigo-sim";

    /// <summary>
    /// Yüzeyin tel adı. <see cref="McpSurface.Unspecified"/> için
    /// <b>fırlatıyor</b>: beyansız bir yüzeyin adı yok, ve "varsayılan bir ad
    /// ver" demek beyan kapısını hiç açmamakla aynı şey olurdu.
    /// </summary>
    public static string WireName(McpSurface surface) => surface switch
    {
        McpSurface.Product => ProductName,
        McpSurface.Simulator => SimulatorName,
        _ => throw new ArgumentOutOfRangeException(
            nameof(surface),
            surface,
            "MCP yüzeyi beyan edilmedi. `Unspecified` bir varsayılan değil, bir rettir: "
            + "yüzeyini yazmayan bir araç sessizce ürün verisi kümesine düşerdi."),
    };

    /// <summary>Tel adından yüzeye. Tanınmayan ad <c>null</c> döner.</summary>
    public static McpSurface? Parse(string? name) => name switch
    {
        ProductName => McpSurface.Product,
        SimulatorName => McpSurface.Simulator,
        _ => null,
    };
}
