using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Contracts.Security;

/// <summary>
/// <b>Kapının ikinci kanala uzanması</b> (M06).
///
/// <para>
/// T41'in kapısı bir <b>tip</b>: <see cref="RedactedPrompt"/> yalnızca
/// <see cref="RedactedPrompt.Redact"/>'ten çıkıyor. M06 onu MCP'nin <i>metin</i>
/// kanalına bağladı (<c>McpToolResult.WithLogText</c>). Ama MCP'nin
/// <b>ikinci</b> bir kanalı var — <c>structuredContent</c> — ve o kanal
/// serbest JSON taşıyor, yani bir aracın yükünün içindeki <c>string</c> alan
/// modelin bağlamına <b>kapıdan geçmeden</b> girebiliyor.
/// </para>
///
/// <para>
/// O boşluğu bir tip tamamen kapatamaz: bir yükte meşru <c>string</c>'ler var
/// (kaynak adı, zaman damgası, kimlik). Kapatılabilen şey <b>kapının o kanalda
/// KULLANILABİLİR olması</b>: log içeriği taşıyan bir yük alanı
/// <see cref="RedactedPrompt"/> olarak yazılırsa, tel üzerinde maskelenmiş
/// metin olarak çıkıyor ve kapı yüke kadar iniyor.
/// </para>
///
/// <para>
/// Dönüştürücü olmasaydı böyle bir alan tel üzerine <b>yedi özellikli bir
/// nesne</b> olarak inerdi (<c>MaskedValues</c>, <c>ShadowCandidates</c>,
/// <c>EntropyThreshold</c>…) — gölge katmanın ölçüm sayıları, kanıt paketine
/// ait olan ve bir araç yükünde işi olmayan şeyler.
/// </para>
///
/// <h3>OKUMA BİLEREK YASAK</h3>
///
/// <para>
/// <see cref="Read"/> fırlatıyor, ve bu dönüştürücünün en önemli satırı.
/// JSON'dan bir <see cref="RedactedPrompt"/> <b>kurabilmek</b>, yapıcının
/// <c>private</c> olmasını anlamsız kılardı: kapıyı atlayan herhangi bir metin
/// bir JSON dizgesi olarak sarılıp tipe dönüştürülebilirdi. Kapının tamamı
/// <i>"bu tipin tek çıkış yolu <see cref="RedactedPrompt.Redact"/>"</i>
/// cümlesine dayanıyor.
/// </para>
/// </summary>
public sealed class RedactedPromptJsonConverter : JsonConverter<RedactedPrompt>
{
    /// <summary>
    /// <b>Her zaman fırlatıyor.</b> Bkz. sınıf belgesi — okuma yolu açmak
    /// redaksiyon kapısına ikinci bir giriş açmak olurdu.
    /// </summary>
    public override RedactedPrompt Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new JsonException(
            $"`{nameof(RedactedPrompt)}` JSON'dan OKUNAMAZ ve bu bilinçli. Okuma yolu, "
            + $"redaksiyon kapısını atlayan bir metnin bu tipe dönüşmesine izin verirdi — "
            + $"oysa kapının tamamı `{nameof(RedactedPrompt)}.{nameof(RedactedPrompt.Redact)}` "
            + "dışında bir çıkış olmamasına dayanıyor. Gelen metni redakte edin.");

    /// <summary>Maskelenmiş metin — tel üzerinde tek görünen şey.</summary>
    public override void Write(
        Utf8JsonWriter writer,
        RedactedPrompt value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(value.Text);
    }
}
