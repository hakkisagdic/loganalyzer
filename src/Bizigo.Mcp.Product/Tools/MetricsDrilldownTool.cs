using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>metrics.drilldown</c> — Metriğin zaman serisi noktalarını ve alt kırılımlarını inceler.
/// Bütçe: 1..1000 kayıt.
/// </summary>
public sealed class MetricsDrilldownTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "metrics.drilldown";
    public const int MaxLimit = 1000;
    public const int DefaultLimit = 100;

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Metrik ayrıntılı inceleme";
    public override string ToolDescription =>
        "Bir metriğe ait zaman serisi noktalarını veya alt kırılımlarını listeler. `limit` 1 ile 1000 arasındadır.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "source_id": { "type": "string", "minLength": 1 },
            "as_of":     { "type": ["string", "integer"] },
            "limit":     { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "cursor":    { "type": "string", "minLength": 1 }
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
                  "id":             { "type": "string" },
                  "time_unix_nano": { "type": "string" },
                  "value":          { "type": "string" },
                  "kind":           { "type": "string" }
                },
                "required": ["id", "time_unix_nano"],
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
        var cursor = invocation.Optional<string>("cursor", null!);

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        // İlgili metric serisini ara
        var query = new TelemetryQuery
        {
            Signal = TelemetrySignal.Metrics,
            FromNano = 0,
            ToNano = (decimal)ulong.MaxValue,
            Name = sourceId,
            Limit = limit,
            Cursor = cursor
        };

        var page = await scopedQuery.SearchTelemetryAsync(query, scope, cancellationToken);
        var items = new List<object>();

        foreach (var rec in page.Records)
        {
            items.Add(new
            {
                id = rec.LogicalId,
                time_unix_nano = rec.TimeUnixNano.ToString(CultureInfo.InvariantCulture),
                value = rec.SeriesKey,
                kind = rec.Kind
            });
        }

        var state = page.Status.ToString();
        var reason = items.Count == 0 ? "NotVerified" : null;

        return McpToolResult.Structured(new
        {
            state,
            items,
            cursor = page.Cursor,
            reason
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
                    id = "sample-point-1",
                    time_unix_nano = "1726300000000000000",
                    value = "42.5",
                    kind = "Gauge"
                }
            },
            cursor = (string?)null,
            reason = (string?)null
        }));
    }
}
