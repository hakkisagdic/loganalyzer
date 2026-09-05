using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>inventory.list</c> — kapsam içindeki kaynak envanteri. Ucu
/// <c>/v1/sources</c> (ölçüldü).
///
/// <para>
/// <b>Envanter de kapsamlı veri.</b> <c>IScopedQuery.SearchSourcesAsync</c>'in
/// belgesi bunu yazıyor: bir ekip başka bir ekibin cihaz listesini görmemeli, ve
/// filtreyi uç katmanında elle uygulamak K17'nin kaçındığı ikinci zorlama yeri
/// olurdu. Bu araç aynı kapıdan geçiyor.
/// </para>
///
/// <para>
/// <b>Kesilme GÖRÜNÜR.</b> REST kapsamdaki bütün kaynakları döndürüyor; bir
/// ekranda bu doğru, modelin bağlamında değil. Burada bir tavan var — ama
/// <c>total</c> ve <c>truncated</c> yükte duruyor, yani modele <i>"envanter bu
/// kadar"</i> diye okunacak bir sessiz kesme yok. Sayamadığını sıfır sanmak bu
/// depodaki en pahalı hata sınıfı; <b>gösterdiğini hepsi sanmak</b> aynı sınıfın
/// kardeşi.
/// </para>
///
/// <para>
/// <b>Yükte olmayanlar ve sebepleri.</b> <c>peer_address</c>, <c>hostname</c> ve
/// <c>encoding</c> bilerek dışarıda: üçü de yalnızca tek bir kaynağın taşıma ya
/// da kodlama arızasını ayıklarken işe yarıyor, modelin karar verebileceği bir
/// şey değil, ve her listelemede bağlam bütçesinden ödeniyor. İhtiyaç doğarsa
/// eklenmesi bilinçli bir hareket olsun.
/// </para>
/// </summary>
/// <param name="scopes">
/// Çağrı başına kapsam açmak için. <c>IScopedQuery</c> <b>scoped</b> ve araç
/// tekil — gerekçe <see cref="ProductReadTool"/> belgesinde.
/// </param>
public sealed class InventoryListTool(IServiceScopeFactory scopes) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "inventory.list";

    /// <summary>Tek çağrıda dönebilecek en fazla kaynak.</summary>
    private const int MaxLimit = 500;

    /// <summary>Varsayılan kaynak sayısı.</summary>
    private const int DefaultLimit = 100;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Kaynak envanteri";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Kapsam içindeki log kaynaklarını listeler. `total` kapsamdaki tüm kaynakları sayar; "
        + "`truncated` doğruysa liste `limit` yüzünden kesilmiştir.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} }
          },
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "total":     { "type": "integer", "minimum": 0 },
            "truncated": { "type": "boolean" },
            "sources": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "source_id":              { "type": "string" },
                  "owner_group":            { "type": "string" },
                  "vendor":                 { "type": "string" },
                  "product":                { "type": "string" },
                  "parser_id":              { "type": ["string", "null"] },
                  "source_class":           { "type": "string" },
                  "enabled":                { "type": "boolean" },
                  "is_known_to_dispatcher": { "type": "boolean" },
                  "created_at":             { "type": "string", "format": "date-time" }
                },
                "required": [
                  "source_id", "owner_group", "vendor", "product", "parser_id",
                  "source_class", "enabled", "is_known_to_dispatcher", "created_at"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["total", "truncated", "sources"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var limit = invocation.Optional("limit", DefaultLimit);

        if (limit is < 1 or > MaxLimit)
        {
            throw new McpToolArgumentException(
                "limit",
                string.Create(CultureInfo.InvariantCulture, $"1 ile {MaxLimit} arasında olmalı"));
        }

        // Kapsam ÇAĞRI BAŞINA açılıyor; gerekçe `ProductReadTool` belgesinde.
        await using var services = scopes.CreateAsyncScope();

        var found = await services.ServiceProvider
            .GetRequiredService<IScopedQuery>()
            .SearchSourcesAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        return McpToolResult.Structured(Shape(found, limit));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SourceSummary[] sample =
        [
            new(
                "fw-edge-01",
                "network/core",
                PeerAddress: "203.0.113.9",
                Hostname: "fw-edge-01.example",
                Vendor: "cisco",
                Product: "asa",
                ParserId: "cisco-asa-v3",
                Encoding: "auto",
                SourceClass: "firewall",
                Enabled: true,
                IsKnownToDispatcher: true,
                CreatedAt: new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)),

            // İkinci satır BİLEREK parser'sız: `parser_id`'nin `null` dalı da
            // şemaya karşı doğrulansın. Tek satırlık bir örnek o dalı hiç
            // sınamazdı.
            new(
                "sw-lab-07",
                "network/lab",
                PeerAddress: null,
                Hostname: null,
                Vendor: string.Empty,
                Product: string.Empty,
                ParserId: null,
                Encoding: "auto",
                SourceClass: "default",
                Enabled: false,
                IsKnownToDispatcher: false,
                CreatedAt: new DateTimeOffset(2026, 8, 20, 14, 30, 0, TimeSpan.Zero)),
        ];

        // Örnek kesilmeyi de gösteriyor: `limit` iki satırın altında.
        return ValueTask.FromResult(McpToolResult.Structured(Shape(sample, limit: 1)));
    }

    private static Payload Shape(IReadOnlyList<SourceSummary> found, int limit) => new(
        found.Count,
        found.Count > limit,
        [.. found.Take(limit).Select(Row.From)]);

    /// <summary>Yükün tek satırı — <c>SourceSummary</c> değil (§8).</summary>
    private sealed record Row(
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("vendor")] string Vendor,
        [property: JsonPropertyName("product")] string Product,
        [property: JsonPropertyName("parser_id")] string? ParserId,
        [property: JsonPropertyName("source_class")] string SourceClass,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("is_known_to_dispatcher")] bool IsKnownToDispatcher,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
    {
        internal static Row From(SourceSummary source) => new(
            source.SourceId,
            source.OwnerGroup,
            source.Vendor,
            source.Product,

            // Boş dize yerine `null`: "parser bağlı değil" ile "parser adı boş"
            // aynı şey değil ve dispatcher'ın birinci kademesi tam bu ayrıma
            // bakıyor.
            string.IsNullOrEmpty(source.ParserId) ? null : source.ParserId,
            source.SourceClass,
            source.Enabled,
            source.IsKnownToDispatcher,
            source.CreatedAt);
    }

    private sealed record Payload(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("sources")] IReadOnlyList<Row> Sources);
}
