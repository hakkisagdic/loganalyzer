using System.Text.Json;
using System.Text.Json.Serialization;

using Bizigo.Commands.Fields;
using Bizigo.Mcp;

namespace Bizigo.Commands.Mcp;

/// <summary>
/// <c>fields.coverage</c> — altın örneklerin taşıdığı bilginin ne kadarının
/// olay tablosuna alan olarak indiği.
///
/// <h3>ARAÇ YALNIZCA KATALOG YARISINI CEVAPLIYOR</h3>
///
/// <para>
/// Komutun iki yarısı var: katalog yarısı <i>"ne üretilebiliyor"</i> (yerel
/// dosyalar) ve ClickHouse yarısı <i>"ne yazılmış"</i> (ürün verisi). Araç
/// <b>yalnızca birincisini</b> ilan ediyor ve bu bir eksiklik değil bir kapı.
/// </para>
///
/// <para>
/// <b>Sebep K17.</b> ClickHouse yarısı <c>owner_group</c> ile sorguluyor ve o
/// değer araç argümanından gelseydi <b>çağıran kendi kapsamını seçerdi</b> —
/// oysa kapsam kimlikten türemek zorunda. Bir model istediği grubun satır
/// sayısını sayabilirdi; hata yok, sayaç yok, belirti yok.
/// </para>
///
/// <para>
/// Doğru çözüm bağlantıyı gizlemek değil, kapsamı <b>kimlikten</b> almak —
/// ve o yol M08'in kimlik taşımasıyla M04'te açılıyor. O gelene kadar yarım
/// bir kapı yerine <b>kapalı bir kapı</b> duruyor; <c>parser.try</c>'daki
/// daraltmanın aynı gerekçesi.
/// </para>
/// </summary>
public sealed class FieldsCoverageTool(ParserToolbox toolbox) : CommandTool("fields.coverage")
{
    /// <summary>
    /// <b>Araç açıklamasına eklenen tek cümle</b> — ve modelin görmesi gereken
    /// şey bu: araç CLI ile <b>aynı sayıyı üretmiyor</b>.
    ///
    /// <para>
    /// CLI <c>--anchor</c> ile örneklerin taşınacağı anı ayarlatıyor; araç onu
    /// <c>UnixEpoch</c>'a sabitliyor, çünkü bir araç çağrısının sonucu çağrı
    /// SAATİNE göre değişmemeli. Ama bu, iki yüzeyin farklı sayı vermesi
    /// demek — ve bunu yalnızca kodda yazmak, aracı kullanan modelin onu hiç
    /// görmemesi olurdu.
    /// </para>
    /// </summary>
    protected override string DescriptionSuffix =>
        " Yalnızca katalog yarısını ölçer (ne üretilebiliyor); yazılmış satırları " +
        "saymaz. Örnekler sabit bir zaman ankrajıyla ölçülür, CLI'nin ayarlanabilir " +
        "ankrajıyla farklı sayı verebilir.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "catalog":     { "type": "string" },
            "mask_file":   { "type": "string" },
            "migrations":  { "type": "string" },
            "owner_group": { "type": "string" }
          },
          "required": ["catalog", "mask_file", "migrations", "owner_group"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "column_count":     { "type": "integer", "minimum": 0 },
            "sample_count":     { "type": "integer", "minimum": 0 },
            "empty_everywhere": { "type": "array", "items": { "type": "string" } },
            "vendors": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "vendor":         { "type": "string" },
                  "lines":          { "type": "integer", "minimum": 0 },
                  "filled_aliases": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["vendor", "lines", "filled_aliases"],
                "additionalProperties": false
              }
            }
          },
          "required": ["column_count", "sample_count", "empty_everywhere", "vendors"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override async ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var request = new FieldCoverageRequest(
            invocation.Required<string>("catalog"),
            invocation.Required<string>("mask_file"),
            invocation.Required<string>("migrations"),
            // BAĞLANTI YOK ve bu araç için sabit: ClickHouse yarısı kapsamı
            // çağıranın seçtiği bir gruptan alırdı (K17 ihlali). `owner_group`
            // burada yalnızca bellekteki sentetik olayları etiketliyor.
            ConnectionString: null,
            invocation.Required<string>("owner_group"),

            // Ana DEĞİŞKEN DEĞİL: araç çağrısının sonucu çağrı saatine göre
            // değişmemeli. CLI'de ayarlanabilir, burada sabit — aynı girdinin
            // aynı cevabı vermesi bir araç için sözleşmenin parçası.
            Anchor: DateTimeOffset.UnixEpoch);

        var outcome = await FieldsCommands
            .CoverageAsync(request, toolbox, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Ok ? McpToolResult.Structured(Shape(outcome.Payload)) : Failure(outcome.Failure);
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new FieldsCoverageOutcome(
                ColumnCount: 42,
                SampleCount: 87,
                new FieldCoverageReport([], []),
                Stored: null))));
    }

    private static Payload Shape(FieldsCoverageOutcome outcome) => new(
        outcome.ColumnCount,
        outcome.SampleCount,
        outcome.Report.EmptyEverywhere(),
        [
            .. outcome.Report.Vendors.Select(vendor => new VendorPayload(
                vendor.Vendor,
                vendor.Lines,
                [
                    .. outcome.Report.Aliases
                        .Where(alias => vendor.Populated.GetValueOrDefault(alias) > 0)
                        .Order(StringComparer.Ordinal),
                ])),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("column_count")] int ColumnCount,
        [property: JsonPropertyName("sample_count")] int SampleCount,
        [property: JsonPropertyName("empty_everywhere")] IReadOnlyList<string> EmptyEverywhere,
        [property: JsonPropertyName("vendors")] IReadOnlyList<VendorPayload> Vendors);

    private sealed record VendorPayload(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("lines")] int Lines,
        [property: JsonPropertyName("filled_aliases")] IReadOnlyList<string> FilledAliases);
}

