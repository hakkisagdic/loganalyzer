using Bizigo.Capacity;

namespace Bizigo.UnitTests;

public sealed class CapacitySafetyTests
{
    private static CapacityAttempt Attempt(CapacitySafetyOptions? safety = null) =>
        new("run", 10, CapacityDiscoveryTests.Options with { Safety = safety ?? new() });
    private static CapacitySafetySample Sample(int n, long wire = 100, double cpu = 1, long queue = 0) =>
        new("run", "fixture", n, DateTimeOffset.UnixEpoch.AddSeconds(n), wire, cpu, queue);
    private static void Add(CapacitySafety safety, CapacitySafetySample sample) => safety.Add(sample, sample.CapturedAt);

    [Theory]
    [InlineData("wire")]
    [InlineData("cpu")]
    [InlineData("queue")]
    public void Consecutive_threshold_latches_while_old_wire_drops_and_spikes_do_not(string metric)
    {
        var safety = new CapacitySafety(Attempt());
        Add(safety, Sample(0));
        Assert.Null(safety.Stop);
        CapacitySafetySample High(int n) => Sample(n, metric == "wire" ? 100 + n : 100,
            metric == "cpu" ? 99 : 1, metric == "queue" ? 9_000_000 : 0);
        Add(safety, High(1));
        Assert.Null(safety.Stop);
        Add(safety, Sample(2, metric == "wire" ? 101 : 100));
        Add(safety, High(3));
        Add(safety, High(4));
        Assert.Null(safety.Stop);
        Add(safety, High(5));
        Assert.Equal(CapacityVerdict.Aborted, safety.Stop!.Verdict);
        Add(safety, Sample(6, 106));
        Assert.Equal(CapacityVerdict.Aborted, safety.Stop.Verdict);
    }

    [Fact]
    public void Independent_metric_streaks_and_equal_thresholds_do_not_trip()
    {
        var safety = new CapacitySafety(Attempt(new() { ConsecutiveSamples = 2 }));
        Add(safety, Sample(0));
        Add(safety, Sample(1, cpu: 99));
        Add(safety, Sample(2, queue: 9_000_000));
        Add(safety, Sample(3, cpu: 90, queue: 1_048_576));
        Assert.Null(safety.Stop);
    }

    [Fact]
    public void Bad_stale_foreign_missing_and_out_of_order_samples_never_pass()
    {
        var valid = Sample(1);
        CapacitySafetySample[] invalid = [valid with { RunId = "old" }, valid with { Target = "other" },
            valid with { WireDrops = -1 }, valid with { WireDrops = 99 }, valid with { CpuPercent = double.NaN },
            valid with { CpuPercent = 101 }, valid with { RecvQueue = -1 }, valid with { CpuPercent = null },
            valid with { MonotonicSeconds = 0 }, valid with { MonotonicSeconds = 9 },
            valid with { CapturedAt = DateTimeOffset.UnixEpoch }, valid with { UnavailableReason = "denied" }];
        foreach (var sample in invalid)
        {
            var safety = new CapacitySafety(Attempt());
            Add(safety, Sample(0));
            safety.Add(sample, valid.CapturedAt);
            Assert.Equal(CapacityVerdict.Inconclusive, safety.Stop!.Verdict);
        }
        var stale = new CapacitySafety(Attempt());
        stale.Add(valid, valid.CapturedAt.AddSeconds(10));
        Assert.Equal(CapacityVerdict.Inconclusive, stale.Stop!.Verdict);
    }

    [Theory]
    [InlineData(MissingProbePolicy.Inconclusive, CapacityVerdict.Inconclusive)]
    [InlineData(MissingProbePolicy.Abort, CapacityVerdict.Aborted)]
    public void Silence_policy_is_latched(MissingProbePolicy policy, CapacityVerdict verdict)
    {
        var safety = new CapacitySafety(Attempt(new() { MissingProbePolicy = policy }));
        Add(safety, Sample(0));
        safety.CheckFreshness(DateTimeOffset.UnixEpoch.AddSeconds(6));
        Add(safety, Sample(7));
        Assert.Equal(verdict, safety.Stop!.Verdict);
        var empty = new CapacitySafety(Attempt());
        empty.CheckFreshness(DateTimeOffset.UnixEpoch);
        Assert.Equal(CapacityVerdict.Inconclusive, empty.Stop!.Verdict);
    }
}
