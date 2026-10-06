using Bizigo.Api.Anomaly;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Xunit;

namespace Bizigo.UnitTests;

public sealed class AnomalyEvaluatorTests
{
    private static AnomalyPolicyEntity CreatePolicy(
        string signal = AnomalySignals.MetricSeriesSum,
        int eventWindow = 60,
        int baselineWindow = 300,
        double sensitivity = 2.0,
        int minSamples = 3,
        double? zeroBaselineMinAbsolute = 5.0) => new()
    {
        Id = "pol-test-1",
        OwnerGroup = "core",
        Name = "Test Policy",
        Signal = signal,
        Target = "http_requests_total",
        EventWindowSeconds = eventWindow,
        BaselineWindowSeconds = baselineWindow,
        Sensitivity = sensitivity,
        MinSamples = minSamples,
        ZeroBaselineMinAbsolute = zeroBaselineMinAbsolute,
        State = AnomalyPolicyStates.Enabled,
        Version = 1,
        CadenceSeconds = 60
    };

    [Fact]
    public async Task Evaluator_ReturnsNoSignal_WhenNeverFed()
    {
        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) => Task.FromResult(new TelemetryPage(
                TelemetryResultStatus.NeverFed,
                Records: []))
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy();
        var now = DateTimeOffset.UtcNow;

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.NoSignal, result.Status);
        Assert.Equal("never_fed", result.Reason);
    }

    [Fact]
    public async Task Evaluator_ReturnsNoSignal_WhenInsufficientBaseline()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineEnd = now.AddSeconds(-60);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) =>
            {
                if (q.ToNano <= baseToNano)
                {
                    var rec = MetricEvidenceProviderTests.Point("100", (ulong)q.FromNano);
                    return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [rec]));
                }

                var evRec = MetricEvidenceProviderTests.Point("500", (ulong)q.FromNano);
                return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [evRec]));
            }
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy(minSamples: 3);

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.NoSignal, result.Status);
        Assert.Equal("insufficient_baseline", result.Reason);
    }

    [Fact]
    public async Task Evaluator_ZeroBaseline_TriggersWhenAboveMinAbsolute()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineEnd = now.AddSeconds(-60);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) =>
            {
                if (q.ToNano <= baseToNano)
                {
                    var records = Enumerable.Range(0, 5).Select(i =>
                        MetricEvidenceProviderTests.Point("0", (ulong)(q.FromNano + (i * 10_000_000_000m)))).ToList();
                    return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, records));
                }

                // Event window: 10.0 (greater than zeroBaselineMinAbsolute = 5.0)
                var evRec = MetricEvidenceProviderTests.Point("10", (ulong)q.FromNano);
                return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [evRec]));
            }
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy(zeroBaselineMinAbsolute: 5.0);

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.Triggered, result.Status);
        Assert.Equal(10.0, result.ObservedValue);
        Assert.Equal(0.0, result.BaselineValue);
    }

    [Fact]
    public async Task Evaluator_ZeroBaseline_ReturnsNoSignalWhenBelowMinAbsolute()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineEnd = now.AddSeconds(-60);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) =>
            {
                if (q.ToNano <= baseToNano)
                {
                    var records = Enumerable.Range(0, 5).Select(i =>
                        MetricEvidenceProviderTests.Point("0", (ulong)(q.FromNano + (i * 10_000_000_000m)))).ToList();
                    return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, records));
                }

                // Event window: 2.0 (less than zeroBaselineMinAbsolute = 5.0)
                var evRec = MetricEvidenceProviderTests.Point("2", (ulong)q.FromNano);
                return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [evRec]));
            }
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy(zeroBaselineMinAbsolute: 5.0);

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.NoSignal, result.Status);
        Assert.Equal("zero_baseline", result.Reason);
    }

    [Fact]
    public async Task Evaluator_Triggers_WhenDeviationExceedsSensitivity()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineEnd = now.AddSeconds(-60);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) =>
            {
                if (q.ToNano <= baseToNano)
                {
                    var vals = new[] { "95", "105", "100", "98", "102" };
                    var records = vals.Select((v, i) =>
                        MetricEvidenceProviderTests.Point(v, (ulong)(q.FromNano + (i * 10_000_000_000m)))).ToList();
                    return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, records));
                }

                // Event window: spike to 200.0 (far exceeds sensitivity 2.0)
                var evRec = MetricEvidenceProviderTests.Point("200", (ulong)q.FromNano);
                return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [evRec]));
            }
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy(sensitivity: 2.0);

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.Triggered, result.Status);
        Assert.NotNull(result.Deviation);
        Assert.True(result.Deviation >= 2.0);
    }

    [Fact]
    public async Task Evaluator_ReturnsNoSignal_WhenWithinThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var baselineEnd = now.AddSeconds(-60);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var fakeQuery = new RecordingScopedQuery
        {
            TelemetrySearch = (q, s, ct) =>
            {
                if (q.ToNano <= baseToNano)
                {
                    var vals = new[] { "100", "100", "100", "100", "100" };
                    var records = vals.Select((v, i) =>
                        MetricEvidenceProviderTests.Point(v, (ulong)(q.FromNano + (i * 10_000_000_000m)))).ToList();
                    return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, records));
                }

                // Event window: 100.0 (exact same as baseline mean)
                var evRec = MetricEvidenceProviderTests.Point("100", (ulong)q.FromNano);
                return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [evRec]));
            }
        };

        var evaluator = new AnomalyEvaluator(fakeQuery);
        var policy = CreatePolicy(sensitivity: 2.0);

        var result = await evaluator.EvaluateAsync(policy, now.AddSeconds(-60), now, TestContext.Current.CancellationToken);

        Assert.Equal(AnomalyRunStatuses.NoSignal, result.Status);
        Assert.Equal("within_threshold", result.Reason);
    }
}
