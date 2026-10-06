using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>metrics.detail</c> — Belirli bir metriğin kayıpsız detayını getirir.
/// </summary>
public sealed class MetricsDetailTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "metrics.detail";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Metrik detayı";
    public override string ToolDescription =>
        "Belirtilen mantıksal kimliğe (`logical_id`) ait metriğin kayıpsız detayını ve ilişkili linklerini getirir.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "logical_id": { "type": "string", "minLength": 1 },
            "as_of":      { "type": ["string", "integer"] }
          },
          "required": ["logical_id"],
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
                "logical_id":     { "type": "string" },
                "name":           { "type": "string" },
                "service_name":   { "type": "string" },
                "kind":           { "type": "string" },
                "unit":           { "type": "string" },
                "time_unix_nano": { "type": "string" },
                "owner_group":    { "type": "string" },
                "temporality":    { "type": "integer" },
                "monotonic":      { "type": "boolean" },
                "series_key":     { "type": "string" }
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
        var logicalId = invocation.Required<string>("logical_id");

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        var page = await scopedQuery.GetMetricPointAsync(logicalId, scope, cancellationToken);
        if (page.Records.Count == 0)
        {
            return McpToolResult.Structured(new
            {
                state = page.Status == TelemetryResultStatus.NeverFed ? "NeverFed" : "Empty",
                data = (object?)null,
                related_links = Array.Empty<object>()
            });
        }

        var rec = page.Records[0];
        var links = new List<object>();

        if (!string.IsNullOrEmpty(rec.TraceId) && rec.TraceId != "00000000000000000000000000000000")
        {
            links.Add(new
            {
                signal = "trace",
                target_id = rec.TraceId,
                status = "Verified",
                reason = (string?)null
            });
        }

        var data = new Dictionary<string, object?>
        {
            ["logical_id"] = rec.LogicalId,
            ["name"] = rec.Name,
            ["service_name"] = rec.ServiceName,
            ["kind"] = rec.Kind,
            ["unit"] = rec.Unit,
            ["time_unix_nano"] = rec.TimeUnixNano.ToString(CultureInfo.InvariantCulture),
            ["owner_group"] = rec.Owner.OwnerGroup,
            ["temporality"] = rec.Temporality,
            ["monotonic"] = rec.Monotonic,
            ["series_key"] = rec.SeriesKey
        };

        if (rec.Metric is { ValueKind: JsonValueKind.Object } m)
        {
            data["metric"] = m;
        }

        return McpToolResult.Structured(new
        {
            state = page.Status.ToString(),
            data,
            related_links = links
        });
    }

    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpToolResult.Structured(new
        {
            state = "Data",
            data = (object?)new
            {
                logical_id = "sample-metric-1",
                name = "http.server.request.duration",
                service_name = "api-gateway",
                kind = "Histogram",
                unit = "ms",
                time_unix_nano = "1726300000000000000",
                owner_group = "platform",
                temporality = 2,
                monotonic = false,
                series_key = "http.server.request.duration{method=GET}"
            },
            related_links = new object[]
            {
                new
                {
                    signal = "trace",
                    target_id = "4bf92f3577b34da6a3ce929d0e0e4736",
                    status = "Verified",
                    reason = (string?)null
                }
            }
        }));
    }
}
