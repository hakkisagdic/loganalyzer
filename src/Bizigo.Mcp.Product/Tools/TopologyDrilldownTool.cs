using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>topology.drilldown</c> — Topoloji komşuluk, yol (path) ve ortak ata (ancestor) ilişkilerini inceler.
/// Bütçe: 1..200 kayıt.
/// </summary>
public sealed class TopologyDrilldownTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "topology.drilldown";
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Topoloji ayrıntılı inceleme";
    public override string ToolDescription =>
        "Topoloji düğümünün komşuluklarını, iki düğüm arasındaki yolu veya ortak atalarını inceler. `limit` 1 ile 200 arasındadır.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "source_id":        { "type": "string", "minLength": 1 },
            "target_id":        { "type": "string" },
            "mode":             { "type": "string", "enum": ["neighbors", "path", "ancestors"] },
            "as_of_unix_nano":  { "type": ["string", "number"] },
            "limit":            { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "cursor":           { "type": "string", "minLength": 1 }
          },
          "required": ["source_id"],
          "additionalProperties": false
        }
        """);

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "state":  { "type": "string" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":       { "type": "string" },
                  "type":     { "type": "string" },
                  "relation": { "type": "string" }
                },
                "required": ["id"],
                "additionalProperties": true
              }
            },
            "cursor": { "type": ["string", "null"] },
            "reason": { "type": ["string", "null"] }
          },
          "required": ["state", "items", "cursor", "reason"],
          "additionalProperties": false
        }
        """);

    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var limit = invocation.Optional("limit", DefaultLimit);
        if (limit is < 1 or > MaxLimit)
        {
            throw new McpToolArgumentException("limit", $"1 ile {MaxLimit} arasında olmalı");
        }

        var sourceId = invocation.Required<string>("source_id");
        var targetId = invocation.Optional<string>("target_id", null!);
        var mode = invocation.Optional<string>("mode", "neighbors");
        var cursor = invocation.Optional<string>("cursor", null!);

        decimal asOf = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000m;
        if (invocation.Arguments.TryGetValue("as_of_unix_nano", out var ao))
        {
            if (ao.ValueKind == JsonValueKind.Number && ao.TryGetDecimal(out var d)) asOf = d;
            else if (ao.ValueKind == JsonValueKind.String && decimal.TryParse(ao.GetString(), CultureInfo.InvariantCulture, out var ds)) asOf = ds;
        }

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        if (mode == "path")
        {
            if (string.IsNullOrEmpty(targetId))
            {
                throw new McpToolArgumentException("target_id", "Path modu için hedef düğüm (`target_id`) gereklidir.");
            }

            var pathResult = await scopedQuery.GetTopologyPathAsync(
                new TopologyPathQuery(sourceId, targetId, asOf, PageSize: limit, Cursor: cursor),
                scope,
                cancellationToken);

            if (pathResult.Status != TopologyGraphResultStatus.Found)
            {
                return McpToolResult.Structured(new
                {
                    state = pathResult.Status == TopologyGraphResultStatus.NotVerified ? "NotVerified" : "Unreachable",
                    items = Array.Empty<object>(),
                    cursor = (string?)null,
                    reason = pathResult.Reason ?? pathResult.Status.ToString()
                });
            }

            var items = pathResult.Nodes.Select(nodeId => (object)new
            {
                id = nodeId,
                type = "node",
                relation = "path_step"
            }).ToArray();

            return McpToolResult.Structured(new
            {
                state = "Data",
                items,
                cursor = pathResult.Cursor,
                reason = (string?)null
            });
        }
        else if (mode == "ancestors")
        {
            var ancestorResult = await scopedQuery.GetTopologyCommonAncestorAsync(
                new TopologyCommonAncestorQuery([sourceId, targetId ?? sourceId], asOf),
                scope,
                cancellationToken);

            if (ancestorResult.Status != TopologyGraphResultStatus.Found || ancestorResult.NodeId is null)
            {
                return McpToolResult.Structured(new
                {
                    state = ancestorResult.Status == TopologyGraphResultStatus.NotVerified ? "NotVerified" : "Empty",
                    items = Array.Empty<object>(),
                    cursor = (string?)null,
                    reason = ancestorResult.Status.ToString()
                });
            }

            return McpToolResult.Structured(new
            {
                state = "Data",
                items = new object[]
                {
                    new { id = ancestorResult.NodeId, type = "node", relation = "common_ancestor" }
                },
                cursor = (string?)null,
                reason = (string?)null
            });
        }
        else
        {
            var neighborhood = await scopedQuery.GetTopologyNeighborhoodAsync(
                new TopologyNeighborhoodQuery(sourceId, asOf, PageSize: limit, Cursor: cursor),
                scope,
                cancellationToken);

            var items = new List<object>();
            foreach (var neighbor in neighborhood.Neighbors)
            {
                items.Add(new
                {
                    id = neighbor.NodeId,
                    type = "node",
                    relation = neighbor.Relation.ToString()
                });
            }

            return McpToolResult.Structured(new
            {
                state = items.Count > 0 ? "Data" : "Empty",
                items,
                cursor = neighborhood.Cursor,
                reason = items.Count == 0 ? "NoNeighbors" : (string?)null
            });
        }
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            state = "Data",
            items = new object[]
            {
                new
                {
                    id = "service-order",
                    type = "node",
                    relation = "neighbor"
                }
            },
            cursor = (string?)null,
            reason = (string?)null
        }));
    }
}
