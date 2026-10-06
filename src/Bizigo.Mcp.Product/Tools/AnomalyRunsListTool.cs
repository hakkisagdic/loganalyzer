using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>anomaly.runs.list</c> — Kapsam içindeki anomali koşumlarını listeler.
/// </summary>
public sealed class AnomalyRunsListTool(IDbContextFactory<ControlPlaneDbContext> factory) : ProductReadTool
{
    public const string ToolIdentifier = "anomaly.runs.list";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Anomali değerlendirme koşumları";
    public override string ToolDescription => "Kapsam altındaki anomali değerlendirme geçmişini listeler.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "policy_id": { "type": "string" },
            "status": { "type": "string", "enum": ["Running", "NoSignal", "Triggered", "Suppressed", "Failed"] },
            "limit": { "type": "integer", "minimum": 1, "maximum": 500 }
          },
          "additionalProperties": false
        }
        """);

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "runs": {
              "type": "array",
              "items": {
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
                  "rca_run_id": { "type": ["string", "null"] }
                }
              }
            }
          },
          "required": ["runs"]
        }
        """);

    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.IsEmpty)
        {
            return McpToolResult.Structured(new { runs = Array.Empty<object>() });
        }

        string? policyFilter = null;
        string? statusFilter = null;
        int limit = 100;

        if (invocation.Arguments.TryGetValue("policy_id", out var polEl) && polEl.ValueKind == JsonValueKind.String)
            policyFilter = polEl.GetString();
        if (invocation.Arguments.TryGetValue("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
            statusFilter = stEl.GetString();
        if (invocation.Arguments.TryGetValue("limit", out var limEl) && limEl.TryGetInt32(out var l) && l > 0)
            limit = Math.Min(l, 500);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.AnomalyRuns.AsNoTracking();

        if (!scope.IsUnrestricted)
        {
            query = query.Where(r => scope.OwnerGroups.Contains(r.OwnerGroup));
        }
        if (!string.IsNullOrWhiteSpace(policyFilter))
        {
            query = query.Where(r => r.PolicyId == policyFilter);
        }
        if (!string.IsNullOrWhiteSpace(statusFilter))
        {
            query = query.Where(r => r.Status == statusFilter);
        }

        var items = await query.OrderByDescending(r => r.CreatedAt).Take(limit).ToListAsync(cancellationToken);

        var list = items.Select(r => new
        {
            id = r.Id,
            policy_id = r.PolicyId,
            owner_group = r.OwnerGroup,
            window_start = r.WindowStart.ToString("o"),
            window_end = r.WindowEnd.ToString("o"),
            status = r.Status,
            reason = r.Reason,
            observed_value = r.ObservedValue,
            baseline_value = r.BaselineValue,
            deviation = r.Deviation,
            rca_run_id = r.RcaRunId?.ToString()
        }).ToArray();

        return McpToolResult.Structured(new { runs = list });
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            runs = new[]
            {
                new
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
                    rca_run_id = (string?)"11111111-2222-3333-4444-555555555555"
                }
            }
        }));
    }
}
