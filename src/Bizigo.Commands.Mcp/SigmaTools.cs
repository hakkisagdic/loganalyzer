using System.Text.Json;
using System.Text.Json.Serialization;

using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Bizigo.Mcp;

namespace Bizigo.Commands.Mcp;

/// <summary>
/// <c>sigma.plan</c> — manifestin ne getireceğini <b>hiçbir şey yazmadan</b>
/// söyler.
///
/// <para>
/// <b>Bu araç, komut çekirdeğinin bölünmesinin kendisi.</b> Yazan yarı
/// (<c>sigma sync</c>) gerekçeli muafiyet; okuyan yarı burada ilan ediliyor.
/// Bölünme M02'de icat edilmedi — <c>SigmaSyncCommandHandler.Plan</c> bu depoda
/// zaten saftı ve ilan edilmemesi için bir sebep yoktu.
/// </para>
/// </summary>
public sealed class SigmaPlanTool : CommandTool
{
    /// <summary>Yeni bir örnek.</summary>
    public SigmaPlanTool()
        : base("sigma.plan")
    {
    }

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "manifest_path": { "type": "string" }
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
            "manifest_path": { "type": "string" },
            "total_rules":   { "type": "integer", "minimum": 0 },
            "planned":       { "type": "integer", "minimum": 0 },
            "gated":         { "type": "integer", "minimum": 0 },
            "skipped_failed": { "type": "integer", "minimum": 0 },
            "decisions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "rule_id":      { "type": "string" },
                  "action":       { "type": "string" },
                  "status":       { "type": "string" },
                  "gated_reason": { "type": "string" }
                },
                "required": ["rule_id", "action", "status", "gated_reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["manifest_path", "total_rules", "planned", "gated", "skipped_failed", "decisions"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override async ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var outcome = await SigmaCommands
            .PlanAsync(
                invocation.Optional("manifest_path", SigmaCommands.DefaultManifest)!,
                cancellationToken)
            .ConfigureAwait(false);

        return outcome.Ok ? McpToolResult.Structured(Shape(outcome.Payload)) : Failure(outcome.Failure);
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            new SigmaPlanOutcome(
                SigmaCommands.DefaultManifest,
                TotalRules: 2,
                [
                    new SigmaSyncDecision(
                        "ornek-kural", SigmaSyncAction.Create, AlertRuleStatus.Disabled, string.Empty),
                ]))));
    }

    private static Payload Shape(SigmaPlanOutcome outcome) => new(
        outcome.ManifestPath,
        outcome.TotalRules,
        outcome.Decisions.Count,
        outcome.Decisions.Count(static d => d.Status == AlertRuleStatus.Gated),

        // `failed` kuralların atlandığı SÖYLENİYOR: sessiz bir fark,
        // "manifest 24 dedi, plan 21 gösterdi" sorusunu cevapsız bırakır.
        outcome.TotalRules - outcome.Decisions.Count,
        [
            .. outcome.Decisions.Select(d => new DecisionPayload(
                d.RuleId, d.Action.ToString(), d.Status.ToString(), d.GatedReason)),
        ]);

    private sealed record Payload(
        [property: JsonPropertyName("manifest_path")] string ManifestPath,
        [property: JsonPropertyName("total_rules")] int TotalRules,
        [property: JsonPropertyName("planned")] int Planned,
        [property: JsonPropertyName("gated")] int Gated,
        [property: JsonPropertyName("skipped_failed")] int SkippedFailed,
        [property: JsonPropertyName("decisions")] IReadOnlyList<DecisionPayload> Decisions);

    private sealed record DecisionPayload(
        [property: JsonPropertyName("rule_id")] string RuleId,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("gated_reason")] string GatedReason);
}