/// <summary>
/// <c>fields.values</c> — bir kolonun taşıyabileceği değerler, <b>veriye
/// bakmadan</b>.
///
/// <para>
/// Değerler katalogdan ve eşleme tablolarından türüyor, log verisinden değil:
/// <c>KAPALI</c> bir kolonun değer kümesi ürünün kendi yapılandırması, müşteri
/// verisi değil. Bu yüzden değerler telde <b>taşınabiliyor</b> —
/// <c>parser.try</c>'daki daraltmanın buraya uygulanmamasının sebebi bu ayrım.
/// </para>
///
/// <para>
/// <b>CLI'nin <c>--rules</c> birleştirmesi burada YOK</b> ve bu bilinçli bir
/// asimetri: girdisi depo dışından gelen bir JSON dosyası
/// (<c>explain_misses.py</c> çıktısı) ve bir modelin onu üretmesinin yolu yok.
/// Aracın cevapladığı soru komutun çekirdek sorusu; <c>--rules</c> bir CLI
/// analiz eki.
/// </para>
/// </summary>
public sealed class FieldsValuesTool : CommandTool
{
    /// <summary>Yeni bir örnek.</summary>
    public FieldsValuesTool()
        : base("fields.values")
    {
    }

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "catalog":    { "type": "string" },
            "mappings":   { "type": "string" },
            "migrations": { "type": "string" }
          },
          "required": ["catalog", "mappings", "migrations"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "column_count": { "type": "integer", "minimum": 0 },
            "vendors": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "vendor":   { "type": "string" },
                  "products": { "type": "array", "items": { "type": "string" } },
                  "unreachable_core_fields": { "type": "array", "items": { "type": "string" } },
                  "columns": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "alias":  { "type": "string" },
                        "kind":   { "type": "string", "enum": ["absent", "closed", "open"] },
                        "values": { "type": "array", "items": { "type": "string" } }
                      },
                      "required": ["alias", "kind", "values"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["vendor", "products", "unreachable_core_fields", "columns"],
                "additionalProperties": false
              }
            }
          },
          "required": ["column_count", "vendors"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = FieldsCommands.Values(
            invocation.Required<string>("catalog"),
            invocation.Required<string>("mappings"),
            invocation.Required<string>("migrations"));

        return ValueTask.FromResult(outcome.Ok
            ? McpToolResult.Structured(Shape(outcome.Payload))
            : Failure(outcome.Failure));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new FieldsValuesOutcome(
                [],
                [
                    new VendorValueSpace(
                        "cisco",
                        ["asa"],
                        new Dictionary<string, ColumnValueSpace>(StringComparer.Ordinal)
                        {
                            ["activity_name"] = new(
                                "activity_name", "activity_name", ValueSpaceKind.Closed,
                                ["Deny", "Permit"], ["asa.v1"], []),
                        },
                        []),
                ]))));
    }

    private static Payload Shape(FieldsValuesOutcome outcome) => new(
        outcome.ColumnCount,
        [
            .. outcome.Spaces.Select(space => new VendorPayload(
                space.Vendor,
                space.Products,
                space.UnreachableCoreFields,
                [
                    .. space.Columns.Values
                        .OrderBy(static column => column.Alias, StringComparer.Ordinal)
                        .Select(column => new ColumnPayload(
                            column.Alias,
                            column.Kind switch
                            {
                                ValueSpaceKind.Absent => "absent",
                                ValueSpaceKind.Closed => "closed",
                                _ => "open",
                            },

                            // AÇIK bir kolonun "değerleri" yok — cihaz ne
                            // yazarsa. Boş dizi döndürmek, kapalı bir kümenin
                            // boş olmasıyla karıştırılabilirdi; `kind` o farkı
                            // taşıyor ve okuyan ona bakmak zorunda.
                            column.Kind == ValueSpaceKind.Closed ? column.Values : [])),
                ])),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("column_count")] int ColumnCount,
        [property: JsonPropertyName("vendors")] IReadOnlyList<VendorPayload> Vendors);

    private sealed record VendorPayload(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("products")] IReadOnlyList<string> Products,
        [property: JsonPropertyName("unreachable_core_fields")] IReadOnlyList<string> UnreachableCoreFields,
        [property: JsonPropertyName("columns")] IReadOnlyList<ColumnPayload> Columns);

    private sealed record ColumnPayload(
        [property: JsonPropertyName("alias")] string Alias,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("values")] IReadOnlyList<string> Values);
}
