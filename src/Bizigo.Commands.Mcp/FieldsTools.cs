using System.Text.Json;
using System.Text.Json.Serialization;

using Bizigo.Commands.Fields;
using Bizigo.Mcp;

namespace Bizigo.Commands.Mcp;

/// <summary>
/// <c>fields.coverage</c> — altın örneklerin taşıdığı bilginin ne kadarının
/// olay tablosuna alan olarak indiği.
///
/// <para>
/// <b>İki yarısı ayrı kalıyor ve bu şemanın taşıdığı asıl bilgi.</b> Katalog
/// yarısı <i>"ne üretilebiliyor"</i>, ClickHouse yarısı <i>"ne yazılmış"</i>.
/// <c>stored</c> <b><see langword="null"/> ise soru hiç sorulmadı</b> (bağlantı
/// verilmedi); boş dizi ise soruldu ve grupta satır yok. İkisi tek değere
/// inseydi <i>"bakmadım"</i> ile <i>"baktım, yok"</i> aynı cevabı verirdi.
/// </para>
/// </summary>
public sealed class FieldsCoverageTool(McpSurface surface, ParserToolbox toolbox)
    : CommandTool(surface, "fields.coverage")
{
    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "catalog":     { "type": "string" },
            "mask_file":   { "type": "string" },
            "migrations":  { "type": "string" },
            "owner_group": { "type": "string" },
            "connection_string": { "type": ["string", "null"] }
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
            },
            "stored": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "properties": {
                  "vendor": { "type": "string" },
                  "rows":   { "type": "integer", "minimum": 0 }
                },
                "required": ["vendor", "rows"],
                "additionalProperties": false
              }
            }
          },
          "required": ["column_count", "sample_count", "empty_everywhere", "vendors", "stored"],
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
            invocation.Optional<string?>("connection_string"),
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
        ],
        outcome.Stored is null
            ? null
            : [.. outcome.Stored.Select(s => new StoredPayload(s.Vendor, s.Rows))]);

    private sealed record Payload(
        [property: JsonPropertyName("column_count")] int ColumnCount,
        [property: JsonPropertyName("sample_count")] int SampleCount,
        [property: JsonPropertyName("empty_everywhere")] IReadOnlyList<string> EmptyEverywhere,
        [property: JsonPropertyName("vendors")] IReadOnlyList<VendorPayload> Vendors,
        [property: JsonPropertyName("stored")] IReadOnlyList<StoredPayload>? Stored);

    private sealed record VendorPayload(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("lines")] int Lines,
        [property: JsonPropertyName("filled_aliases")] IReadOnlyList<string> FilledAliases);

    private sealed record StoredPayload(
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("rows")] long Rows);
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
    public FieldsValuesTool(McpSurface surface)
        : base(surface, "fields.values")
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
