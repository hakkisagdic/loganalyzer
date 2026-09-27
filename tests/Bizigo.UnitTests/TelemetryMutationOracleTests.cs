using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class TelemetryMutationOracleTests
{
    [Fact] public void Metric_is_not_exempt() => Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Metric));
    [Fact] public void Trace_is_not_exempt() => Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Trace));
    [Fact] public void Log_remains_not_exempt() => Assert.False(EvidenceKinds.IsExempt(EvidenceKind.Log));
    [Fact] public void Production_di_metric_baseline_required() => Registered<MetricBaselineProvider>();
    [Fact] public void Production_di_metric_threshold_required() => Registered<MetricThresholdProvider>();
    [Fact] public void Production_di_trace_error_required() => Registered<TraceErrorPropagationProvider>();
    [Fact] public void Production_di_trace_dependency_required() => Registered<TraceServiceDependencyProvider>();
    private static void Registered<T>() where T : IEvidenceProvider
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddScoped<IScopedQuery, RecordingScopedQuery>(); services.AddBizigoEvidence();
        using var sp = services.BuildServiceProvider(); using var scope = sp.CreateScope();
        Assert.Single(scope.ServiceProvider.GetServices<IEvidenceProvider>().OfType<T>());
    }
    [Fact]
    public void Kind_failed_is_not_empty()
    {
        foreach (var first in new[] { EvidenceStatus.Gathered, EvidenceStatus.Empty })
        {
            var result = Coverage(first, EvidenceStatus.Failed);
            Assert.Equal(first == EvidenceStatus.Gathered ? EvidenceStatus.Gathered : EvidenceStatus.Failed, result.Status);
            Assert.True(result.Partial); Assert.Contains("second", result.NotConsulted);
        }
    }
    [Fact] public void Two_empty_providers_are_empty() { var result = Coverage(EvidenceStatus.Empty, EvidenceStatus.Empty); Assert.Equal(EvidenceStatus.Empty, result.Status); Assert.False(result.Partial); }
    private static EvidenceKindCoverage Coverage(EvidenceStatus first, EvidenceStatus second) =>
        Assert.Single(EvidenceKindCoverage.From([new() { ProviderId = "first", Kind = EvidenceKind.Metric, Status = first },
            new() { ProviderId = "second", Kind = EvidenceKind.Metric, Status = second }]), c => c.Kind == EvidenceKind.Metric);
    [Fact] public void Outside_failed_count_is_null() => Assert.Null(new ExcludedInputRecords([new(EvidenceKind.Metric, null, "Failed"), new(EvidenceKind.Trace, 0, null)]).Total);
    [Fact] public void Outside_measured_zero_is_zero() => Assert.Equal(0, new ExcludedInputRecords([new(EvidenceKind.Metric, 0, null), new(EvidenceKind.Trace, 0, null)]).Total);
    [Fact]
    public void Outside_one_provider_count_three()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var bundle = new EvidenceBundle { Id = Guid.NewGuid(), SchemaVersion = 2, GatheredAt = now, Scope = new(["A"], false), Trust = WindowTrust.Unmeasured,
            Window = new() { From = now, To = now.AddMinutes(1), BaselineFrom = now.AddMinutes(-1), BaselineTo = now },
            Slices = [new() { ProviderId = "one", Kind = EvidenceKind.Metric, Status = EvidenceStatus.Gathered, OutOfScopeCount = 3 }],
            ExcludedInputs = new([new(EvidenceKind.Metric, 3, null), new(EvidenceKind.Trace, 0, null)]) };
        Assert.Equal(3, bundle.OutOfScopeCount);
    }
    [Fact]
    public void Metric_rest_bucket_parity()
    {
        var row = MetricEvidenceProviderTests.Point("0", 1) with { Kind = "Histogram", Metric = JsonSerializer.Deserialize<JsonElement>(
            """{"histogram":{"dataPoints":[{"count":"6","sum":11,"bucketCounts":["1","2","3"],"explicitBounds":[1,3]}]}}""") };
        Assert.Equal(new[] { "1", "2", "3" }, Assert.Single(TelemetryWire.Record(row).Metric!.DataPoints).BucketCounts);
    }
    [Fact]
    public void Five_samples_and_ratio_three_positive()
    {
        var before = Enumerable.Range(1, 5).Select(i => MetricEvidenceProviderTests.Point("10", (ulong)i)).ToArray();
        var after = Enumerable.Range(1, 5).Select(i => MetricEvidenceProviderTests.Point("30", (ulong)i)).ToArray();
        Assert.Equal("Changed", MetricArithmetic.Compare(MetricArithmetic.Summarize(before), MetricArithmetic.Summarize(after), ExactMetricNumber.Integer(2)).State);
    }
    [Fact] public void Trace_parent_edge_parity() => Assert.Equal("parent", Span().ParentSpanId);
    [Fact] public void Trace_link_observation_parity() => Assert.Equal("linked", Assert.Single(Span().Links).SpanId);
    private static TelemetrySpanDto Span() => TelemetryWire.Record(MetricEvidenceProviderTests.Point("0", 1) with
    { Signal = TelemetrySignal.Traces, Metric = null, Span = JsonSerializer.Deserialize<JsonElement>(
        """{"traceId":"trace","spanId":"child","parentSpanId":"parent","links":[{"traceId":"trace","spanId":"linked"}]}""") }).Span!;
    [Fact]
    public async Task Matching_rule_finds_violation()
    {
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data,
            Enumerable.Range(1, 5).Select(i => MetricEvidenceProviderTests.Point("20", (ulong)i)).ToArray())) };
        var settings = new MetricEvidenceOptions { ThresholdRules = [new() { StableId = "explicit", MetricName = "metric", Unit = "ms", OwnerGroups = ["A"], Threshold = "20" }] };
        var provider = new MetricThresholdProvider(q, Options.Create(settings), Options.Create(new TelemetryEvidenceOptions()));
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var result = await provider.GatherAsync(new() { From = now, To = now.AddMinutes(1), BaselineFrom = now.AddMinutes(-1), BaselineTo = now },
            AccessScope.ForGroups("test", ["A"]), GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status); Assert.Single(result.Items);
        Assert.Equal("Violated", Assert.Single(result.Telemetry!.Decisions).State);
    }
}
