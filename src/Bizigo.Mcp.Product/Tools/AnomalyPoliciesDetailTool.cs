using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>anomaly.policies.detail</c> — Tek bir anomali politikasının detayını ve son durumunu getirir.
/// </summary>
public sealed class AnomalyPoliciesDetailTool(IDbContextFactory<ControlPlaneDbContext> factory) : ProductReadTool
{
    public const string ToolIdentifier = "anomaly.policies.detail";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Anomali politikası detayı";
    public override string ToolDescription => "Belirtilen anomali politikasının kapsam ve parametre detaylarını getirir.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "minLength": 1 }
          },
          "required": ["id"],
          "additionalProperties": false
        }
        """);

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "policy": {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "name": { "type": "string" },
                "owner_group": { "type": "string" },
                "signal": { "type": "string" },
                "target": { "type": "string" },
                "event_window_seconds": { "type": "integer" },
                "baseline_window_seconds": { "type": "integer" },
                "sensitivity": { "type": "number" },
                "min_samples": { "type": "integer" },
                "zero_baseline_min_absolute": { "type": ["number", "null"] },
                "state": { "type": "string" },
                "version": { "type": "integer" },
                "cadence_seconds": { "type": "integer" },
                "last_evaluated_window_start": { "type": ["string", "null"] }
              }
            }
          }
        }
        """);

    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.IsEmpty)
        {
            return McpToolResult.Failure(new McpToolError("not_found", "Not found or unauthorized."));
        }

        if (!invocation.Arguments.TryGetValue("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id)
        {
            return McpToolResult.Failure(new McpToolError("bad_request", "Missing 'id' parameter."));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var policy = await db.AnomalyPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null || !scope.Allows(policy.OwnerGroup))
        {
            return McpToolResult.Failure(new McpToolError("not_found", "Not found or unauthorized."));
        }

        var res = new
        {
            policy = new
            {
                id = policy.Id,
                name = policy.Name,
                owner_group = policy.OwnerGroup,
                signal = policy.Signal,
                target = policy.Target,
                event_window_seconds = policy.EventWindowSeconds,
                baseline_window_seconds = policy.BaselineWindowSeconds,
                sensitivity = policy.Sensitivity,
                min_samples = policy.MinSamples,
                zero_baseline_min_absolute = policy.ZeroBaselineMinAbsolute,
                state = policy.State,
                version = policy.Version,
                cadence_seconds = policy.CadenceSeconds,
                last_evaluated_window_start = policy.LastEvaluatedWindowStart?.ToString("o")
            }
        };

        return McpToolResult.Structured(res);
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            policy = new
            {
                id = "pol-1",
                name = "Örnek Anomali Politikası",
                owner_group = "network/core",
                signal = "log_event_count",
                target = "fw-01",
                event_window_seconds = 300,
                baseline_window_seconds = 3600,
                sensitivity = 2.0,
                min_samples = 5,
                zero_baseline_min_absolute = (double?)null,
                state = "Enabled",
                version = 1,
                cadence_seconds = 300,
                last_evaluated_window_start = (string?)null
            }
        }));
    }
}
