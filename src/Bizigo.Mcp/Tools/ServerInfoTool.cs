using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Mcp.Tools;

/// <summary>
/// <c>server.info</c> — çekirdeğin kendi aracı. <b>Ürün verisine hiç
/// dokunmuyor.</b>
///
/// <para>
/// <b>Neden var.</b> Üç somut işi var ve hiçbiri süs değil:
/// </para>
/// <list type="number">
/// <item>
/// <b>Uyum kapısının bir öznesi olsun.</b> Sıfır araçlı bir sunucuda kapı
/// sıfır şema doğrular ve <b>sessizce yeşil</b> kalır — bu deponun adını
/// koyduğu hata sınıfı. M03/M04 gelene kadar kapının dişi olması gerekiyor.
/// </item>
/// <item>
/// <b>Bağlam bütçesinin tabanı ölçülsün.</b> Plan §9 araç şemalarının belirteç
/// maliyetini bilmediğini yazıyor. Tek araçla ölçülen sayı hem <i>zarf</i>
/// maliyetini hem <i>araç başına marjinal</i> maliyeti veriyor; M04/M05 araç
/// eklerken kör kalmıyor.
/// </item>
/// <item>
/// <b>İstemci hangi revizyona bağlandığını sorabilsin.</b> Anlaşma sonucunu
/// yalnızca el sıkışmasında görmek, sonradan bakmak isteyen için kayıp.
/// </item>
/// </list>
/// </summary>
/// <param name="surface">Aracın ilan edildiği yüzey.</param>
public sealed class ServerInfoTool(McpSurface surface) : BizigoMcpTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "server.info";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override McpSurface Surface => surface;

    /// <summary>
    /// <b>Kimlik muafiyeti — kalıcı, geçici değil</b> (M08).
    ///
    /// <para>
    /// Gerekçe sınıfın ilk cümlesi: bu araç <b>ürün verisine hiç dokunmuyor</b>.
    /// Döndürdüğü üç alan da sunucunun kendi hâli — yüzey adı, revizyon sabiti,
    /// ilan edilen araç sayısı. Kapsam filtresinin uygulanacağı bir satır yok,
    /// dolayısıyla kimlik istemek <i>"her isteği reddet"</i> demek olurdu:
    /// istemci hangi revizyona bağlandığını soramazdı ve uyum kapısının öznesi
    /// ortadan kalkardı.
    /// </para>
    ///
    /// <para>
    /// §8'in ayrımı önemli: bu bir <b>muafiyet</b>, bir bekleyen değil. "Bir gün
    /// kapanacak" ile "hiç kapanmayacak" aynı listede duramaz — bu satır ikinci
    /// listede ve <c>McpIdentityTests</c> sayısını sabit tutuyor.
    /// </para>
    /// </summary>
    public override bool RequiresCallerIdentity => false;

    /// <inheritdoc/>
    public override string ToolTitle => "Sunucu bilgisi";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bu MCP sunucusunun yüzeyini, uyduğu spesifikasyon revizyonunu ve ilan ettiği "
        + "araç sayısını döndürür. Ürün verisine erişmez.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "surface":            { "type": "string", "enum": ["bizigo", "bizigo-sim"] },
            "protocol_revision":  { "type": "string" },
            "tool_count":         { "type": "integer", "minimum": 0 }
          },
          "required": ["surface", "protocol_revision", "tool_count"],
          "additionalProperties": false
        }
        """);

    /// <summary>
    /// Yüzeyde ilan edilen araç sayısı. <c>AddBizigoMcp</c> kayıt bittikten
    /// sonra dolduruyor — kayıt sırasında sayı henüz kesin değil.
    /// </summary>
    internal int DeclaredToolCount { get; set; }

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Snapshot()));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek, gerçek yolun ŞEKİLLENDİRMESİNİ kullanıyor: aynı `Snapshot()`,
        // aynı `McpToolResult.Structured`. Elle yazılmış bir JSON sabiti
        // döndürseydi kapı şemayı değil kendini doğrulardı.
        return ValueTask.FromResult(McpToolResult.Structured(Snapshot()));
    }

    private Payload Snapshot() => new(
        McpSurfaces.WireName(Surface),
        McpRevision.Supported,
        DeclaredToolCount);

    /// <summary>
    /// Yanıt tipi — anonim nesne değil. §8: depolama tipi tel sözleşmesi
    /// değildir, ve anonim bir nesne sözleşmeyi hiç kimsenin göremeyeceği bir
    /// yere koyar.
    /// </summary>
    private sealed record Payload(
        [property: JsonPropertyName("surface")] string Surface,
        [property: JsonPropertyName("protocol_revision")] string ProtocolRevision,
        [property: JsonPropertyName("tool_count")] int ToolCount);
}
