using Bizigo.Contracts.Security;

namespace Bizigo.Mcp;

/// <summary>
/// <b>Bir kaynağın gövdesi — ve kaynak kanalının redaksiyon kapısı.</b>
///
/// <para>
/// Kaynak okuması ikinci bir model kanalı: <c>resources/read</c>'in cevabı da
/// doğrudan modelin bağlamına giriyor, tıpkı bir araç çağrısının dönüş değeri
/// gibi. M06 araç kanalında kapıyı derleyiciye bağladı
/// (<see cref="McpToolResult.WithLogText(RedactedPrompt[])"/>); bu kanal aynı
/// kapıdan geçmezse T41'in tabanı <b>yarım</b> kalır — ve yarım bir taban,
/// olmayan bir taban gibi davranmaz: <i>varmış gibi okunur</i>.
/// </para>
///
/// <para>
/// Bu yüzden bu tipi üretebilen <b>tek</b> yol <see cref="Of"/> ve onun
/// parametresi <see cref="RedactedPrompt"/>. Serbest <c>string</c> kabul eden
/// bir aşırı yükleme <b>yok</b>: bir gövde yazmak isteyen her yol elinde
/// redaksiyon kapısının çıktısını tutmak zorunda, ve o tip yalnızca
/// <see cref="RedactedPrompt.Redact"/>'ten çıkıyor.
/// </para>
///
/// <h3>Araç kanalındaki açık burada NEDEN kapanabildi</h3>
///
/// <para>
/// M06 araç kanalında bir açığı <b>yazılı bıraktı</b>:
/// <c>McpToolResult.Structured&lt;TPayload&gt;</c>'a verilen bir nesnenin
/// içindeki <c>string</c> alanlar kapıya hiç uğramadan modele iniyor. Kaynak
/// kanalında aynı açık <b>yok</b>, ve sebebi bir dikkat farkı değil, iki kanalın
/// sözleşmelerinin farkı:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Araç yükü şemaya bağlı.</b> <c>structuredContent</c> aracın
/// <c>outputSchema</c>'sına uymak <i>zorunda</i> ve uyum kapısı bunu ölçüyor.
/// Yükün tamamını redaksiyondan geçirmek, şemada <c>integer</c>,
/// <c>format: date-time</c> ya da bir sayılı küme olarak yazılı bir alanın
/// içine maske dizgesi koyabilirdi — yani kapıyı takmak <b>şemayı ihlal
/// ederdi</b>. O kanalda kapı bir <i>yasak</i> değil bir <i>imkân</i>:
/// alan <see cref="RedactedPrompt"/> olarak yazılırsa maskelenmiş çıkıyor.
/// </item>
/// <item>
/// <b>Kaynak gövdesi şemaya bağlı DEĞİL.</b> Spesifikasyon gövde için
/// <c>text</c> + <c>mimeType</c> istiyor, ilan edilmiş bir şema yok ve istemci
/// gövdeyi bir sözleşmeye karşı doğrulamıyor. Yani gövdenin tamamı kapıdan
/// geçebiliyor ve geçmesinin bir bedeli yok.
/// </item>
/// </list>
///
/// <para>
/// <b>Ölçülen kısmı:</b> gövde JSON ise redaksiyondan sonra <b>hâlâ geçerli
/// JSON</b> — çünkü maskeleme dizge <i>değerlerini</i> değiştiriyor, yapıyı
/// değil, ve maske dizgesi tırnak taşımıyor. Bu bir tahmin değil,
/// <c>McpResourceRedactionGateTests</c> ölçüyor. Ölçmeseydik iddia şu olurdu:
/// <i>"gövdeyi redaksiyondan geçiriyoruz ve muhtemelen bozulmuyor"</i> — bu
/// depoda ölçülmemiş bir <i>muhtemelen</i>, ölçülmüş bir hatadan pahalı.
/// </para>
///
/// <para>
/// <b>Yüksek entropili meşru değerler maskelenmiyor:</b> <c>signature_hash</c>,
/// <c>template_id</c> gibi alanlar <see cref="RedactedPrompt"/>'un A katmanında
/// yalnızca <b>sayılıyor</b>; ikame yalnızca C (vendor ataması) ve B (bilinen
/// biçimler: JWT, PEM, <c>Authorization</c>) katmanlarından geliyor. Yani
/// gövdeyi tümden kapıdan geçirmek belgenin kimlik alanlarını bozmuyor.
/// </para>
///
/// <h3>Kapının kalıcılığı bir yorumla tutulmuyor</h3>
///
/// <para>
/// Bu dosyanın içinde ikinci bir fabrika, bir <c>internal</c> yapıcı ya da bir
/// <c>string</c> aşırı yüklemesi açmak <b>mümkün</b> ve o hareket sessiz olurdu
/// — <see cref="McpLogText"/>'in aynı zayıflığı. Kalıcılığı ölçüm tutuyor:
/// <c>McpResourceRedactionGateTests</c> hem yansımayla (bu tipi üretebilen her
/// üye bir <see cref="RedactedPrompt"/> istiyor mu) hem IL ile (bu tipi
/// <c>newobj</c> ile kuran tek metot hangisi) sınıyor. Kalıp M06'nın
/// <c>McpRedactionGateTests</c>'inden — ikinci bir gösterim değil, aynı ölçütün
/// ikinci kanala uygulanması.
/// </para>
/// </summary>
public sealed class McpResourceBody
{
    private McpResourceBody(string text, string mimeType)
    {
        Text = text;
        MimeType = mimeType;
    }

    /// <summary>Modelin bağlamına girecek gövde — <b>maskelenmiş</b> hâli.</summary>
    public string Text { get; }

    /// <summary>Gövdenin türü. Tel üzerinde <c>mimeType</c>.</summary>
    public string MimeType { get; }

    /// <summary>
    /// Gövdeyi kurar. <b>Parametre tipi kapının kendisi.</b>
    ///
    /// <para>
    /// <paramref name="mimeType"/> serbest bir <c>string</c> ve olması gerektiği
    /// gibi: o alan <b>gövdenin içeriği değil</b> onun türü — modelin bağlamına
    /// giren metin değil. Kapının nesnesi olan şey yalnızca gövde.
    /// </para>
    /// </summary>
    public static McpResourceBody Of(RedactedPrompt redacted, string mimeType)
    {
        ArgumentNullException.ThrowIfNull(redacted);

        if (string.IsNullOrWhiteSpace(mimeType))
        {
            // Boş bir `mimeType` istemciye gövdeyi TAHMİN ettirir; MCP alanı
            // isteğe bağlı tutuyor ama "belirtilmedi" ile "düz metin" ayırt
            // edilemez hâle gelirdi.
            throw new ArgumentException(
                "Kaynak gövdesinin `mimeType`'ı zorunlu: belirtilmemiş bir tür, istemciye "
                + "gövdeyi tahmin ettirmek demek.",
                nameof(mimeType));
        }

        return new McpResourceBody(redacted.Text, mimeType);
    }
}

/// <summary>
/// Kaynak gövdelerinin tür adları. <b>Tek yerde</b>: aynı dizge iki kaynakta
/// ayrı ayrı yazılsaydı biri bir gün <c>text/json</c> olurdu ve kimse görmezdi.
/// </summary>
public static class McpResourceMimeTypes
{
    /// <summary>Yapısal belge.</summary>
    public const string Json = "application/json";

    /// <summary>İnsan tarafından okunacak rapor metni.</summary>
    public const string Markdown = "text/markdown";

    /// <summary>Parser tanımı — katalogdaki hâliyle.</summary>
    public const string Yaml = "application/yaml";
}
