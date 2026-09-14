using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Alerting;
using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>alerts.triggers</c> — tetiklenmiş alarmlar. Ucu
/// <c>GET /v1/alerts/triggers</c> (ölçüldü).
///
/// <para>
/// <b>Bu araç bir kapsam borcunu kapattı.</b> Ölçüldüğünde tetiklenme
/// okumasının kapsam kararı <c>AlertEndpoints.ListTriggersAsync</c> <b>gövdesinde</b>
/// duruyordu: görünür kural kimlikleri orada türetiliyor, filtre orada
/// uygulanıyordu. Tek tüketici REST iken bu görünmez; ikinci tüketici gelince
/// tek seçenek kopyalamak olurdu. Karar <c>AlertRuleService.ResolveTriggerScopeAsync</c>'e
/// taşındı ve <b>REST ucu da artık onu çağırıyor</b> — yani bitti tanımının
/// 7. maddesi (*"kapsam filtresi MCP yüzeyinde de tek kapıdan"*) bu araçta bir
/// iddia değil, iki tüketicisi olan tek bir metot.
/// </para>
///
/// <para>
/// <b>Teslim kayıtları yükte YOK.</b> REST onları taşıyor çünkü ekranın
/// "gönderildi ≠ ulaştı" ayrımını göstermesi gerekiyor. Model için bu bir
/// bildirim altyapısı teşhisi; ayrıca <c>last_error</c> alanı redaksiyondan
/// geçmiş olsa bile serbest metin, ve serbest metnin bu yüzeye girdiği tek yol
/// <c>McpLogText</c> menteşesi olmalı.
/// </para>
/// </summary>
/// <param name="rules">Kapsam kararının ve tetiklenme sorgusunun tek yeri.</param>
public sealed class AlertTriggersTool(AlertRuleService rules) : ProductReadTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "alerts.triggers";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Tetiklenmiş alarmlar";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Kapsam içindeki kuralların tetiklenmelerini en yenisi önce listeler. "
        + "`rule_id` verilirse yalnızca o kural; kural kapsam dışındaysa `not_found`.";

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "rule_id": { "type": "string", "format": "uuid" },
            "limit":   { "type": "integer", "minimum": 1, "maximum": {{AlertRuleService.MaxTriggerLimit}} }
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
            "count": { "type": "integer", "minimum": 0 },
            "triggers": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "trigger_id":       { "type": "string", "format": "uuid" },
                  "rule_id":          { "type": "string", "format": "uuid" },
                  "rule_name":        { "type": "string" },
                  "fired_at":         { "type": "string", "format": "date-time" },
                  "window_from":      { "type": "string", "format": "date-time" },
                  "window_to":        { "type": "string", "format": "date-time" },
                  "value":            { "type": "number" },
                  "threshold":        { "type": "number" },
                  "source_id":        { "type": ["string", "null"] },
                  "owner_group":      { "type": "string" },
                  "summary":          { "type": "string" },
                  "state":            { "type": "string", "enum": ["open", "closed"] },
                  "closed_at":        { "type": ["string", "null"], "format": "date-time" },
                  "closed_by":        { "type": ["string", "null"] }
                },
                "required": [
                  "trigger_id", "rule_id", "rule_name", "fired_at", "window_from", "window_to",
                  "value", "threshold", "source_id", "owner_group", "summary", "state",
                  "closed_at", "closed_by"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["count", "triggers"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var limit = invocation.Optional("limit", AlertRuleService.DefaultTriggerLimit);

        if (limit is < 1 or > AlertRuleService.MaxTriggerLimit)
        {
            throw new McpToolArgumentException(
                "limit",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"1 ile {AlertRuleService.MaxTriggerLimit} arasında olmalı"));
        }

        var ruleId = invocation.Optional<Guid?>("rule_id");

        var visible = await rules
            .ResolveTriggerScopeAsync(scope, ruleId, cancellationToken)
            .ConfigureAwait(false);

        if (visible is null)
        {
            // İstenen kural bu kapsamda görünmüyor. `not_found` — ve boş liste
            // DEĞİL: sıfır tetiklenme "alarm yok" diye okunurdu, oysa söylenen
            // şey "o kuralı göremiyorsun".
            return McpToolResult.Failure(new McpToolError(
                McpToolError.NotFound,
                "Kural bu kapsamda görünmüyor.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["rule_id"] = ruleId?.ToString() ?? string.Empty,
                }));
        }

        var rows = await rules
            .ListTriggersAsync(visible, limit, cancellationToken)
            .ConfigureAwait(false);

        return McpToolResult.Structured(Shape(rows, visible.Names));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ruleId = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        var fired = new DateTimeOffset(2026, 9, 5, 11, 42, 0, TimeSpan.Zero);

        AlertTriggerEntity[] sample =
        [
            new()
            {
                Id = Guid.Parse("cccccccc-1111-2222-3333-444444444444"),
                RuleId = ruleId,
                FiredAt = fired,
                WindowFrom = fired.AddMinutes(-5),
                WindowTo = fired,
                Value = 340,
                Threshold = 100,
                SourceId = "fw-edge-01",
                OwnerGroup = "network/core",
                Summary = "5 dk içinde 340 olay — sınır > 100.",
                State = AlertTriggerState.Open,
            },

            // İkinci satır BİLEREK kapalı ve kaynaksız: `closed_at`,
            // `closed_by` ve `source_id`'nin HER İKİ dalı da şemaya karşı
            // doğrulansın. Yalnızca açık bir satır, `null` dallarını hiç
            // sınamazdı — ve şemada `["string","null"]` yazmak onları
            // sınamakla aynı şey değil.
            new()
            {
                Id = Guid.Parse("dddddddd-1111-2222-3333-444444444444"),
                RuleId = ruleId,
                FiredAt = fired.AddHours(-3),
                WindowFrom = fired.AddHours(-3).AddMinutes(-5),
                WindowTo = fired.AddHours(-3),
                Value = 210,
                Threshold = 100,
                SourceId = string.Empty,
                OwnerGroup = "network/core",
                Summary = "5 dk içinde 210 olay — sınır > 100.",
                State = AlertTriggerState.Closed,
                ClosedAt = fired.AddHours(-2),
                ClosedBySubject = "analyst.core",
            },
        ];

        var names = new Dictionary<Guid, string>(1) { [ruleId] = "deny sağanağı" };

        return ValueTask.FromResult(McpToolResult.Structured(Shape(sample, names)));
    }

    private static Payload Shape(
        IReadOnlyList<AlertTriggerEntity> rows,
        IReadOnlyDictionary<Guid, string> names) => new(
        rows.Count,
        [.. rows.Select(row => Row.From(row, names))]);

    /// <summary>Yükün tek satırı — <c>AlertTriggerEntity</c> değil (§8).</summary>
    private sealed record Row(
        [property: JsonPropertyName("trigger_id")] Guid TriggerId,
        [property: JsonPropertyName("rule_id")] Guid RuleId,
        [property: JsonPropertyName("rule_name")] string RuleName,
        [property: JsonPropertyName("fired_at")] DateTimeOffset FiredAt,
        [property: JsonPropertyName("window_from")] DateTimeOffset WindowFrom,
        [property: JsonPropertyName("window_to")] DateTimeOffset WindowTo,
        [property: JsonPropertyName("value")] double Value,
        [property: JsonPropertyName("threshold")] double Threshold,
        [property: JsonPropertyName("source_id")] string? SourceId,
        [property: JsonPropertyName("owner_group")] string OwnerGroup,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("closed_at")] DateTimeOffset? ClosedAt,
        [property: JsonPropertyName("closed_by")] string? ClosedBy)
    {
        internal static Row From(
            AlertTriggerEntity trigger,
            IReadOnlyDictionary<Guid, string> names) => new(
            trigger.Id,
            trigger.RuleId,

            // Ad çözülemezse boş dize: kural silinmiş olabilir ve tetiklenme
            // kaydı duruyor. `null` yapmak "adı yok" ile "kural yok"u
            // karıştırırdı; ikisi de anlamsız, ama boş dize şemayı sadeleştiriyor.
            names.TryGetValue(trigger.RuleId, out var name) ? name : string.Empty,
            trigger.FiredAt,
            trigger.WindowFrom,
            trigger.WindowTo,
            trigger.Value,
            trigger.Threshold,

            // Boş dize yerine `null`: eşik/oran kuralları kaynak başına
            // tetiklenmiyor ve boş dize "kaynak adı boş" diye okunurdu.
            string.IsNullOrEmpty(trigger.SourceId) ? null : trigger.SourceId,
            trigger.OwnerGroup,

            // `Summary` ürünün KENDİ cümlesi — sayaç ve eşikten kuruluyor
            // (`AlertEvaluator`), log satırından değil. Ölçüldü: üç kural
            // tipinin ürettiği metinlerin hiçbiri olay gövdesi taşımıyor.
            trigger.Summary,
            JsonNamingPolicy.SnakeCaseLower.ConvertName(trigger.State.ToString()),
            trigger.ClosedAt,
            string.IsNullOrEmpty(trigger.ClosedBySubject) ? null : trigger.ClosedBySubject);
    }

    private sealed record Payload(
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("triggers")] IReadOnlyList<Row> Triggers);
}
