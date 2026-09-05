using System.Text.Json;
using System.Text.Json.Nodes;
using Bizigo.Contracts.Security;

namespace Bizigo.Mcp;

/// <summary>
/// Bir MCP aracının dönebileceği <b>tek</b> şekil.
///
/// <para>
/// <b>Serbest <c>string</c> yok — ve bu kasıtlı.</b> Araç çağrısının dönüş
/// değeri doğrudan modelin bağlamına giriyor; <c>string</c> döndüren bir
/// arayüz, T41'in redaksiyon kapısını bir çağrı alışkanlığına indirirdi.
/// Gerekçenin tamamı <see cref="McpLogText"/> belgesinde.
/// </para>
///
/// <para>
/// İki taşıyıcı var ve ikisi de dar:
/// </para>
/// <list type="number">
/// <item>
/// <b>Yapısal yük</b> — <c>structuredContent</c>. Aracın <c>outputSchema</c>'sı
/// bunu tarif ediyor ve uyum kapısı örnek çağrının buna uyduğunu sınıyor.
/// </item>
/// <item>
/// <b><see cref="McpLogText"/></b> — log metni, ve içine yalnızca T41'in
/// redaksiyon kapısından geçmiş metin giriyor. Araç tarafının gördüğü imza
/// <see cref="WithLogText(RedactedPrompt[])"/>: kapı bir çağrı alışkanlığı
/// değil, <b>parametre tipi</b> (M06).
/// </item>
/// </list>
///
/// <para>
/// Hata hâli üçüncü bir taşıyıcı değil <b>aynı şeklin bir dalı</b>:
/// <see cref="Failure"/> ile üretiliyor ve tel üzerinde <c>isError: true</c>
/// oluyor — protokol istisnası değil.
/// </para>
///
/// <h3>Redaksiyon kapısının ULAŞMADIĞI yer — beyan</h3>
///
/// <para>
/// <b>İki taşıyıcının ikisi de modelin bağlamına giriyor, ama kapı yalnızca
/// birinin imzasında duruyor.</b> <see cref="BizigoMcpTool.ToProtocol"/> yükü
/// hem <c>structuredContent</c> olarak hem de <c>content</c> içinde bir metin
/// bloğu olarak gönderiyor (<c>structuredContent</c> desteklemeyen istemci için).
/// Yani <see cref="Structured{TPayload}"/>'a verilen bir nesnenin içindeki
/// <c>string</c> alan, <see cref="RedactedPrompt"/>'a <b>hiç uğramadan</b>
/// modele iniyor.
/// </para>
///
/// <para>
/// <b>Bunu bir tip tamamen kapatamaz</b> ve kapatmaya çalışmak yanlış olurdu:
/// bir yükte meşru <c>string</c>'ler var — kaynak adı, zaman damgası, kimlik,
/// hata kodu. Kapının o kanaldaki hâli bir <i>yasak</i> değil bir
/// <b>imkân</b>: log içeriği taşıyan bir yük alanı <see cref="RedactedPrompt"/>
/// olarak yazılırsa tel üzerinde maskelenmiş metin olarak çıkıyor
/// (<c>RedactedPromptJsonConverter</c>), yani kapı yüke kadar iniyor.
/// </para>
///
/// <para>
/// <b>Ve o alanın öyle yazılması bugün MEKANİK OLARAK TUTULMUYOR.</b> Bu bir
/// çağrı alışkanlığı, ve bu depo çağrı alışkanlığına dayanan mekanizmanın kaç
/// kez kaybettiğini ölçtü. Yazılı olması, kapatılmış olmasıyla aynı şey değil —
/// ama <b>yazılı olmaması</b> §7'nin sınıfına girerdi: kapı varmış gibi
/// okunan, olmayan bir kapı. M04/M05 gerçek araçları yazdığında mekanik bir
/// bekçinin şekli görülebilir olacak; bugün görülmeyen bir şeye bekçi yazmak
/// tüketicisi olmayan bir tip yazmakla aynı hata (§8).
/// </para>
/// </summary>
public sealed class McpToolResult
{
    private McpToolResult(JsonElement payload, McpToolError? error, IReadOnlyList<McpLogText> logText)
    {
        Payload = payload;
        Error = error;
        LogText = logText;
    }

