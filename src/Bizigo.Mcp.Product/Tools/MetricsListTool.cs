using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Mcp.Product.Tools;

/// <summary>
/// <c>metrics.list</c> — Kapsam içindeki metrik telemetri kayıtlarını listeler.
/// Bütçe: 1..1000 kayıt, max 1MB UTF-8 yük.
/// </summary>
public sealed class MetricsListTool(IServiceScopeFactory scopes) : ProductReadTool
{
    public const string ToolIdentifier = "metrics.list";
    public const int MaxLimit = 1000;
    public const int DefaultLimit = 100;
    public const int MaxResponseBytes = 1_048_576;

    public override string ToolName => ToolIdentifier;
    public override string ToolTitle => "Metrik listesi";
    public override string ToolDescription =>
        "Kapsam içindeki metrik kayıtlarını listeler. `limit` 1 ile 1000 arasındadır. "
        + "Sayfalama için yanıttaki `cursor` değerini verin.";

    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "name":         { "type": "string" },
            "service_name": { "type": "string" },
            "kind":         { "type": "string" },
            "resource_id":  { "type": "string" },
            "from_nano":    { "type": "integer", "minimum": 0 },
            "to_nano":      { "type": "integer", "minimum": 0 },
            "owner_groups": { "type": "array", "items": { "type": "string" } },
            "limit":        { "type": "integer", "minimum": 1, "maximum": {{MaxLimit}} },
            "cursor":       { "type": "string", "minLength": 1 }
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
                  "logical_id":     { "type": "string" },
                  "name":           { "type": "string" },
                  "service_name":   { "type": "string" },
                  "kind":           { "type": "string" },
                  "unit":           { "type": "string" },
                  "time_unix_nano": { "type": "string" },
                  "owner_group":    { "type": "string" }
                },
                "required": [
                  "logical_id", "name", "service_name", "kind", "time_unix_nano", "owner_group"
                ],
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

        var name = invocation.Optional<string>("name", null!);
        var serviceName = invocation.Optional<string>("service_name", null!);
        var kind = invocation.Optional<string>("kind", null!);
        var resourceId = invocation.Optional<string>("resource_id", null!);
        var cursor = invocation.Optional<string>("cursor", null!);

        decimal fromNano = 0;
        if (invocation.Arguments.TryGetValue("from_nano", out var fn) && fn.TryGetDecimal(out var fnVal))
        {
            fromNano = fnVal;
        }

        decimal toNano = (decimal)ulong.MaxValue;
        if (invocation.Arguments.TryGetValue("to_nano", out var tn) && tn.TryGetDecimal(out var tnVal))
        {
            toNano = tnVal;
        }

        IReadOnlyList<string>? ownerGroups = null;
        if (invocation.Arguments.TryGetValue("owner_groups", out var og) && og.ValueKind == JsonValueKind.Array)
        {
            ownerGroups = og.EnumerateArray().Select(static e => e.GetString() ?? "").Where(static s => !string.IsNullOrEmpty(s)).ToArray();
        }

        var query = new TelemetryQuery
        {
            Signal = TelemetrySignal.Metrics,
            FromNano = fromNano,
            ToNano = toNano,
            Name = name,
            ServiceName = serviceName,
            Kind = kind,
            ResourceId = resourceId,
            OwnerGroups = ownerGroups,
            Limit = limit,
            Cursor = cursor
        };

        await using var services = scopes.CreateAsyncScope();
        var scopedQuery = services.ServiceProvider.GetRequiredService<IScopedQuery>();

        var page = await scopedQuery.SearchTelemetryAsync(query, scope, cancellationToken);
        var state = page.Status.ToString();

        var items = new List<object>();
        foreach (var rec in page.Records)
        {
            items.Add(new
            {
                logical_id = rec.LogicalId,
                name = rec.Name,
                service_name = rec.ServiceName,
                kind = rec.Kind,
                unit = rec.Unit,
                time_unix_nano = rec.TimeUnixNano.ToString(CultureInfo.InvariantCulture),
                owner_group = rec.Owner.OwnerGroup
            });
        }

        return McpToolResult.Structured(new
        {
            state,
            items,
            cursor = page.Cursor,
            partial = page.Partial
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
                    logical_id = "sample-metric-1",
                    name = "http.server.request.duration",
                    service_name = "api-gateway",
                    kind = "Histogram",
                    unit = "ms",
                    time_unix_nano = "1726300000000000000",
                    owner_group = "platform"
                }
            },
            cursor = (string?)null,
            partial = false
        }));
    }
}
