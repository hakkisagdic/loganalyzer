using System.Text.Json;

namespace Bizigo.Contracts;

/// <summary>Lossless OTLP JSON leaves plus explicitly indexed query columns.</summary>
public sealed record TelemetryRecord(
    int Version, Guid EnvelopeId, string LogicalId, TelemetrySignal Signal,
    string PayloadSha256, string OwnerBindingSha256, TelemetryOwnerBinding Owner,
    ulong TimeUnixNano, string Name, string ServiceName, string Kind, string Unit,
    int Temporality, bool Monotonic, string TraceId, string SpanId, int Status,
    string SeriesKey, JsonElement Resource, JsonElement Scope, string ResourceSchemaUrl,
    string ScopeSchemaUrl, JsonElement? Metric, JsonElement? Span)
{
    public int RetentionDays { get; init; } = 90;
}

public interface ITelemetrySink
{
    Task WriteAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken);
}
