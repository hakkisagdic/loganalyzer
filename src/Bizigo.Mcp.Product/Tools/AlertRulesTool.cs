using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>alerts.rules</c> — kapsam içindeki alarm kuralları. Ucu
/// <c>GET /v1/alerts/rules</c> (ölçüldü).
///
/// <para>
/// <b>Kapsam kapısı zaten tekti.</b> <c>AlertRuleService.ListAsync(scope)</c>
/// filtreyi kendi içinde uyguluyor ve yorumu bunu açıkça söylüyor:
/// <i>"önemli olan filtrenin BU sınıfta olması, uç katmanında değil."</i> Bu
/// araç aynı çağrıyı yapıyor; ikinci bir filtre yazılmadı.
/// </para>
///
/// <para>
/// <b>Yükte olmayan iki alan ve sebepleri.</b> <c>search_json</c> yok: kuralın
/// kaydedilmiş aramasının içinde tam metin terimleri durabiliyor ve o terimler
/// <b>logdan gelmiş</b> olabilir: bir kuralın filtresi bir kullanıcı adını ya da
/// bir IP'yi literal olarak taşıyorsa, o dize kurumun verisi hakkında bilgidir.
/// <c>sigma_*</c> özetleri de yok: pipeline teşhisi, modelin karar vereceği bir
/// şey değil.
/// </para>
/// </summary>
/// <param name="rules">Kural okumasının kapsam kapısı.</param>
public sealed class AlertRulesTool(AlertRuleService rules) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "alerts.rules";

    /// <summary>Tek çağrıda dönebilecek en fazla kural.</summary>
    private const int MaxLimit = 200;

    /// <summary>Varsayılan kural sayısı.</summary>
    private const int DefaultLimit = 50;

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Alarm kuralları";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Kapsam içindeki alarm kurallarını listeler. `status` `gated` ise kural KOŞMUYOR ve "
        + "`gated_reason` sebebini söyler. `total`/`truncated` kesilmeyi bildirir.";

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
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "rule_id":          { "type": "string", "format": "uuid" },
                  "name":             { "type": "string" },
                  "rule_type":        { "type": "string", "enum": ["threshold", "ratio", "silence"] },
                  "owner_groups":     { "type": "array", "items": { "type": "string" } },
                  "status":           { "type": "string", "enum": ["enabled", "disabled", "gated"] },
                  "gated_reason":     { "type": ["string", "null"] },
                  "source":           { "type": "string", "enum": ["bizigo", "sigma"] },
                  "window_seconds":   { "type": "integer", "minimum": 0 },
                  "interval_seconds": { "type": "integer", "minimum": 0 },
                  "threshold":        { "type": "number" },
                  "comparison":       {
                    "type": "string",
                    "enum": ["greater_than", "greater_than_or_equal", "less_than", "less_than_or_equal"]
                  },
                  "silence_seconds":  { "type": "integer", "minimum": 0 }
                },
                "required": [
                  "rule_id", "name", "rule_type", "owner_groups", "status", "gated_reason",
                  "source", "window_seconds", "interval_seconds", "threshold", "comparison",
                  "silence_seconds"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["total", "truncated", "rules"],
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

        var found = await rules.ListAsync(scope, cancellationToken).ConfigureAwait(false);

        return McpToolResult.Structured(Shape(found, limit));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        AlertRuleEntity[] sample =
        [
            new()
            {
                Id = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
                Name = "deny sağanağı",
                OwnerSubject = "analyst.core",
                OwnerGroups = "network/core,network/edge",
                RuleType = AlertRuleType.Threshold,
                Status = AlertRuleStatus.Enabled,
                WindowSeconds = 300,
                IntervalSeconds = 60,
                Threshold = 100,
                Comparison = AlertComparison.GreaterThan,
            },

            // İkinci kural BİLEREK `gated`: `gated_reason`'ın dize dalı da
            // şemaya karşı doğrulansın. Tek `enabled` satır o dalı hiç
            // sınamazdı ve `null` ile dize arasındaki ayrım ölçülmemiş kalırdı.
            new()
            {
                Id = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444"),
                Name = "sigma: şüpheli oturum",
                OwnerSubject = "pipeline",
                OwnerGroups = "network/core",
                RuleType = AlertRuleType.Silence,
                Status = AlertRuleStatus.Gated,
                GatedReason = "unsupported_field: winlog.event_data.SubjectUserName",
                Source = AlertRuleSource.Sigma,
                WindowSeconds = 900,
                IntervalSeconds = 300,
                SilenceSeconds = 900,
                Comparison = AlertComparison.LessThan,
            },
        ];

        return ValueTask.FromResult(McpToolResult.Structured(Shape(sample, limit: 1)));
    }

    private static Payload Shape(IReadOnlyList<AlertRuleEntity> found, int limit) => new(
        found.Count,
        found.Count > limit,
        [.. found.Take(limit).Select(Row.From)]);

    /// <summary>Yükün tek satırı — <c>AlertRuleEntity</c> değil (§8).</summary>
    private sealed record Row(
        [property: JsonPropertyName("rule_id")] Guid RuleId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("rule_type")] string RuleType,
        [property: JsonPropertyName("owner_groups")] IReadOnlyList<string> OwnerGroups,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("gated_reason")] string? GatedReason,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("window_seconds")] int WindowSeconds,
        [property: JsonPropertyName("interval_seconds")] int IntervalSeconds,
        [property: JsonPropertyName("threshold")] double Threshold,
        [property: JsonPropertyName("comparison")] string Comparison,
        [property: JsonPropertyName("silence_seconds")] int SilenceSeconds)
    {
        internal static Row From(AlertRuleEntity rule) => new(
            rule.Id,
            rule.Name,
            Snake(rule.RuleType.ToString()),

            // Depoda tek kolonda virgülle duruyor; telde DİZİ. Virgüllü dizeyi
            // olduğu gibi vermek, modelin ayırıcıyı tahmin etmesi demek olurdu
            // ve bir grup adında virgül olduğu gün sessizce iki gruba bölünürdü.
            [.. rule.OwnerGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Snake(rule.Status.ToString()),

            // Boş dize yerine `null`: "gated değil" ile "sebep yazılmamış" aynı
            // şey değil, ve kaynağın kendi yorumu sessiz bir "kapalı" rozetinin
            // listeyi çöp kutusuna çevirdiğini yazıyor.
            string.IsNullOrEmpty(rule.GatedReason) ? null : rule.GatedReason,
            Snake(rule.Source.ToString()),
            rule.WindowSeconds,
            rule.IntervalSeconds,
            rule.Threshold,
            Snake(rule.Comparison.ToString()),
            rule.SilenceSeconds);

        /// <summary>
        /// <c>PascalCase</c> → <c>snake_case</c>. Enum adları yükte
        /// <b>ad</b> olarak gidiyor, sayı olarak değil: `2` gövdesine bakan hiç
        /// kimse "gated" demiyor ve enum sırası değiştiğinde sessizce başka bir
        /// durum gösterirdi.
        /// </summary>
        private static string Snake(string pascal) =>
            JsonNamingPolicy.SnakeCaseLower.ConvertName(pascal);
    }

    private sealed record Payload(
        [property: JsonPropertyName("total")] int Total,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("rules")] IReadOnlyList<Row> Rules);
}