    /// <summary>
    /// Yapısal yük. Hata hâlinde de dolu: <see cref="McpToolError"/>'ın kendisi
    /// serileştirilmiş hâlde burada duruyor, yani istemci hatayı da
    /// <b>ayrıştırabiliyor</b> — metinden okumak zorunda kalmıyor.
    /// </summary>
    public JsonElement Payload { get; }

    /// <summary><c>null</c> değilse bu bir araç hatası.</summary>
    public McpToolError? Error { get; }

    /// <summary>Modelin bağlamına girecek log metinleri. Bugün her zaman boş.</summary>
    public IReadOnlyList<McpLogText> LogText { get; }

    /// <summary>Araç başarılı mı.</summary>
    public bool IsError => Error is not null;

    /// <summary>
    /// Başarılı sonuç. <paramref name="payload"/> aracın
    /// <c>outputSchema</c>'sına uymak zorunda — uyum kapısı bunu örnek çağrı
    /// üzerinden sınıyor.
    /// </summary>
    public static McpToolResult Structured<TPayload>(TPayload payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, McpJson.PayloadOptions);

        if (element.ValueKind != JsonValueKind.Object)
        {
            // MCP `structuredContent`'i bir NESNE istiyor. Dizi ya da skaler
            // döndüren bir araç şemasıyla birlikte sessizce geçersiz olurdu;
            // burada patlaması, çalışma anında istemcide patlamasından iyi.
            throw new ArgumentException(
                $"MCP yapısal yükü nesne olmalı, '{element.ValueKind}' geldi. "
                + "Dizi döndürmek isteyen araç onu bir alana sarmalı (örn. `{ \"items\": [...] }`).",
                nameof(payload));
        }

        return new McpToolResult(element, error: null, logText: []);
    }

    /// <summary>
    /// Araç hatası. <b>Protokol hatası değil</b>: tel üzerinde başarılı bir
    /// yanıtın içinde <c>isError: true</c> olarak gidiyor.
    /// </summary>
    public static McpToolResult Failure(McpToolError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var element = JsonSerializer.SerializeToElement(
            new JsonObject { ["error"] = JsonSerializer.SerializeToNode(error, McpJson.PayloadOptions) },
            McpJson.PayloadOptions);

        return new McpToolResult(element, error, logText: []);
    }

    /// <summary>
    /// Sonuca log metni ekler — <b>araçların gördüğü tek yol</b>.
    ///
    /// <para>
    /// <b>Parametre <see cref="RedactedPrompt"/>, <see cref="McpLogText"/>
    /// değil.</b> İkincisi bu derlemenin iç taşıyıcısı ve fabrikası
    /// <c>internal</c>; araçlar <c>Bizigo.Mcp</c>'nin dışında yaşadığı için onu
    /// hiç göremiyorlar. Yani bir aracın log metni döndürmek için elinde
    /// tutabileceği tek şey redaksiyon kapısının çıktısı, ve o tip yalnızca
    /// <see cref="RedactedPrompt.Redact"/>'ten çıkıyor.
    /// </para>
    ///
    /// <para>
    /// İmzayı <c>McpLogText</c> alacak şekilde bırakmak, aracı bu derlemenin
    /// içine bir yol aramaya iterdi; <see cref="RedactedPrompt"/> almak kapıyı
    /// <b>aracın kendi imzasına</b> taşıyor. M06'nın "kapı derleyicide" şartı
    /// pratikte burada görülüyor.
    /// </para>
    /// </summary>
    public McpToolResult WithLogText(params RedactedPrompt[] redacted)
    {
        ArgumentNullException.ThrowIfNull(redacted);

        return new McpToolResult(
            Payload,
            Error,
            [.. LogText, .. redacted.Select(McpLogText.FromRedacted)]);
    }
}
