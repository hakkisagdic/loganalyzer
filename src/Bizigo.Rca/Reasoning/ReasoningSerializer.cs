using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Rca.Reasoning;

/// <summary>
/// Üretilen belgenin <b>saklanan</b> biçimi (T51).
///
/// <para>
/// <b>Bu bir anlık görüntü, bir görünüm değil</b> — <c>BundleSerializer</c>'ın
/// kanıt paketi için verdiği kararın aynısı ve aynı gerekçeyle. Bugün yazılan
/// bir rapor altı ay sonra, o günkü kodla okunacak: F4'ün <i>"aynı kanıt
/// üzerinde farklı model koşturup karşılaştır"</i> ihtiyacının tamamı buna
/// dayanıyor.
/// </para>
///
/// <para>
/// Bunun bedeli <b>şeklin donması</b>. <see cref="CurrentSchemaVersion"/> o
/// donmayı görünür kılıyor, ve bir bekçi diske yazılmış eski bir belgenin
/// bugünkü kodla okunabildiğini sınıyor.
/// </para>
///
/// <para>
/// <b>Neden <c>snake_case</c>:</b> saklanan belge ile tel sözleşmesi aynı
/// adlandırmayı kullanıyor (§8). İki adlandırma olsaydı, aralarındaki eşleme
/// elle yazılırdı ve elle yazılan eşleme bu depoda <c>idp_groups</c> →
/// <c>idpGroups</c> olarak bir kez sessizce kırıldı.
/// </para>
/// </summary>
public static class ReasoningSerializer
{
    /// <summary>
    /// Belge biçiminin sürümü.
    ///
    /// <para>
    /// Artırmak <b>iki ayrı bilinçli hareket</b> istiyor: burayı değiştirmek ve
    /// eski sürüm fixture'ının hâlâ okunabildiğini göstermek. Sessizce
    /// artırmak, geçmiş raporları okunamaz yapıp bunu kimseye söylememek
    /// olurdu.
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,

        // Enum'lar SAYI değil AD olarak yazılıyor: saklanan bir belgede sayı,
        // enum'a bir üye eklendiği gün sessizce başka bir şeye işaret eder.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },

        // `null` YAZILIYOR. Gizlenen bir `null`, "ölçülmedi" ile "alan yok"
        // farkını siler — ve bu belgede tam olarak o fark taşınıyor
        // (`dropped_sentence_ratio`, `prompt_tokens`).
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        WriteIndented = false,
    };

    public static string Serialize(RcaReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static RcaReportDocument Deserialize(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return JsonSerializer.Deserialize<RcaReportDocument>(payload, Options)
            ?? throw new InvalidOperationException("RCA belgesi ayrıştırılamadı: sonuç null.");
    }
}
