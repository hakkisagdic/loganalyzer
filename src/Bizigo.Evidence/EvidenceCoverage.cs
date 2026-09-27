using System.Text.Json.Serialization;
using Bizigo.Contracts;

namespace Bizigo.Evidence;

/// <summary>Expected identities survive a missing DI registration.</summary>
public sealed record EvidenceProviderRequirement(string Id, EvidenceKind Kind);

public sealed record EvidenceProviderRequirements(IReadOnlyList<EvidenceProviderRequirement> Providers)
{
    public static EvidenceProviderRequirements Telemetry { get; } = new([
        new("metrics.baseline", EvidenceKind.Metric), new("metrics.threshold", EvidenceKind.Metric),
        new("traces.error-propagation", EvidenceKind.Trace), new("traces.service-dependency", EvidenceKind.Trace),
    ]);
}

public sealed record EvidenceKindCoverage(
    [property: JsonPropertyName("kind")] EvidenceKind Kind,
    [property: JsonPropertyName("status")] EvidenceStatus Status,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("not_consulted")] IReadOnlyList<string> NotConsulted)
{
    public static IReadOnlyList<EvidenceKindCoverage> From(IReadOnlyList<EvidenceSlice> slices) =>
        Enum.GetValues<EvidenceKind>().Select(kind => Aggregate(kind, slices.Where(s => s.Kind == kind).ToArray())).ToArray();

    private static EvidenceKindCoverage Aggregate(EvidenceKind kind, EvidenceSlice[] slices)
    {
        var partial = slices.Length == 0 || slices.Any(s => s.Truncated || s.Status is
            EvidenceStatus.Failed or EvidenceStatus.Unavailable or EvidenceStatus.NotRegistered or EvidenceStatus.NeverFed);
        EvidenceStatus status;
        if (slices.Any(s => s.Status == EvidenceStatus.Gathered)) status = EvidenceStatus.Gathered;
        else if (slices.Length == 0) status = EvidenceStatus.NotRegistered;
        else if (slices.All(s => s.Telemetry?.Feed == TelemetryResultStatus.NeverFed || s.Status == EvidenceStatus.NeverFed))
            status = EvidenceStatus.NeverFed;
        else if (slices.Any(s => s.Status == EvidenceStatus.Failed)) status = EvidenceStatus.Failed;
        else if (slices.Any(s => s.Status == EvidenceStatus.NotRegistered)) status = EvidenceStatus.NotRegistered;
        else if (slices.Any(s => s.Status == EvidenceStatus.Unavailable)) status = EvidenceStatus.Unavailable;
        else if (slices.Any(s => s.Status == EvidenceStatus.NeverFed)) status = EvidenceStatus.NeverFed;
        else if (slices.All(s => s.Status == EvidenceStatus.OutOfScope)) status = EvidenceStatus.OutOfScope;
        else status = EvidenceStatus.Empty;
        return new(kind, status, partial,
            slices.Where(s => !s.IsEvidence).Select(s => s.ProviderId).Order(StringComparer.Ordinal).ToArray());
    }
}
