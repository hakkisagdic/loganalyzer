using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Ingest.Otlp;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>Accepted Sprint04 numeric, grouping and incomplete-window oracles.</summary>
public sealed class MetricEvidenceOracleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AccessScope Scope = AccessScope.ForGroups("oracle", ["A"]);
    private static RcaWindow Window => new()
    {
        From = DateTimeOffset.UnixEpoch.AddMinutes(10), To = DateTimeOffset.UnixEpoch.AddMinutes(20),
        BaselineFrom = DateTimeOffset.UnixEpoch, BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(10),
    };
    private static TelemetryRecord[] Rows(string value) => Enumerable.Range(0, 5)
        .Select(i => MetricEvidenceProviderTests.Point(value, (ulong)i + 1)).ToArray();
    private static MetricThresholdRule Rule(string id = "rule", string op = "gte") => new()
    { StableId = id, MetricName = "metric", OwnerGroups = ["A"], Unit = "ms", Threshold = "10", Operator = op };
    private static MetricThresholdProvider Threshold(RecordingScopedQuery query, params MetricThresholdRule[] rules) =>
        new(query, Options.Create(new MetricEvidenceOptions { ThresholdRules = rules }), Options.Create(new TelemetryEvidenceOptions()));

    [Theory]
    [InlineData("gt", "9", false)] [InlineData("gt", "10", false)] [InlineData("gt", "11", true)]
    [InlineData("gte", "9", false)] [InlineData("gte", "10", true)] [InlineData("gte", "11", true)]
    [InlineData("lt", "9", true)] [InlineData("lt", "10", false)] [InlineData("lt", "11", false)]
    [InlineData("lte", "9", true)] [InlineData("lte", "10", true)] [InlineData("lte", "11", false)]
    public async Task Threshold_operator_equality_and_multiple_rules(string op, string value, bool breach)
    {
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, Rows(value))) };
        var result = await Threshold(q, Rule("z", op), Rule("a", op)).GatherAsync(Window, Scope, GatherBudget.Default, Ct);
        Assert.Equal(new[] { "a", "z" }, result.Telemetry!.Decisions.Select(d => d.Values["rule_id"]));
        Assert.All(result.Telemetry.Decisions, d => Assert.Equal(breach ? "Violated" : "Evaluated", d.State));
        Assert.Equal(breach ? 2 : 0, result.Items.Count);
    }

    [Theory]
    [InlineData("kind")] [InlineData("unit")] [InlineData("temporality")]
    [InlineData("monotonic")] [InlineData("resource")] [InlineData("scope")] [InlineData("attributes")]
    [InlineData("historical-owner")] [InlineData("histogram")]
    public async Task Heterogeneous_series_never_cancel_each_others_baseline_change(string dimension)
    {
        TelemetryRecord Make(string value, int index, bool other)
        {
            var time = (ulong)(index + 1) * 1_000_000_000;
            var point = new NumberDataPoint { TimeUnixNano = time, AsInt = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
            if (other && dimension == "attributes") point.Attributes.Add(new KeyValue { Key = "port", Value = new AnyValue { StringValue = "other" } });
            var metric = new Metric { Name = "metric", Unit = other && dimension == "unit" ? "seconds" : "ms" };
            if (dimension is "temporality" or "monotonic" || (other && dimension == "kind"))
                metric.Sum = new Sum { DataPoints = { point }, AggregationTemporality = other && dimension == "temporality" ? AggregationTemporality.Cumulative : AggregationTemporality.Delta,
                    IsMonotonic = other && dimension == "monotonic" };
            else if (other && dimension == "histogram") metric.Histogram = new Histogram { DataPoints = { new HistogramDataPoint {
                TimeUnixNano = time, Count = 1, Sum = point.AsInt, BucketCounts = { 1UL, 0UL }, ExplicitBounds = { 100d } } } };
            else metric.Gauge = new Gauge { DataPoints = { point } };
            var resource = new Resource();
            if (other && dimension == "resource") resource.Attributes.Add(new KeyValue { Key = "host", Value = new AnyValue { StringValue = "other" } });
            var leaf = new TelemetryLeaf(index.ToString(System.Globalization.CultureInfo.InvariantCulture), resource,
                new InstrumentationScope { Name = other && dimension == "scope" ? "other" : "scope" }, "", "", metric, null);
            var envelope = new RawSignalEnvelope(2, Guid.NewGuid(), TelemetrySignal.Metrics, "application/x-protobuf", DateTimeOffset.UnixEpoch, RawSignalEnvelope.Hash([]), [], 1, [leaf.Key], 0);
            return TelemetryMaterializer.Materialize(envelope, leaf,
                new(leaf.Key, "source", other && dimension == "historical-owner" ? "B" : "A", 1, time, "known"));
        }
        var q = new RecordingScopedQuery { TelemetrySearch = (request, _, _) =>
        {
            var baseline = request.FromNano == 0;
            var first = Enumerable.Range(0, 5).Select(i => Make(baseline ? "10" : "30", i, false));
            var second = Enumerable.Range(0, 5).Select(i => Make(baseline ? "30" : "10", i, true));
            return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [.. first, .. second]));
        } };
        var result = await new MetricBaselineProvider(q, Options.Create(new MetricEvidenceOptions()), Options.Create(new TelemetryEvidenceOptions()))
            .GatherAsync(Window, AccessScope.ForGroups("oracle", ["A", "B"]), GatherBudget.Default, Ct);
        Assert.Equal(2, result.Telemetry!.Decisions.Count);
        Assert.All(result.Telemetry.Decisions, d => Assert.Equal("Changed", d.State));
        Assert.Equal(2, result.Items.Count);
        var rule = Rule(); rule.OwnerGroups = ["A", "B"]; rule.Threshold = "15";
        var threshold = await Threshold(q, rule).GatherAsync(Window, AccessScope.ForGroups("oracle", ["A", "B"]), GatherBudget.Default, Ct);
        Assert.Equal(2, threshold.Telemetry!.Decisions.Count);
        Assert.Single(threshold.Items);
        Assert.Single(threshold.Telemetry.Decisions, d => d.State == "Violated");
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("groups")] [InlineData("operator")] [InlineData("statistic")]
    [InlineData("nan")] [InlineData("infinity")] [InlineData("minimum")] [InlineData("factor-one")]
    [InlineData("factor-nan")] [InlineData("factor-infinity")]
    public void Rule_configuration_rejects_invalid_and_ambiguous_input(string fault)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddBizigoEvidence();
        services.Configure<MetricEvidenceOptions>(o =>
        {
            o.ThresholdRules = [Rule()];
            switch (fault)
            {
                case "duplicate": o.ThresholdRules = [Rule(), Rule()]; break;
                case "groups": o.ThresholdRules[0].OwnerGroups = []; break;
                case "operator": o.ThresholdRules[0].Operator = "eq"; break;
                case "statistic": o.ThresholdRules[0].Statistic = "maximum"; break;
                case "nan": o.ThresholdRules[0].Threshold = "NaN"; break;
                case "infinity": o.ThresholdRules[0].Threshold = "Infinity"; break;
                case "minimum": o.MinimumSamples = 0; break;
                case "factor-one": o.ChangeFactor = 1; break;
                case "factor-nan": o.ChangeFactor = double.NaN; break;
                case "factor-infinity": o.ChangeFactor = double.PositiveInfinity; break;
            }
        });
        using var sp = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public async Task Mixed_rules_preserve_versions_reasons_and_only_breached_findings()
    {
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, Rows("10"))) };
        var z = Rule("z", "gt"); z.Version = 7;
        var a = Rule("A", "gte"); a.Version = 3;
        var result = await Threshold(q, z, a).GatherAsync(Window, Scope, GatherBudget.Default, Ct);
        Assert.Equal(new[] { "A", "z" }, result.Telemetry!.Decisions.Select(d => d.Values["rule_id"]));
        Assert.Equal(new[] { "3", "7" }, result.Telemetry.Decisions.Select(d => d.Values["rule_version"]));
        Assert.Equal("ThresholdExceeded", result.Telemetry.Decisions[0].Reason);
        Assert.Equal("Evaluated", result.Telemetry.Decisions[1].State);
        var item = Assert.Single(result.Items);
        Assert.Contains("A", JsonSerializer.Serialize(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_unit_is_visible_and_valid_custom_minimum_remains_supported()
    {
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, Rows("20"))) };
        var rule = Rule(); rule.Unit = "seconds";
        var result = await Threshold(q, rule).GatherAsync(Window, Scope, GatherBudget.Default, Ct);
        Assert.Empty(result.Items); Assert.Equal("UnitMismatch", Assert.Single(result.Telemetry!.Decisions).Reason);
        Assert.True(new MetricEvidenceOptions { MinimumSamples = 3, ChangeFactor = 4, ThresholdRules = [Rule()] }.Valid());
    }

    [Theory]
    [InlineData(false, "partial")] [InlineData(true, "partial")]
    [InlineData(false, "pages")] [InlineData(true, "pages")]
    [InlineData(false, "bytes")] [InlineData(true, "bytes")]
    [InlineData(false, "failed")] [InlineData(true, "failed")]
    [InlineData(false, "exception")] [InlineData(true, "exception")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    [InlineData(false, "query-cancel")] [InlineData(true, "query-cancel")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Incomplete_baseline_cannot_assert_deviation(bool baselineFault, string fault)
        => await Incomplete(baselineFault, fault, false);

    [Theory]
    [InlineData("partial")] [InlineData("pages")] [InlineData("bytes")]
    [InlineData("failed")] [InlineData("exception")] [InlineData("timeout")] [InlineData("cancel")] [InlineData("query-cancel")]
    public async Task Incomplete_threshold_event_cannot_assert_violation(string fault) => await Incomplete(false, fault, true);

    private static async Task Incomplete(bool baselineFault, string fault, bool threshold)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var q = new RecordingScopedQuery { TelemetrySearch = async (request, _, token) =>
        {
            if ((request.FromNano == 0) != baselineFault)
                return new TelemetryPage(TelemetryResultStatus.Data, Rows("10"));
            entered.TrySetResult();
            if (fault == "exception") throw new IOException("fixture query failure");
            if (fault == "query-cancel") throw new OperationCanceledException("query-side cancellation; caller remains active");
            if (fault is "timeout" or "cancel")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (fault == "failed") return new(TelemetryResultStatus.Failed, [], Error: "fixture failure");
            if (fault == "bytes") return new(TelemetryResultStatus.Data,
                Rows("30").Select(p => p with { Resource = JsonSerializer.SerializeToElement(new { large = new string('x', 40000) }) }).ToArray());
            return new(TelemetryResultStatus.Data, Rows("30"), Partial: true, Cursor: fault == "pages" ? "next" : null);
        } };
        var limits = new TelemetryEvidenceOptions { MaxPages = 2, TimeoutSeconds = 1,
            MaxBytes = fault == "bytes" ? 8192 : 4 * 1024 * 1024 };
        IEvidenceProvider provider = threshold
            ? new MetricThresholdProvider(q, Options.Create(new MetricEvidenceOptions { ThresholdRules = [Rule()] }), Options.Create(limits))
            : new MetricBaselineProvider(q, Options.Create(new MetricEvidenceOptions()), Options.Create(limits));
        var collector = new EvidenceCollector([provider], NullLogger<EvidenceCollector>.Instance);
        var run = collector.GatherAsync(Window, Scope, GatherBudget.Default, cancel.Token);
        if (fault == "cancel")
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2), Ct));
            return;
        }
        var report = await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(entered.Task.IsCompletedSuccessfully, "The designated event/baseline query must actually be reached.");
        var slice = Assert.Single(report.Slices, s => s.ProviderId == provider.Id);
        Assert.Empty(slice.Items); Assert.True(report.IsPartial);
        Assert.Equal(fault is "failed" or "exception" or "timeout" or "query-cancel" ? EvidenceStatus.Failed : EvidenceStatus.Unavailable, slice.Status);
        Assert.False(string.IsNullOrWhiteSpace(slice.Detail));
    }
}
