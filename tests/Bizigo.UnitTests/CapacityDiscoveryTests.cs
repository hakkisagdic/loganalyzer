using Bizigo.Capacity;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

public sealed class CapacityDiscoveryTests
{
    internal static CapacityOptions Options => new()
    {
        Target = "fixture", MinimumEps = 100, MaximumEps = 1000, ResolutionEps = 25,
        DurationSeconds = 1, ObservationSeconds = .1, GeneratorTimeoutSeconds = 5, GeneratorLocation = "same",
    };

    private sealed class MemoryStore : ICapacityRunStore
    {
        public List<CapacityAttemptRecord> Records { get; } = [];
        public CapacityDiscoveryResult? Result { get; private set; }
        public bool FailWrite { get; init; }
        public Task<string> SaveAttemptAsync(CapacityAttemptRecord record, CancellationToken cancellationToken)
        {
            if (FailWrite) throw new IOException("injected write failure");
            Records.Add(record);
            return Task.FromResult(record.Attempt.RunId + ".json");
        }
        public Task SaveDiscoveryAsync(CapacityDiscoveryResult result, CancellationToken cancellationToken)
        { Result = result; return Task.CompletedTask; }
    }

    private sealed class Runner(Func<CapacityAttempt, int, CapacityVerdict> verdict) : ICapacityAttemptRunner
    {
        public List<CapacityAttempt> Calls { get; } = [];
        public Task<CapacityAttemptRecord> RunAsync(CapacityAttempt attempt, CancellationToken cancellationToken)
        {
            Calls.Add(attempt);
            var now = DateTimeOffset.UnixEpoch;
            return Task.FromResult(new CapacityAttemptRecord(attempt, now, now, null, null, [],
                new(verdict(attempt, Calls.Count), CapacityGap.None, "fixture"), null));
        }
    }

    [Fact]
    public async Task Search_is_deterministic_and_requires_three_consecutive_passes()
    {
        async Task<(CapacityDiscoveryResult, Runner)> Run()
        {
            var runner = new Runner((a, _) => a.EventsPerSecond <= 650 ? CapacityVerdict.Pass : CapacityVerdict.Fail);
            var result = await new CapacityDiscovery(runner, new MemoryStore(), new FakeTimeProvider()).DiscoverAsync(Options, TestContext.Current.CancellationToken);
            return (result, runner);
        }
        var (first, one) = await Run();
        var (second, two) = await Run();
        Assert.Equal(CapacityVerdict.Pass, first.Verdict);
        Assert.Equal(650, first.CapacityEps);
        Assert.Equal(675, first.FailedUpperEps);
        Assert.Equal(first.CapacityEps, second.CapacityEps);
        Assert.Equal(one.Calls.Select(a => a.EventsPerSecond), two.Calls.Select(a => a.EventsPerSecond));
        Assert.Equal(new[] { 100, 100, 100, 200, 200, 200, 400, 400, 400, 800, 600, 600, 600, 700, 650, 650, 650, 675 },
            one.Calls.Select(a => a.EventsPerSecond));
        Assert.Equal(3, first.ConfirmingRunIds.Count);
        Assert.All(one.Calls, a => Assert.Equal(Options, a.Options));
        Assert.Equal(one.Calls.Count + two.Calls.Count,
            one.Calls.Concat(two.Calls).Select(a => a.RunId).Distinct().Count());
        Assert.NotEqual(first.DiscoveryId, second.DiscoveryId);
    }

