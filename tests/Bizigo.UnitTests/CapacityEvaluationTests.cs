using System.Text.Json;
using Bizigo.Capacity;
using Bizigo.Simulators;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

public sealed class CapacityEvaluationTests
{
    [Fact]
    public void Sub_tick_wait_cannot_emit_without_granting_a_token()
    {
        var time = new FakeTimeProvider();
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(3_000_000, TimeSpan.FromSeconds(1)), time);
        Assert.Equal(TimeSpan.Zero, pacer.TryAcquire());
        time.Advance(TimeSpan.FromTicks(3));
        Assert.True(pacer.TryAcquire() > TimeSpan.Zero);
        Assert.Equal(1, pacer.Granted);
    }

    internal static (CapacityAttempt Attempt, CapacityRunManifest Manifest, CapacityObservation Observation) Fixture(string run = "run-1")
    {
        var options = CapacityDiscoveryTests.Options with { MinimumEps = 10, MaximumEps = 10 };
        var attempt = new CapacityAttempt(run, 10, options);
        var time = new FakeTimeProvider();
        var pacer = new TokenBucketPacer(new PaceProfile.Fixed(10, TimeSpan.FromSeconds(1)), time);
        time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 10; i++) pacer.TryAcquire();
        var digests = Enumerable.Range(0, 10).Select(i => CapacityRunManifest.Digest(CapacityEmitter.Tag("line", run, i, DateTimeOffset.UnixEpoch))).ToArray();
        var manifest = new CapacityRunManifest(run, "fixed", GeneratorAttainment.From(pacer), digests, true);
        var identities = digests.Select((d, i) => new CapacityIdentity(run, i, d)).ToArray();
        var ledger = new ArrivalLedger(run, 10, Read(LedgerLayer.Wire, 0), Read(LedgerLayer.Collector, 10),
            Read(LedgerLayer.Collector, 0), Read(LedgerLayer.Product, 10), Read(LedgerLayer.Product, 10));
        return (attempt, manifest, new(run, options.Target, ledger, identities, identities, 1));
    }
    private static LedgerReading Read(LedgerLayer layer, long count) => LedgerReading.Measured(layer, "fixture", count);

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3, 4 }, CapacityGap.None)]
    [InlineData(new[] { 1, 2, 3, 4 }, CapacityGap.Head)]
    [InlineData(new[] { 0, 1, 2, 3 }, CapacityGap.Tail)]
    [InlineData(new[] { 0, 1, 3, 4 }, CapacityGap.Contiguous)]
    [InlineData(new[] { 0, 2, 4 }, CapacityGap.Scattered)]
    [InlineData(new int[] { }, CapacityGap.Unknown)]
    [InlineData(new[] { -1, 0, 1, 2, 3, 4 }, CapacityGap.Unknown)]
    public void Gap_boundaries(int[] observed, CapacityGap expected)
    { Assert.Equal(expected, CapacityEvaluation.Classify(5, observed.Reverse())); }

    [Fact]
    public void Missing_or_empty_sequence_evidence_is_unknown()
    {
        Assert.Equal(CapacityGap.Unknown, CapacityEvaluation.Classify(5, null));
        Assert.Equal(CapacityGap.Unknown, CapacityEvaluation.Classify(0, []));
    }

    [Fact]
    public void Pass_requires_all_evidence_and_slo_and_preserves_round_trip()
    {
        var (a, m, o) = Fixture();
        Assert.Equal(CapacityVerdict.Pass, CapacityEvaluation.Evaluate(a, m, o, null).Verdict);
        Assert.Equal(CapacityVerdict.Pass, CapacityEvaluation.Evaluate(a, m,
            o with { LatencyMilliseconds = a.Options.MaxLatencyMilliseconds }, null).Verdict);
        Assert.Equal(CapacityVerdict.Fail, CapacityEvaluation.Evaluate(a, m,
            o with { LatencyMilliseconds = a.Options.MaxLatencyMilliseconds + 1 }, null).Verdict);
        var record = new CapacityAttemptRecord(a, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, m, o, [],
            CapacityEvaluation.Evaluate(a, m, o, null), null);
        var roundtrip = JsonSerializer.Deserialize<CapacityAttemptRecord>(JsonSerializer.Serialize(record, CapacityJson.Options), CapacityJson.Options)!;
        Assert.Equal(m.Attainment, roundtrip.Manifest!.Attainment);
        Assert.Equal(record.Decision, roundtrip.Decision);
        Assert.Equal(o.Searchable, roundtrip.Observation!.Searchable);
        Assert.Null(roundtrip.CapacityEps);
    }

    [Fact]
    public void Missing_unknown_mismatched_and_limited_evidence_is_inconclusive()
    {
        var (a, m, o) = Fixture();
        CapacityDecision[] results = [
            CapacityEvaluation.Evaluate(a, null, o, null), CapacityEvaluation.Evaluate(a, m, null, null),
            CapacityEvaluation.Evaluate(a, m with { RunId = "other" }, o, null),
            CapacityEvaluation.Evaluate(a, m with { GeneratorOnSameHost = null }, o, null),
            CapacityEvaluation.Evaluate(a with { Options = a.Options with { GeneratorLocation = "unknown" } }, m, o, null),
            CapacityEvaluation.Evaluate(a, m with { Digests = [] }, o, null),
            CapacityEvaluation.Evaluate(a, m, o with { RunId = "other" }, null),
            CapacityEvaluation.Evaluate(a, m, o with { LatencyMilliseconds = null }, null),
            CapacityEvaluation.Evaluate(a, m, o with { LatencyMilliseconds = double.NaN }, null),
            CapacityEvaluation.Evaluate(a, m, o with { Searchable = null }, null),
            CapacityEvaluation.Evaluate(a, m, o with { Ledger = o.Ledger with { Expected = 11 } }, null),
            CapacityEvaluation.Evaluate(a, m, o with { Ledger = o.Ledger with { ProductArchived = LedgerReading.Limited(LedgerLayer.Product, "raw", "unavailable") } }, null),
        ];
        Assert.All(results, r => Assert.Equal(CapacityVerdict.Inconclusive, r.Verdict));
        foreach (var verdict in new[] { GeneratorVerdict.GeneratorLimited, GeneratorVerdict.NoTarget, GeneratorVerdict.Unmeasured, GeneratorVerdict.Unspecified })
        {
            var attainment = new GeneratorAttainment(verdict, 10, 10, 10, TimeSpan.FromSeconds(1), "fixture");
            Assert.Equal(CapacityVerdict.Inconclusive, CapacityEvaluation.Evaluate(a, m with { Attainment = attainment }, o, null).Verdict);
        }
        foreach (var achieved in new[] { 0.0, double.NaN, 100.0 })
        {
            var forged = new GeneratorAttainment(GeneratorVerdict.Attained, 10, achieved, 10, TimeSpan.FromSeconds(1), "inconsistent");
            Assert.Equal(CapacityVerdict.Inconclusive, CapacityEvaluation.Evaluate(a, m with { Attainment = forged }, o, null).Verdict);
        }
        Assert.Equal(CapacityVerdict.Aborted, CapacityEvaluation.Evaluate(a, null, null,
            new(CapacityVerdict.Aborted, CapacityGap.Unknown, "breaker")).Verdict);
    }

    [Fact]
    public void Late_events_with_same_ordinals_cannot_fill_next_attempt_gaps()
    {
        var first = Fixture(CapacityDiscovery.AttemptId("discovery", 1));
        var second = Fixture(CapacityDiscovery.AttemptId("discovery", 2));
        // A window probe returns old records plus the current run's first seven.
        var mixed = first.Observation.Searchable!.Concat(second.Observation.Searchable!.Take(7)).ToArray();
        var observation = second.Observation with
        {
            Searchable = mixed,
            Ledger = second.Observation.Ledger with { ProductSearchable = Read(LedgerLayer.Product, 7) },
        };
        var result = CapacityEvaluation.Evaluate(second.Attempt, second.Manifest, observation, null);
        Assert.Equal(CapacityGap.Tail, result.Gap);
        Assert.Equal(CapacityVerdict.Fail, result.Verdict);
        var withoutLate = observation with { Searchable = second.Observation.Searchable!.Take(7).ToArray() };
        Assert.Equal(CapacityEvaluation.Evaluate(second.Attempt, second.Manifest, withoutLate, null), result);
    }

    [Fact]
    public void Duplicate_and_invalid_sequence_cannot_hide_loss()
    {
        var (a, m, o) = Fixture();
        var duplicate = o.Searchable!.Take(9).Append(o.Searchable![0]).ToArray();
        Assert.NotEqual(CapacityVerdict.Pass, CapacityEvaluation.Evaluate(a, m, o with { Searchable = duplicate }, null).Verdict);
        var invalid = o.Searchable!.Select(i => i.Sequence == 0 ? i with { Sequence = -1 } : i).ToArray();
        Assert.Equal(CapacityVerdict.Inconclusive, CapacityEvaluation.Evaluate(a, m, o with { Searchable = invalid }, null).Verdict);
    }
}
