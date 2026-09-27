namespace Bizigo.Contracts;

public enum TelemetryResultStatus { Data, Empty, NeverFed, Failed }

/// <summary>
/// Half-open event-time window; spans use start time, not overlap. Decimal
/// bounds are integral nanos and can express UInt64.MaxValue + 1 exactly.
/// Pagination is keyset traversal, not a database snapshot across requests.
/// </summary>
public sealed record TelemetryQuery
{
    public required TelemetrySignal Signal { get; init; }
    public required decimal FromNano { get; init; }
    public required decimal ToNano { get; init; }
    public string? ResourceId { get; init; }
    public string? Name { get; init; }
    public string? ServiceName { get; init; }
    public string? Kind { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public int? Status { get; init; }
    public IReadOnlyList<string>? OwnerGroups { get; init; }
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
    public const int MaxBytes = 4 * 1024 * 1024;

    public void Validate()
    {
        if (!Enum.IsDefined(Signal) || FromNano < 0 || ToNano > (decimal)ulong.MaxValue + 1
            || FromNano >= ToNano || FromNano != decimal.Truncate(FromNano) || ToNano != decimal.Truncate(ToNano)
            || ToNano - FromNano > 31 * 86400000000000m || Limit is < 1 or > 1000
            || Status is < 0 or > 2 || OwnerGroups is { Count: > 256 }
            || new[] { ResourceId, Name, ServiceName, Kind, TraceId, SpanId }.Any(s => s is { Length: > 1024 })
            || OwnerGroups?.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 64) == true
            || Cursor is { Length: > 4096 })
            throw new ArgumentException("Invalid or unbounded telemetry query.");
        if (TraceId is not null && (TraceId.Length != 32 || !TraceId.All(Uri.IsHexDigit)))
            throw new ArgumentException("Trace ID must contain 32 hexadecimal characters.");
        if (SpanId is not null && (SpanId.Length != 16 || !SpanId.All(Uri.IsHexDigit)))
            throw new ArgumentException("Span ID must contain 16 hexadecimal characters.");
    }
}

public interface ITelemetryResult
{
    TelemetryResultStatus Status { get; }
    long RowCount { get; }
    bool Partial { get; }
}

public sealed record TelemetryPage(TelemetryResultStatus Status, IReadOnlyList<TelemetryRecord> Records,
    bool Partial = false, string? Cursor = null, string? Error = null) : ITelemetryResult
{
    public long RowCount => Records.Count;
}

public sealed record TelemetryCount(TelemetryResultStatus Status, long? Count, string? Error = null) : ITelemetryResult
{
    public long RowCount => Count ?? 0;
    public bool Partial => false;
}

public sealed record TelemetrySummary(string Key, long Count, ulong FirstNano, ulong LastNano, TelemetryRecord Last);
public sealed record TelemetrySummaryPage(TelemetryResultStatus Status, IReadOnlyList<TelemetrySummary> Groups,
    bool Partial = false, string? Cursor = null, string? Error = null) : ITelemetryResult
{
    public long RowCount => Groups.Count;
}
