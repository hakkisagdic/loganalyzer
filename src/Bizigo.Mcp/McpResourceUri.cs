using System.Diagnostics.CodeAnalysis;

namespace Bizigo.Mcp;

/// <summary>
/// Kaynak adresi — <c>bizigo://{tür}/{kimlik}</c>.
///
/// <para>
/// <b>Plan şemayı tarif etmiyor</b> (M07 §6.1: <i>"plan sessiz,
/// uydurmuyorum"</i>), yani karar burada verildi ve gerekçesi yazılı.
/// </para>
///
/// <h3>Adres kapsamı TEMSİL ETMİYOR — ve bu şemanın tek katı kuralı</h3>
///
/// <para>
/// Adrese <c>owner_group</c> koymak cazip: <c>bizigo://rca-report/network-core/{id}</c>
/// okunabilir ve "hangi ekibin" sorusunu adresten cevaplıyor. <b>Reddedildi.</b>
/// Kapsamı adresin içine yazmak, kapsam kontrolünü <b>adresin doğruluğuna</b>
/// bağlamak olur: istemci adresi kendisi kuruyor, yani grubu da kendisi yazıyor.
/// O hâlde kapı <i>"istemci doğru grubu yazdı mı"</i> diye sormak zorunda kalır
/// ve bu, kapının hiç olmaması demek.
/// </para>
///
/// <para>
/// Bu deponun CSV dersinde kazanan şey tam olarak <c>owner_group</c>'du — yani
/// kapsamın kendisi. Kapsam <b>okuma yolunda</b> uygulanıyor
/// (<see cref="BizigoMcpResource"/>), adreste değil: adres yalnızca
/// <i>hangi belge</i> sorusunu cevaplıyor.
/// </para>
///
/// <para>
/// Bunun ölçülebilir sonucu şu: kimlik <b>tahmin edilebilir</b> olsa bile
/// (sıralı bir sayı, bilinen bir <c>Guid</c>) adresi bilmek belgeyi okumaya
/// yetmiyor. Kabul kriteri 3'ün istediği şey bu, ve bir bekçi onu ölçüyor.
/// </para>
///
/// <h3>Neden <c>bizigo://</c>, neden <c>https://</c> değil</h3>
///
/// <para>
/// Spesifikasyon kaynak URI'lerini serbest bırakıyor ve <c>https://</c> kullanan
/// sunucular var. Burada <b>kasıtlı olarak dolaşılamaz</b> bir şema seçildi:
/// <c>https://</c> bir adres, istemcinin ya da modelin onu <b>getirebileceği</b>
/// izlenimini verir — oysa bu belgeler yalnızca MCP oturumundan, kimliğin
/// çözüldüğü yerden okunabiliyor. Getirilebilir görünen bir adres, kimliksiz bir
/// yoldan denenmeye davet ediyor.
/// </para>
/// </summary>
/// <param name="Kind">Belge türü — <c>rca-report</c>, <c>evidence-bundle</c>, …</param>
/// <param name="Id">Belgenin kimliği. <b>Kapsam taşımıyor.</b></param>
public sealed record McpResourceUri(string Kind, string Id)
{
    /// <summary>Şema adı. Getirilemez olması bilinçli — gerekçe sınıf belgesinde.</summary>
    public const string Scheme = "bizigo";

    /// <summary>
    /// Şablondaki değişkenin adı. <b>Tek yerde</b>: şablonu kuran ile onu
    /// ayrıştıran aynı dizgeyi okuyor, yoksa ikisi sessizce ayrışırdı (§9).
    /// </summary>
    public const string IdVariable = "id";

    private const string Prefix = Scheme + "://";

    /// <summary>
    /// Bir belge türünün RFC 6570 şablonu — <c>bizigo://{tür}/{id}</c>.
    /// <c>resources/templates/list</c> bunu ilan ediyor.
    /// </summary>
    public static string Template(string kind) =>
        $"{Prefix}{Require(kind, nameof(kind))}/{{{IdVariable}}}";

    /// <summary>
    /// Şablonsuz, <b>tek</b> bir belgenin adresi — örn. koşum listesi.
    /// Aboneliğin hedefi bu biçim: değişen şey belge, kimliği yok.
    /// </summary>
    public static string Fixed(string kind) => $"{Prefix}{Require(kind, nameof(kind))}";

    /// <summary>Tel üzerindeki hâli.</summary>
    public override string ToString() => $"{Prefix}{Kind}/{Id}";

    /// <summary>
    /// Adresi ayrıştırır. Tanınmayan biçim <see langword="false"/>.
    ///
    /// <para>
    /// <b>Fırlatmıyor</b> ve sebebi ayrımın kendisi: bozuk bir URI istemci
    /// hatası, sunucu arızası değil. Çağıran bunu bir <b>kaynak hatasına</b>
    /// çeviriyor (<c>not_found</c>) — protokol istisnasına değil, yoksa istemci
    /// bir yazım hatasını bağlantı arızası sanardı (M01 §4).
    /// </para>
    ///
    /// <para>
    /// Kimlik <b>gövde olarak</b> alınıyor, eğik çizgi bölünmüyor: bir parser
    /// kimliği <c>fortinet/fortigate</c> gibi eğik çizgi taşıyabiliyor ve onu
    /// bölmek adresi türün altında ikinci bir hiyerarşiye çevirirdi.
    /// </para>
    /// </summary>
    public static bool TryParse(string? uri, [NotNullWhen(true)] out McpResourceUri? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = uri[Prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        parsed = new McpResourceUri(rest[..slash], rest[(slash + 1)..]);

        return true;
    }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Kaynak türü boş olamaz.", name)
            : value;
}
