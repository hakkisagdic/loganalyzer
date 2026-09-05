using System.Text.Json.Serialization;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Araç hatası</b> — protokol hatası değil.
///
/// <para>
/// Ayrım planın §2'sinde bir satır ama karıştırılmasının bedeli büyük:
/// istemci bir <b>iş hatasını bağlantı arızası</b> sanar. "Bu kapsamda kayıt
/// yok" ile "sunucuya ulaşamıyorum" aynı kanaldan gelirse, ajan ikincisine
/// göre davranır — yeniden dener, bekler, sunucuyu suçlar. Ürünün cevabı
/// kaybolur.
/// </para>
///
/// <para>
/// Protokol hatası (bilinmeyen yöntem, bozuk istek, desteklenmeyen sürüm)
/// JSON-RPC <c>error</c> nesnesi olarak dönüyor ve SDK'nın
/// <c>McpException</c>'ı ile ifade ediliyor. Araç hatası <b>başarılı</b> bir
/// yanıtın içinde <c>isError: true</c> ile dönüyor. Bu tip ikincisinin gövdesi.
/// </para>
/// </summary>
/// <param name="Code">
/// Makine tarafından okunabilir kod. Serbest metin değil çünkü istemcinin
/// dallanması gereken şey bu; mesaj insana, kod makineye.
/// </param>
/// <param name="Message">
/// İnsana okunan açıklama. <b>Log içeriği taşıyamaz</b> — bu tip
/// <see cref="McpLogText"/> menteşesinden geçmiyor, dolayısıyla buraya bir
/// olay satırı koymak §1'in kapısını atlamak olurdu. Hata mesajı sunucunun
/// kendi cümlesi olmalı.
/// </param>
/// <param name="Details">
/// İsteğe bağlı yapısal ayrıntı (hangi alan, hangi değer aralığı). Yine
/// sunucunun kendi ürettiği veri.
/// </param>
public sealed record McpToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, string>? Details = null)
{
    /// <summary>Girdi şemaya uymuyor ya da anlamsız.</summary>
    public const string InvalidArgument = "invalid_argument";

    /// <summary>İstenen kayıt yok — ya da <b>kapsam dışı</b>.</summary>
    public const string NotFound = "not_found";

    /// <summary>Aracın dayandığı servis şu an cevap vermiyor.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Çağrıda <b>kimlik yok</b> — araç hiç koşmadı (M08).
    ///
    /// <para>
    /// <c>not_found</c>'dan ayrı olması bir üslup tercihi değil: kimliği
    /// kaybolmuş bir <c>logs.search</c> boş sonuç dönseydi ajan bunu
    /// <i>"eşleşme yok"</i> diye okur ve <b>yanlış bir sonuca güvenle</b>
    /// varırdı. Ayrı kod, istemcinin dallanabildiği tek dürüst yer.
    /// </para>
    ///
    /// <para>
    /// <c>unavailable</c> da olmazdı: bu geçici bir arıza değil, oturumun
    /// yapısal hâli. Yeniden denemek düzeltmiyor.
    /// </para>
    /// </summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>
    /// Senaryo/istek yanlış <b>yüzeye</b> uygulandı (planın §4'ü). Ayrı bir kod,
    /// çünkü <c>not_found</c> ile karışması S04'ün tam olarak düzelttiği
    /// yanlış cümleyi geri getirirdi: <i>"profilde yok"</i> demek, <i>"bu
    /// senaryo başka bir yüzeye ait"</i>in yerine geçemez.
    /// </summary>
    public const string WrongSurface = "wrong_surface";
}
