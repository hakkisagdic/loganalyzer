using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>traces.drilldown</c> — Bir izin alt span'lerini, event'lerini veya ilişkili kayıtlarını listeler.
/// Bütçe: 1..1000 kayıt.
/// </summary>
public sealed class TracesDrilldownTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "traces.drilldown";
    public const int MaxLimit = 1000;
    public const int DefaultLimit = 100;

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "İz ayrıntılı inceleme";
    public override string ToolDescription =>
        "Bir izin alt span'lerini, event'lerini ve ilişkili ayrıntılarını listeler. `limit` 1 ile 1000 arasındadır.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "trace_id": { "type": "string", "minLength": 1 },
            "span_id":  { "type": "string" },
            "as_of":    { "type": ["string", "integer"] },
            "limit":    { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "cursor":   { "type": "string", "minLength": 1 }
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
            "state":  { "type": "string" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "span_id":        { "type": "string" },
                  "name":           { "type": "string" },
                  "service_name":   { "type": "string" },
                  "time_unix_nano": { "type": "string" }
                },
                "required": ["span_id", "name", "service_name", "time_unix_nano"],
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
            Limit = limit
        };

        var page = await scopedQuery.GetTraceAsync(query, scope, cancellationToken);
        var items = new List<object>();

        foreach (var rec in page.Records)
        {
            items.Add(new
            {
                span_id = rec.SpanId,
                name = rec.Name,
                service_name = rec.ServiceName,
                time_unix_nano = rec.TimeUnixNano.ToString(CultureInfo.InvariantCulture)
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
                    span_id = "00f067aa0ba902b7",
                    name = "POST /api/payment",
                    service_name = "payment-service",
                    time_unix_nano = "1726300000000000000"
                }
            },
            cursor = (string?)null,
            reason = (string?)null
        }));
    }
}
