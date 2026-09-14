using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Parsing;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Schema;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>catalog.parsers</c> — yüklü parser kataloğu. Ucu <c>/v1/parsers</c>.
///
/// <para>
/// <b>Kapsam filtresi yok ve bu bilinçli.</b> Gerekçe REST tarafında yazılı:
/// <i>"katalog veri değil, yapılandırma. Bir ekibin hangi parser'ların var
/// olduğunu görmesi kimsenin logunu görmesi anlamına gelmiyor."</i> Bu araç
/// <see cref="ProductReadTool.ReadsScopedData"/>'yı <see langword="false"/>
/// yapan <b>tek</b> araç ve o muafiyet sayılıyor
/// (<c>McpProductScopeGateTests</c>).
/// </para>
///
/// <para>
/// <b>Kapsam ORANI (<c>/v1/parsers/coverage</c>) bu araçta YOK.</b> O uç
/// <c>CatalogCoverageCache.Measure(...)</c>'a gidiyor ve katalog değiştiyse
/// <b>bütün kataloğu yeniden ölçüyor</b> — altın örneklerin tamamını
/// dispatcher'dan geçirerek. Bir ekran düğmesi için doğru maliyet; bir model
/// çağrısı için değil, çünkü modelin o maliyeti göremediği bir yerden
/// tetikliyor. Ayrıca <c>Bizigo.Authoring</c> referansı gerektiriyordu ve o
/// referans <c>Bizigo.Cli</c>'yi de sürüklüyordu. Katalog kalitesinin
/// modele açık olan hâli <c>backtracking_groks</c>: F1'de 21'den 0'a indirilen
/// sayaç, ve sıfırdan farklı olması tek başına bir uyarı.
/// </para>
/// </summary>
/// <param name="catalog">Yüklü katalog. Yeniden yükleme atomik.</param>
public sealed class CatalogParsersTool(ParserCatalog catalog) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "catalog.parsers";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Parser kataloğu";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Yüklü log parser'larını listeler (yapılandırma, log verisi değil). "
        + "`backtracking_groks` sıfırdan farklıysa katalogda doğrusal olmayan bir ifade var.";

    /// <summary>
    /// <inheritdoc/>
    ///
    /// <para>
    /// Katalog yapılandırma, kapsamlı veri değil — gerekçe sınıf belgesinde.
    /// </para>
    /// </summary>
    protected internal override bool ReadsScopedData => false;

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
            "count":              { "type": "integer", "minimum": 0 },
            "backtracking_groks": { "type": "integer", "minimum": 0 },
            "parsers": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":                 { "type": "string" },
                  "version":            { "type": "string" },
                  "vendor":             { "type": "string" },
                  "product":            { "type": "string" },
                  "specificity":        { "type": "integer" },
                  "backtracking_groks": { "type": "integer", "minimum": 0 }
                },
                "required": ["id", "version", "vendor", "product", "specificity", "backtracking_groks"],
                "additionalProperties": false
              }
            }
          },
          "required": ["count", "backtracking_groks", "parsers"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Anlık görüntü BİR KEZ okunuyor: `catalog.Current` her erişimde
        // değişebilir (yeniden yükleme atomik ama araya girebilir) ve iki
        // ayrı okuma "sayı N, satır M" gibi kendi içinde tutarsız bir yük
        // üretebilirdi.
        var snapshot = catalog.Current;

        var rows = snapshot.Parsers
            .Select(static parser => Row.From(
                parser.Definition.Metadata,
                parser.Groks.Count(static g => !g.IsLinearTime)))
            .OrderBy(static row => row.Id, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(rows)));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Örnek gerçek `Row.From` ve gerçek `Shape` üzerinden geçiyor. Atlanan
        // tek şey `parser.Groks.Count(...)` — o bir SAYIM, şekillendirme değil;
        // bir `CompiledParser` kurmak YAML derlemek demek olurdu ve uyum kapısı
        // şemayı ölçüyor, derleyiciyi değil.
        Row[] rows =
        [
            Row.From(
                new ParserMetadata
                {
                    Id = "cisco-asa",
                    Version = "3",
                    Vendor = "cisco",
                    Product = "asa",
                    Specificity = 80,
                },
                backtrackingGroks: 0),

            // İkinci satır BİLEREK sıfırdan farklı: katalog geneli toplamının
            // gerçekten toplandığı görülsün. İki sıfır satır, toplamı sabit
            // sıfır döndüren bir kusuru gizlerdi.
            Row.From(
                new ParserMetadata
                {
                    Id = "generic-syslog",
                    Version = "1",
                    Vendor = string.Empty,
                    Product = string.Empty,
                    Specificity = 10,
                },
                backtrackingGroks: 2),
        ];

        return ValueTask.FromResult(McpToolResult.Structured(Shape(rows)));
    }

    private static Payload Shape(IReadOnlyList<Row> rows) => new(
        rows.Count,
        rows.Sum(static row => row.BacktrackingGroks),
        rows);

    /// <summary>Yükün tek satırı — <c>CompiledParser</c> değil (§8).</summary>
    private sealed record Row(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("specificity")] int Specificity,
        [property: JsonPropertyName("backtracking_groks")] int BacktrackingGroks)
    {
        /// <summary>
        /// <c>description</c> ve <c>license</c> yükte YOK: ikisi de serbest
        /// metin, katalog başına onlarca satır, ve modelin karar verebileceği
        /// bir şey değil. Her <c>catalog.parsers</c> çağrısında bağlam
        /// bütçesinden ödenirlerdi.
        /// </summary>
        internal static Row From(ParserMetadata metadata, int backtrackingGroks) => new(
            metadata.Id,
            metadata.Version,
            metadata.Vendor,
            metadata.Product,
            metadata.Specificity,
            backtrackingGroks);
    }

    private sealed record Payload(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("backtracking_groks")] int BacktrackingGroks,
        [property: JsonPropertyName("parsers")] IReadOnlyList<Row> Parsers);
}
