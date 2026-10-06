using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>anomaly.policies.list</c> — Kapsam içindeki anomali politikalarını listeler.
/// </summary>
public sealed class AnomalyPoliciesListTool(IDbContextFactory<ControlPlaneDbContext> factory) : ProductReadTool
{
    public const string ToolIdentifier = "anomaly.policies.list";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Anomali politikaları listesi";
    public override string ToolDescription => "Kapsam altındaki S4 anomali politikalarını listeler.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "state": { "type": "string", "enum": ["Enabled", "Disabled"] },
            "signal": { "type": "string" },
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
            "policies": {
              "type": "array",
              "items": {
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
                  "state": { "type": "string" },
                  "version": { "type": "integer" }
                }
              }
            }
          },
          "required": ["policies"]
        }
        """);

    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.IsEmpty)
        {
            return McpToolResult.Structured(new { policies = Array.Empty<object>() });
        }

        string? stateFilter = null;
        string? signalFilter = null;
        int limit = 100;

        if (invocation.Arguments.TryGetValue("state", out var stEl) && stEl.ValueKind == JsonValueKind.String)
            stateFilter = stEl.GetString();
        if (invocation.Arguments.TryGetValue("signal", out var sigEl) && sigEl.ValueKind == JsonValueKind.String)
            signalFilter = sigEl.GetString();
        if (invocation.Arguments.TryGetValue("limit", out var limEl) && limEl.TryGetInt32(out var l) && l > 0)
            limit = Math.Min(l, 500);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.AnomalyPolicies.AsNoTracking();

        if (!scope.IsUnrestricted)
        {
            query = query.Where(p => scope.OwnerGroups.Contains(p.OwnerGroup));
        }
        if (!string.IsNullOrWhiteSpace(stateFilter))
        {
            query = query.Where(p => p.State == stateFilter);
        }
        if (!string.IsNullOrWhiteSpace(signalFilter))
        {
            query = query.Where(p => p.Signal == signalFilter);
        }

        var items = await query.OrderByDescending(p => p.CreatedAt).Take(limit).ToListAsync(cancellationToken);

        var list = items.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            owner_group = p.OwnerGroup,
            signal = p.Signal,
            target = p.Target,
            event_window_seconds = p.EventWindowSeconds,
            baseline_window_seconds = p.BaselineWindowSeconds,
            sensitivity = p.Sensitivity,
            min_samples = p.MinSamples,
            state = p.State,
            version = p.Version
        }).ToArray();

        return McpToolResult.Structured(new { policies = list });
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            policies = new[]
            {
                new
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
                    state = "Enabled",
                    version = 1
                }
            }
        }));
    }
}
