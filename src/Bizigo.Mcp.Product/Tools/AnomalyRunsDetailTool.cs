using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>anomaly.runs.detail</c> — Anomali koşumu detayını ve varsa RCA bağlantısını getirir.
/// </summary>
public sealed class AnomalyRunsDetailTool(IDbContextFactory<ControlPlaneDbContext> factory) : ProductReadTool
{
    public const string ToolIdentifier = "anomaly.runs.detail";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Anomali koşumu detayı";
    public override string ToolDescription => "Belirtilen anomali koşumunun durumunu, gerekçesini, sayısal gözlemlerini ve RCA bağlantısını getirir.";

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
            "run": {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "policy_id": { "type": "string" },
                "owner_group": { "type": "string" },
                "window_start": { "type": "string" },
                "window_end": { "type": "string" },
                "status": { "type": "string" },
                "reason": { "type": ["string", "null"] },
                "observed_value": { "type": ["number", "null"] },
                "baseline_value": { "type": ["number", "null"] },
                "deviation": { "type": ["number", "null"] },
                "rca_run_id": { "type": ["string", "null"] },
                "evaluated_policy_version": { "type": "integer" },
                "created_at": { "type": "string" },
                "completed_at": { "type": ["string", "null"] }
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
        var run = await db.AnomalyRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (run is null || !scope.Allows(run.OwnerGroup))
        {
            return McpToolResult.Failure(new McpToolError("not_found", "Not found or unauthorized."));
        }

        var res = new
        {
            run = new
            {
                id = run.Id,
                policy_id = run.PolicyId,
                owner_group = run.OwnerGroup,
                window_start = run.WindowStart.ToString("o"),
                window_end = run.WindowEnd.ToString("o"),
                status = run.Status,
                reason = run.Reason,
                observed_value = run.ObservedValue,
                baseline_value = run.BaselineValue,
                deviation = run.Deviation,
                rca_run_id = run.RcaRunId?.ToString(),
                evaluated_policy_version = run.EvaluatedPolicyVersion,
                created_at = run.CreatedAt.ToString("o"),
                completed_at = run.CompletedAt?.ToString("o")
            }
        };

        return McpToolResult.Structured(res);
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            run = new
            {
                id = "run-1",
                policy_id = "pol-1",
                owner_group = "network/core",
                window_start = "2026-10-06T12:00:00Z",
                window_end = "2026-10-06T12:05:00Z",
                status = "Triggered",
                reason = (string?)null,
                observed_value = (double?)150.0,
                baseline_value = (double?)50.0,
                deviation = (double?)3.0,
                rca_run_id = (string?)"11111111-2222-3333-4444-555555555555",
                evaluated_policy_version = 1,
                created_at = "2026-10-06T12:05:01Z",
                completed_at = (string?)"2026-10-06T12:05:02Z"
            }
        }));
    }
}
