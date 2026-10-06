using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>topology.list</c> — Kapsam içindeki topoloji düğüm veya kenarlarını listeler.
/// Bütçe: 1..200 kayıt, max 1MB UTF-8 yük.
/// </summary>
public sealed class TopologyListTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "topology.list";
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Topoloji listesi";
    public override string ToolDescription =>
        "Kapsam içindeki topoloji düğüm ve kenarlarını listeler. `limit` 1 ile 200 arasındadır.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "kind":             { "type": "string", "enum": ["node", "edge", "all"] },
            "relation":         { "type": "string" },
            "provenance":       { "type": "string", "enum": ["declared", "observed", "all"] },
            "as_of_unix_nano":  { "type": ["string", "number"] },
            "limit":            { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "cursor":           { "type": "string", "minLength": 1 }
          },
          "additionalProperties": false
        }
        """);

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "state":   { "type": "string" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id":           { "type": "string" },
                  "type":         { "type": "string" },
                  "kind":         { "type": "string" },
                  "owner_group":  { "type": "string" },
                  "provenance":   { "type": "string" },
                  "display_name": { "type": "string" }
                },
                "required": ["id", "type", "owner_group"],
                "additionalProperties": true
              }
            },
            "cursor":  { "type": ["string", "null"] },
            "partial": { "type": "boolean" }
          },
          "required": ["state", "items", "cursor", "partial"],
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

        var kind = invocation.Optional<string>("kind", "all");
        var cursor = invocation.Optional<string>("cursor", null!);

        decimal asOf = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000m;
        if (invocation.Arguments.TryGetValue("as_of_unix_nano", out var ao))
        {
            if (ao.ValueKind == JsonValueKind.Number && ao.TryGetDecimal(out var d)) asOf = d;
            else if (ao.ValueKind == JsonValueKind.String && decimal.TryParse(ao.GetString(), CultureInfo.InvariantCulture, out var ds)) asOf = ds;
        }

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        var items = new List<object>();
        string? nextCursor = null;

        if (kind == "edge")
        {
            var edgeQuery = new TopologyEdgeQuery(asOf, PageSize: limit, Cursor: cursor);
            var page = await scopedQuery.SearchTopologyEdgesAsync(edgeQuery, scope, cancellationToken);
            nextCursor = page.Cursor;
            foreach (var edge in page.Items)
            {
                items.Add(new
                {
                    id = edge.Id,
                    type = "edge",
                    kind = edge.Relation.ToString(),
                    owner_group = edge.FromOwnerGroup,
                    provenance = edge.Provenance.ToString(),
                    display_name = $"{edge.FromNode} -> {edge.ToNode}"
                });
            }
        }
        else
        {
            var nodeQuery = new TopologyNodeQuery(asOf, PageSize: limit, Cursor: cursor);
            var page = await scopedQuery.SearchTopologyNodesAsync(nodeQuery, scope, cancellationToken);
            nextCursor = page.Cursor;
            foreach (var node in page.Items)
            {
                items.Add(new
                {
                    id = node.Id,
                    type = "node",
                    kind = node.Kind.ToString(),
                    owner_group = node.OwnerGroup,
                    provenance = "declared",
                    display_name = node.DisplayName
                });
            }
        }

        var state = items.Count > 0 ? "Data" : "Empty";

        return McpToolResult.Structured(new
        {
            state,
            items,
            cursor = nextCursor,
            partial = false
        });
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
                    id = "service-payment",
                    type = "node",
                    kind = "Service",
                    owner_group = "payments",
                    provenance = "declared",
                    display_name = "Payment Service"
                }
            },
            cursor = (string?)null,
            partial = false
        }));
    }
}
