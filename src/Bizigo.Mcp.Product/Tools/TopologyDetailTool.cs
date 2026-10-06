using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>topology.detail</c> — Topoloji düğümü veya kenarının detayını getirir.
/// </summary>
public sealed class TopologyDetailTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "topology.detail";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Topoloji eleman detayı";
    public override string ToolDescription =>
        "Belirtilen topoloji düğüm veya kenarının kökenini (declared/observed), güven skorunu ve detayını getirir.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "id":               { "type": "string", "minLength": 1 },
            "type":             { "type": "string", "enum": ["node", "edge"] },
            "as_of_unix_nano":  { "type": ["string", "number"] }
          },
          "required": ["id", "type"],
          "additionalProperties": false
        }
        """);

    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "state": { "type": "string" },
            "data": {
              "type": ["object", "null"],
              "properties": {
                "id":                         { "type": "string" },
                "type":                       { "type": "string" },
                "kind":                       { "type": "string" },
                "owner_group":                { "type": "string" },
                "provenance":                 { "type": "string" },
                "confidence":                 { "type": ["number", "null"] },
                "effective_expiry_unix_nano": { "type": ["string", "null"] }
              },
              "additionalProperties": true
            },
            "related_links": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "signal":    { "type": "string" },
                  "target_id": { "type": "string" },
                  "status":    { "type": "string" },
                  "reason":    { "type": ["string", "null"] }
                },
                "required": ["signal", "target_id", "status"],
                "additionalProperties": false
              }
            }
          },
          "required": ["state", "data", "related_links"],
          "additionalProperties": false
        }
        """);

    protected internal override async ValueTask<McpToolResult> ExecuteScopedAsync(
        McpToolInvocation invocation,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        var id = invocation.Required<string>("id");
        var type = invocation.Required<string>("type");

        decimal asOf = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000m;
        if (invocation.Arguments.TryGetValue("as_of_unix_nano", out var ao))
        {
            if (ao.ValueKind == JsonValueKind.Number && ao.TryGetDecimal(out var d)) asOf = d;
            else if (ao.ValueKind == JsonValueKind.String && decimal.TryParse(ao.GetString(), CultureInfo.InvariantCulture, out var ds)) asOf = ds;
        }

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        var links = new List<object>();

        if (type == "edge")
        {
            var detail = await scopedQuery.GetTopologyEdgeAsync(id, asOf, scope, cancellationToken);
            if (detail is null)
            {
                return McpToolResult.Structured(new
                {
                    state = "Empty",
                    data = (object?)null,
                    related_links = Array.Empty<object>()
                });
            }

            var edge = detail.Edge;
            foreach (var ev in detail.Evidence)
            {
                links.Add(new
                {
                    signal = "trace",
                    target_id = ev.TraceLogicalId,
                    status = "Verified",
                    reason = (string?)null
                });
            }

            return McpToolResult.Structured(new
            {
                state = "Data",
                data = (object?)new
                {
                    id = edge.Id,
                    type = "edge",
                    kind = edge.Relation.ToString(),
                    owner_group = edge.FromOwnerGroup,
                    provenance = edge.Provenance.ToString(),
                    confidence = (double?)edge.Confidence,
                    effective_expiry_unix_nano = edge.EffectiveExpiry?.ToString(CultureInfo.InvariantCulture)
                },
                related_links = links
            });
        }
        else
        {
            var node = await scopedQuery.GetTopologyNodeAsync(id, asOf, scope, cancellationToken);
            if (node is null)
            {
                return McpToolResult.Structured(new
                {
                    state = "Empty",
                    data = (object?)null,
                    related_links = Array.Empty<object>()
                });
            }

            return McpToolResult.Structured(new
            {
                state = "Data",
                data = (object?)new
                {
                    id = node.Id,
                    type = "node",
                    kind = node.Kind.ToString(),
                    owner_group = node.OwnerGroup,
                    provenance = "declared",
                    confidence = (double?)1.0,
                    effective_expiry_unix_nano = node.ValidToUnixNano?.ToString(CultureInfo.InvariantCulture)
                },
                related_links = links
            });
        }
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            state = "Data",
            data = (object?)new
            {
                id = "service-payment",
                type = "node",
                kind = "Service",
                owner_group = "payments",
                provenance = "declared",
                confidence = 1.0,
                effective_expiry_unix_nano = (string?)null
            },
            related_links = Array.Empty<object>()
        }));
    }
}
