namespace Bizigo.Capacity;

public sealed record CapacitySafetySample(
    string RunId, string Target, double MonotonicSeconds, DateTimeOffset CapturedAt,
    long? WireDrops, double? CpuPercent, long? RecvQueue, string? UnavailableReason = null);

/// <summary>One latch per attempt, with independent consecutive counters per metric.</summary>
public sealed class CapacitySafety(CapacityAttempt attempt)
{
    private readonly int[] streaks = new int[3];
    private CapacitySafetySample? previous;
    private DateTimeOffset? received;
    public CapacityDecision? Stop { get; private set; }

    public void Missing(string reason) => Stop ??= new(
        attempt.Options.Safety.MissingProbePolicy == MissingProbePolicy.Abort
            ? CapacityVerdict.Aborted : CapacityVerdict.Inconclusive,
        CapacityGap.Unknown, reason);

    public void CheckFreshness(DateTimeOffset now)
    {
        if (received is null || now - received > TimeSpan.FromSeconds(attempt.Options.Safety.MaxSampleGapSeconds))
            Missing("Safety probe silent beyond max sample gap.");
    }

    public void Add(CapacitySafetySample sample, DateTimeOffset now)
    {
        if (Stop is not null) return;
        var policy = attempt.Options.Safety;
        if (sample.RunId != attempt.RunId || sample.Target != attempt.Options.Target
            || !double.IsFinite(sample.MonotonicSeconds) || sample.MonotonicSeconds < 0
            || Math.Abs((now - sample.CapturedAt).TotalSeconds) > policy.MaxSampleGapSeconds
            || sample.WireDrops is null or < 0 || sample.RecvQueue is null or < 0
            || sample.CpuPercent is null || !double.IsFinite(sample.CpuPercent.Value)
            || sample.CpuPercent is < 0 or > 100 || sample.UnavailableReason is not null)
        {
            Missing("Invalid, stale, out-of-scope or unavailable safety sample: " + sample.UnavailableReason);
            return;
        }

        if (previous is not null)
        {
            if (sample.MonotonicSeconds <= previous.MonotonicSeconds
                || sample.MonotonicSeconds - previous.MonotonicSeconds > policy.MaxSampleGapSeconds
                || sample.CapturedAt <= previous.CapturedAt
                || (now - received!.Value).TotalSeconds > policy.MaxSampleGapSeconds
                || sample.WireDrops < previous.WireDrops)
            {
                Missing("Safety sample order/gap invalid or wire counter reset.");
                return;
            }

            bool[] exceeded = [sample.WireDrops - previous.WireDrops > policy.MaxNewWireDrops,
                sample.CpuPercent > policy.MaxCpuPercent, sample.RecvQueue > policy.MaxRecvQueue];
            string[] names = ["wire-drop", "CPU", "Recv-Q"];
            for (var i = 0; i < streaks.Length; i++)
            {
                streaks[i] = exceeded[i] ? streaks[i] + 1 : 0;
                if (streaks[i] >= policy.ConsecutiveSamples)
                    Stop ??= new(CapacityVerdict.Aborted, CapacityGap.Unknown,
                        $"Safety breaker: {names[i]} exceeded threshold for {streaks[i]} consecutive samples.");
            }
        }
        previous = sample;
        received = now;
    }
}