    [Fact]
    public async Task Two_passes_then_failure_does_not_confirm_the_rate()
    {
        var runner = new Runner((_, i) => i == 3 ? CapacityVerdict.Fail : CapacityVerdict.Pass);
        var result = await new CapacityDiscovery(runner, new MemoryStore()).DiscoverAsync(Options, TestContext.Current.CancellationToken);
        Assert.Equal(CapacityVerdict.Fail, result.Verdict);
        Assert.Null(result.CapacityEps);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Theory]
    [InlineData(CapacityVerdict.Inconclusive)]
    [InlineData(CapacityVerdict.Aborted)]
    public async Task Limited_or_aborted_after_passes_never_publishes_capacity(CapacityVerdict verdict)
    {
        var runner = new Runner((_, i) => i <= 3 ? CapacityVerdict.Pass : verdict);
        var result = await new CapacityDiscovery(runner, new MemoryStore()).DiscoverAsync(Options, TestContext.Current.CancellationToken);
        Assert.Equal(verdict, result.Verdict);
        Assert.Null(result.CapacityEps);
        Assert.Empty(result.ConfirmingRunIds);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Theory]
    [InlineData(2, CapacityVerdict.Inconclusive)]
    [InlineData(3, CapacityVerdict.Pass)]
    public async Task Exact_budget_and_single_point_boundary(int budget, CapacityVerdict verdict)
    {
        var runner = new Runner((_, _) => CapacityVerdict.Pass);
        var result = await new CapacityDiscovery(runner, new MemoryStore()).DiscoverAsync(
            Options with { MaximumEps = 100, AttemptBudget = budget }, TestContext.Current.CancellationToken);
        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(budget, runner.Calls.Count);
        Assert.False(result.UpperBoundaryFound);
    }

    [Fact]
    public async Task Growth_near_integer_boundary_does_not_overflow()
    {
        var runner = new Runner((_, _) => CapacityVerdict.Pass);
        var result = await new CapacityDiscovery(runner, new MemoryStore()).DiscoverAsync(
            Options with { MinimumEps = 1_500_000_000, MaximumEps = 2_000_000_000 }, TestContext.Current.CancellationToken);
        Assert.Equal(2_000_000_000, result.CapacityEps);
        Assert.Equal(6, runner.Calls.Count);
    }

    [Fact]
    public async Task Sub_resolution_interval_terminates_at_verified_lower_bound()
    {
        var runner = new Runner((a, _) => a.EventsPerSecond == 100 ? CapacityVerdict.Pass : CapacityVerdict.Fail);
        var result = await new CapacityDiscovery(runner, new MemoryStore()).DiscoverAsync(
            Options with { MaximumEps = 110 }, TestContext.Current.CancellationToken);
        Assert.Equal(100, result.CapacityEps);
        Assert.Equal(110, result.FailedUpperEps);
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_and_persistence_failure_are_not_capacity()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var runner = new Runner((_, _) => CapacityVerdict.Pass);
        var store = new MemoryStore();
        var result = await new CapacityDiscovery(runner, store).DiscoverAsync(Options, cancelled.Token);
        Assert.Equal(CapacityVerdict.Aborted, result.Verdict);
        Assert.Null(result.CapacityEps);
        Assert.Empty(runner.Calls);
        Assert.Single(store.Records);
        Assert.NotNull(store.Records[0].MissingEvidenceReason);

        result = await new CapacityDiscovery(runner, new MemoryStore { FailWrite = true }).DiscoverAsync(Options, TestContext.Current.CancellationToken);
        Assert.Equal(CapacityVerdict.Inconclusive, result.Verdict);
        Assert.Null(result.CapacityEps);
    }

    [Fact]
    public void Invalid_ranges_durations_and_overflow_are_rejected()
    {
        CapacityOptions[] invalid = [Options with { MinimumEps = 0 }, Options with { MaximumEps = 1 },
            Options with { ResolutionEps = 0 }, Options with { AttemptBudget = 0 },
            Options with { DurationSeconds = double.NaN }, Options with { DurationSeconds = double.PositiveInfinity },
            Options with { DurationSeconds = -1 }, Options with { MaxLatencyMilliseconds = 0 },
            Options with { GeneratorLocation = "nearby" }, Options with { GeneratorTimeoutSeconds = .5 },
            Options with { MaximumEps = int.MaxValue, DurationSeconds = 2 }];
        Assert.All(invalid, o => Assert.Throws<ArgumentException>(o.Validate));
    }
}
