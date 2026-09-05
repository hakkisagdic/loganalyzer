using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Bizigo.Mcp;

/// <summary>
/// Araç <b>yükü</b>nün serileştirme ayarları — protokol zarfının değil.
///
/// <para>
/// İki ayrı JSON var ve karıştırılırsa ikisi de bozulur:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Protokol zarfı</b> (<c>jsonrpc</c>, <c>method</c>, <c>inputSchema</c> …)
/// spesifikasyonun kendi adlandırmasını taşıyor ve SDK'nın
/// <c>McpJsonUtilities.DefaultOptions</c>'ı ile yazılıyor. Ona dokunmuyoruz.
/// </item>
/// <item>
/// <b>Araç yükü</b> (<c>structuredContent</c>'in içi) <b>bizim</b>
/// sözleşmemiz ve bu depoda JSON adlandırması <c>snake_case</c> (§8). REST
/// yüzeyiyle aynı olması bir tutarlılık süsü değil: aynı alan iki yüzeyde iki
/// ad taşırsa, ikisini birden okuyan bir ajan için <c>owner_group</c> ile
/// <c>ownerGroup</c> <b>iki farklı alan</b> hâline geliyor.
/// </item>
/// </list>
/// </summary>
public static class McpJson
{
    /// <summary>Araç yükleri için tek serileştirme ayarı.</summary>
    public static JsonSerializerOptions PayloadOptions { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,

            // Alan yoksa "null" yazmak yerine hiç yazmamak, `outputSchema`'nın
            // `required` listesiyle çelişmeyi kolaylaştırırdı: kapı zaten
            // örnek çıktıyı şemaya karşı sınıyor, sessiz eksilme olmasın.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));

        // Çözücü AÇIKÇA veriliyor. `MakeReadOnly()` çözücüsüz bir örnekte
        // fırlatıyor ve hata **tip başlatıcısında** çıkıyor: her araç çağrısı
        // "An error occurred invoking '...'" diye düşüyor ve sebebi hiçbir
        // yerde görünmüyor. Uyum kapısı bunu ilk koşumda yakaladı.
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();

        return options;
    }
}
