using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence;

public sealed record ExcludedKindCount(
    [property: JsonPropertyName("kind")] EvidenceKind Kind,
    [property: JsonPropertyName("count")] long? Count,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record ExcludedInputRecords(
    [property: JsonPropertyName("kinds")] IReadOnlyList<ExcludedKindCount> Kinds,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("legacy_reported_count")] long? LegacyReportedCount = null)
{
    [JsonPropertyName("measured")]
    public bool Measured => Reason is null && Kinds.Count == 2 && Kinds.All(k => k.Count is >= 0)
        && Kinds.Any(k => k.Kind == EvidenceKind.Metric) && Kinds.Any(k => k.Kind == EvidenceKind.Trace);
    [JsonPropertyName("known_subtotal")] public long KnownSubtotal => Kinds.Sum(k => k.Count ?? 0);
    [JsonPropertyName("total")] public long? Total => Measured ? KnownSubtotal : null;
    public static ExcludedInputRecords Unmeasured { get; } = new([], "NotMeasured");

    public static async Task<ExcludedInputRecords> MeasureAsync(IScopedQuery query, RcaWindow window, AccessScope scope,
        CancellationToken token, CancellationToken callerToken = default)
    {
        var kinds = new List<ExcludedKindCount>();
        foreach (var signal in new[] { TelemetrySignal.Metrics, TelemetrySignal.Traces })
        {
            var kind = signal == TelemetrySignal.Metrics ? EvidenceKind.Metric : EvidenceKind.Trace;
            try
            {
                var count = await query.CountExcludedTelemetryInputsAsync(new(signal,
                    TelemetryEvidenceReader.Nano(window.From), TelemetryEvidenceReader.Nano(window.To),
                    TelemetryEvidenceReader.Nano(window.BaselineFrom), TelemetryEvidenceReader.Nano(window.BaselineTo), window.SourceIds), scope, token);
                var value = count.Status == TelemetryResultStatus.Failed || count.Count is null or < 0 ? null : count.Count;
                kinds.Add(new(kind, value, value is null ? count.Error ?? "CountUnavailable" : null));
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { kinds.Add(new(kind, null, ex is OperationCanceledException or TimeoutException ? "Timeout" : "CountUnavailable")); }
        }
        return new(kinds);
    }
}
