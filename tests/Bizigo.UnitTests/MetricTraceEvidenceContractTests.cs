using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class MetricTraceEvidenceContractTests
{
    [Theory]
    [InlineData(false, 5L, 5L, true)]
    [InlineData(true, null, 2L, false)]
    public async Task Outside_counts_are_distinct_and_nullable(bool failMetrics, long? total, long subtotal, bool measured)
    {
        var seen = new List<TelemetryInputWindow>();
        var q = new RecordingScopedQuery { ExcludedTelemetryCount = (w, _, _) =>
        {
            seen.Add(w);
            return Task.FromResult(w.Signal == TelemetrySignal.Metrics
                ? new TelemetryCount(failMetrics ? TelemetryResultStatus.Failed : TelemetryResultStatus.Data, failMetrics ? null : 3, failMetrics ? "DatabaseUnavailable" : null)
                : new TelemetryCount(TelemetryResultStatus.Data, 2));
        } };
        var result = await ExcludedInputRecords.MeasureAsync(q, Window(), AccessScope.ForGroups("u", ["A"]), TestContext.Current.CancellationToken);
        Assert.Equal(2, seen.Count); Assert.Single(seen, w => w.Signal == TelemetrySignal.Metrics);
        Assert.Single(seen, w => w.Signal == TelemetrySignal.Traces);
        Assert.All(seen, w => { Assert.Equal(TelemetryEvidenceReader.Nano(Window().From), w.EventFrom); Assert.Equal(TelemetryEvidenceReader.Nano(Window().BaselineFrom), w.BaselineFrom); });
        Assert.Equal(total, result.Total); Assert.Equal(subtotal, result.KnownSubtotal); Assert.Equal(measured, result.Measured);
    }

    [Fact]
    public void Legacy_bundle_count_is_not_reinterpreted()
    {
        var legacy = new EvidenceBundle { Id = Guid.NewGuid(), GatheredAt = Window().To, SchemaVersion = 1,
            Window = Window(), Scope = new(["A"], false), Trust = WindowTrust.Unmeasured,
            Slices = [new() { ProviderId = "old", Kind = EvidenceKind.Metric, Status = EvidenceStatus.Empty, OutOfScopeCount = 8 }] };
        var reopened = BundleSerializer.Deserialize(BundleSerializer.Serialize(legacy));
        Assert.Null(reopened.OutOfScopeCount); Assert.False(reopened.ExcludedInputRecords.Measured);
        Assert.Equal(8, reopened.ExcludedInputRecords.LegacyReportedCount); Assert.Equal("LegacySemantics", reopened.ExcludedInputRecords.Reason);
        var current = legacy with { SchemaVersion = 2, ExcludedInputs = new([new(EvidenceKind.Metric, 3, null), new(EvidenceKind.Trace, 2, null)]) };
        var roundTrip = BundleSerializer.Deserialize(BundleSerializer.Serialize(current));
        Assert.Equal(5, roundTrip.OutOfScopeCount); Assert.Equal(current.ContentHash, roundTrip.ContentHash);
        Assert.NotEqual(current.ContentHash, (current with { ExcludedInputs = new([new(EvidenceKind.Metric, null, "Failed"), new(EvidenceKind.Trace, 2, null)]) }).ContentHash);
    }

    [Fact]
    public void Production_di_requires_four_distinct_providers_and_no_exemption()
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddScoped<IScopedQuery, RecordingScopedQuery>(); services.AddBizigoEvidence();
        using var sp = services.BuildServiceProvider(); using var scope = sp.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IEvidenceProvider>().ToArray();
        Assert.Single(providers.OfType<MetricBaselineProvider>()); Assert.Single(providers.OfType<MetricThresholdProvider>());
        Assert.Single(providers.OfType<TraceErrorPropagationProvider>()); Assert.Single(providers.OfType<TraceServiceDependencyProvider>());
        Assert.Empty(EvidenceKinds.Exempt); Assert.Equal(0, EvidenceKinds.ExpectedExemptCount);
    }

    [Fact]
    public async Task No_rule_preserves_feed_and_reports_no_evaluation()
    {
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [MetricEvidenceProviderTests.Point("20", 1)])) };
        var provider = new MetricThresholdProvider(q, Options.Create(new MetricEvidenceOptions()), Options.Create(new TelemetryEvidenceOptions()));
        var result = await provider.GatherAsync(Window(), AccessScope.ForGroups("u", ["A"]), GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Empty(result.Items); Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Equal(TelemetryResultStatus.Data, result.Telemetry!.Feed); Assert.Equal("NoRule", result.Telemetry.Evaluation);
        Assert.NotNull(result.Telemetry.PolicyHash);
    }

    [Theory]
    [InlineData("metrics.baseline", EvidenceKind.Metric)]
    [InlineData("metrics.threshold", EvidenceKind.Metric)]
    [InlineData("traces.error-propagation", EvidenceKind.Trace)]
    [InlineData("traces.service-dependency", EvidenceKind.Trace)]
    public async Task Missing_expected_provider_is_visible_by_identity(string missing, EvidenceKind kind)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped<IScopedQuery, RecordingScopedQuery>(); services.AddBizigoEvidence();
        using var sp = services.BuildServiceProvider(); using var scope = sp.CreateScope();
        var providers = scope.ServiceProvider.GetServices<IEvidenceProvider>().Where(p => p.Id != missing).ToArray();
        var collector = new EvidenceCollector(providers, Microsoft.Extensions.Logging.Abstractions.NullLogger<EvidenceCollector>.Instance,
            sp.GetRequiredService<EvidenceProviderRequirements>());
        var report = await collector.GatherAsync(Window(), AccessScope.ForGroups("u", ["A"]), GatherBudget.Default, TestContext.Current.CancellationToken);
        var absent = Assert.Single(report.Slices, s => s.ProviderId == missing);
        Assert.Equal(kind, absent.Kind); Assert.Equal(EvidenceStatus.NotRegistered, absent.Status);
        Assert.Contains(report.NotConsulted, s => s.ProviderId == missing); Assert.True(report.IsPartial);
    }

    [Theory]
    [InlineData(EvidenceStatus.Gathered, EvidenceStatus.Failed, EvidenceStatus.Gathered, true)]
    [InlineData(EvidenceStatus.Gathered, EvidenceStatus.NotRegistered, EvidenceStatus.Gathered, true)]
    [InlineData(EvidenceStatus.Empty, EvidenceStatus.Failed, EvidenceStatus.Failed, true)]
    [InlineData(EvidenceStatus.Empty, EvidenceStatus.NotRegistered, EvidenceStatus.NotRegistered, true)]
    [InlineData(EvidenceStatus.Empty, EvidenceStatus.Unavailable, EvidenceStatus.Unavailable, true)]
    [InlineData(EvidenceStatus.Empty, EvidenceStatus.NeverFed, EvidenceStatus.NeverFed, true)]
    [InlineData(EvidenceStatus.Empty, EvidenceStatus.Empty, EvidenceStatus.Empty, false)]
    public void Kind_coverage_truth_table_preserves_partial(EvidenceStatus first, EvidenceStatus second, EvidenceStatus expected, bool partial)
    {
        var coverage = Assert.Single(EvidenceKindCoverage.From([
            new() { ProviderId = "metrics.baseline", Kind = EvidenceKind.Metric, Status = first },
            new() { ProviderId = "metrics.threshold", Kind = EvidenceKind.Metric, Status = second },
        ]), c => c.Kind == EvidenceKind.Metric);
        Assert.Equal(expected, coverage.Status); Assert.Equal(partial, coverage.Partial);
    }

    [Fact]
    public void Both_never_fed_override_no_rule_evaluation_without_claiming_empty()
    {
        var coverage = Assert.Single(EvidenceKindCoverage.From([
            new() { ProviderId = "metrics.baseline", Kind = EvidenceKind.Metric, Status = EvidenceStatus.NeverFed,
                Telemetry = new(TelemetryResultStatus.NeverFed, "NotRun", []) },
            new() { ProviderId = "metrics.threshold", Kind = EvidenceKind.Metric, Status = EvidenceStatus.Unavailable,
                Telemetry = new(TelemetryResultStatus.NeverFed, "NoRule", []) },
        ]), c => c.Kind == EvidenceKind.Metric);
        Assert.Equal(EvidenceStatus.NeverFed, coverage.Status); Assert.True(coverage.Partial);
        Assert.Equal(2, coverage.NotConsulted.Count);
    }

    [Fact]
    public async Task Unequal_legacy_rca_preserves_existing_providers()
    {
        var calls = 0;
        var q = new RecordingScopedQuery { TelemetrySearch = (_, _, _) => { calls++; return Task.FromResult(new TelemetryPage(TelemetryResultStatus.Data, [])); } };
        var provider = new MetricBaselineProvider(q, Options.Create(new MetricEvidenceOptions()), Options.Create(new TelemetryEvidenceOptions()));
        var window = Window() with { BaselineFrom = Window().BaselineFrom.AddMinutes(-10) };
        window.Validate();
        var result = await provider.GatherAsync(window, AccessScope.ForGroups("u", ["A"]), GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal("WindowMismatch", result.Detail); Assert.Equal(EvidenceStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    public void Rca_global_invalid_windows_reject_before_providers(int minutesAfterStart)
    {
        var window = Window() with { BaselineTo = Window().From.AddMinutes(minutesAfterStart) };
        Assert.Throws<ArgumentException>(window.Validate);
    }

    private static RcaWindow Window()
    {
        var from = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        return new() { From = from, To = from.AddMinutes(10), BaselineFrom = from.AddMinutes(-10), BaselineTo = from };
    }
}
