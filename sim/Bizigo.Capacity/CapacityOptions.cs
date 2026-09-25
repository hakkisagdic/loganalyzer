namespace Bizigo.Capacity;

public enum CapacityVerdict { Pass, Fail, Inconclusive, Aborted }
public enum CapacityGap { None, Head, Tail, Contiguous, Scattered, Unknown }
public enum MissingProbePolicy { Inconclusive, Abort }

public sealed record CapacitySafetyOptions
{
    public long MaxNewWireDrops { get; init; }
    public double MaxCpuPercent { get; init; } = 90;
    public long MaxRecvQueue { get; init; } = 1_048_576;
    public int ConsecutiveSamples { get; init; } = 3;
    public double SampleIntervalSeconds { get; init; } = 1;
    public double MaxSampleGapSeconds { get; init; } = 5;
    public double ProbeTimeoutSeconds { get; init; } = 3;
    public MissingProbePolicy MissingProbePolicy { get; init; }

    public void Validate()
    {
        if (MaxNewWireDrops < 0 || MaxRecvQueue < 0 || ConsecutiveSamples < 1
            || !double.IsFinite(MaxCpuPercent) || MaxCpuPercent is <= 0 or > 100
            || !Enum.IsDefined(MissingProbePolicy))
            throw new ArgumentException("Invalid safety thresholds or missing-probe policy.");
        CapacityOptions.Positive(SampleIntervalSeconds, nameof(SampleIntervalSeconds));
        CapacityOptions.Positive(MaxSampleGapSeconds, nameof(MaxSampleGapSeconds));
        CapacityOptions.Positive(ProbeTimeoutSeconds, nameof(ProbeTimeoutSeconds));
        if (SampleIntervalSeconds > MaxSampleGapSeconds || ProbeTimeoutSeconds > MaxSampleGapSeconds)
            throw new ArgumentException("Sampling interval and probe timeout must not exceed max sample gap.");
    }
}

public sealed record CapacityOptions
{
    public string Target { get; init; } = "";
    public int MinimumEps { get; init; } = 100;
    public int MaximumEps { get; init; } = 1000;
    public int ResolutionEps { get; init; } = 25;
    public int AttemptBudget { get; init; } = 100;
    public double DurationSeconds { get; init; } = 10;
    public double MaxLatencyMilliseconds { get; init; } = 1000;
    public double ObservationSeconds { get; init; } = 5;
    public double GeneratorTimeoutSeconds { get; init; } = 30;
    public string GeneratorLocation { get; init; } = "unknown";
    public CapacitySafetyOptions Safety { get; init; } = new();

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Target);
        if (MinimumEps <= 0 || MaximumEps < MinimumEps || ResolutionEps <= 0 || AttemptBudget <= 0)
            throw new ArgumentException("Invalid EPS range, resolution or attempt budget.");
        Positive(DurationSeconds, nameof(DurationSeconds));
        Positive(MaxLatencyMilliseconds, nameof(MaxLatencyMilliseconds));
        Positive(ObservationSeconds, nameof(ObservationSeconds));
        Positive(GeneratorTimeoutSeconds, nameof(GeneratorTimeoutSeconds));
        if (GeneratorTimeoutSeconds <= DurationSeconds || MaximumEps * DurationSeconds > int.MaxValue)
            throw new ArgumentException("Generator timeout must exceed duration; EPS × duration must fit Int32.");
        if (GeneratorLocation is not ("same" or "separate" or "unknown"))
            throw new ArgumentException("Generator location must be same, separate or unknown.");
        ArgumentNullException.ThrowIfNull(Safety);
        Safety.Validate();
    }

    internal static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0 || value > TimeSpan.MaxValue.TotalSeconds / 1000)
            throw new ArgumentException($"{name} must be a finite positive value in the supported time range.");
    }
}

public sealed record CapacityAttempt(string RunId, int EventsPerSecond, CapacityOptions Options);
public sealed record CapacityIdentity(string RunId, int Sequence, string Digest);
public sealed record CapacityObservation(
    string RunId, string Target, ArrivalLedger Ledger,
    IReadOnlyList<CapacityIdentity>? Archived,
    IReadOnlyList<CapacityIdentity>? Searchable,
    double? LatencyMilliseconds);

public sealed record CapacityDecision(CapacityVerdict Verdict, CapacityGap Gap, string Reason);

public sealed record CapacityAttemptRecord(
    CapacityAttempt Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    CapacityRunManifest? Manifest,
    CapacityObservation? Observation,
    IReadOnlyList<CapacitySafetySample> SafetySamples,
    CapacityDecision Decision,
    string? MissingEvidenceReason,
    int? GeneratorPid = null)
{
    public int SchemaVersion => 1;
    public string? LedgerReport => Observation?.Ledger.Report();
    // An attempt, even a successful one, is never a capacity claim.
    public int? CapacityEps => null;
}

public interface ICapacityAttemptRunner
{
    Task<CapacityAttemptRecord> RunAsync(CapacityAttempt attempt, CancellationToken cancellationToken);
}
