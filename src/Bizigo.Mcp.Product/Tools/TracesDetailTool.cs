using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>traces.detail</c> — Belirli bir izin (trace) tüm span ve hiyerarşi detayını getirir.
/// </summary>
public sealed class TracesDetailTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "traces.detail";

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "İz detayı";
    public override string ToolDescription =>
        "Belirtilen `trace_id`'ye ait izin tüm span'lerini, hiyerarşisini, servis yolunu ve ilişkili linklerini getirir.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "trace_id": { "type": "string", "minLength": 1 },
            "span_id":  { "type": "string" },
            "as_of":    { "type": ["string", "integer"] }
          },
          "required": ["trace_id"],
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
                "trace_id":       { "type": "string" },
                "span_id":        { "type": "string" },
                "parent_span_id": { "type": ["string", "null"] },
                "name":           { "type": "string" },
                "service_name":   { "type": "string" },
                "service_path":   { "type": "string" },
                "time_unix_nano": { "type": "string" },
                "owner_group":    { "type": "string" },
                "status":         { "type": "integer" },
                "spans":          { "type": "array" }
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
        var traceId = invocation.Required<string>("trace_id");
        var spanId = invocation.Optional<string>("span_id", null!);

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        var query = new TelemetryQuery
        {
            Signal = TelemetrySignal.Traces,
            FromNano = 0,
            ToNano = (decimal)ulong.MaxValue,
            TraceId = traceId,
            SpanId = spanId,
            Limit = 1000
        };

        var page = await scopedQuery.GetTraceAsync(query, scope, cancellationToken);
        if (page.Records.Count == 0)
        {
            return McpToolResult.Structured(new
            {
                state = page.Status == TelemetryResultStatus.NeverFed ? "NeverFed" : "Empty",
                data = (object?)null,
                related_links = Array.Empty<object>()
            });
        }

        var root = page.Records[0];
        var spans = new List<object>();
        var serviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rec in page.Records)
        {
            serviceNames.Add(rec.ServiceName);
            string? parentSpanId = null;
            if (rec.Span is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("parentSpanId", out var ps))
            {
                parentSpanId = ps.GetString();
            }

            spans.Add(new
            {
                span_id = rec.SpanId,
                parent_span_id = parentSpanId,
                name = rec.Name,
                service_name = rec.ServiceName,
                time_unix_nano = rec.TimeUnixNano.ToString(CultureInfo.InvariantCulture),
                status = rec.Status
            });
        }

        var servicePath = string.Join(" -> ", serviceNames);

        var data = new Dictionary<string, object?>
        {
            ["trace_id"] = root.TraceId,
            ["span_id"] = root.SpanId,
            ["parent_span_id"] = null,
            ["name"] = root.Name,
            ["service_name"] = root.ServiceName,
            ["service_path"] = servicePath,
            ["time_unix_nano"] = root.TimeUnixNano.ToString(CultureInfo.InvariantCulture),
            ["owner_group"] = root.Owner.OwnerGroup,
            ["status"] = root.Status,
            ["spans"] = spans
        };

        var links = new List<object>();
        // Eğer RCA referansı veya metrik ilişkisi varsa link ekle
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
                trace_id = "4bf92f3577b34da6a3ce929d0e0e4736",
                span_id = "00f067aa0ba902b7",
                parent_span_id = (string?)null,
                name = "GET /api/checkout",
                service_name = "order-service",
                service_path = "order-service -> payment-service",
                time_unix_nano = "1726300000000000000",
                owner_group = "orders",
                status = 1,
                spans = new object[]
                {
                    new
                    {
                        span_id = "00f067aa0ba902b7",
                        parent_span_id = (string?)null,
                        name = "GET /api/checkout",
                        service_name = "order-service",
                        time_unix_nano = "1726300000000000000",
                        status = 1
                    }
                }
            },
            related_links = Array.Empty<object>()
        }));
    }
}
