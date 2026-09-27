using System.Net;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: production provider DI/collector and RCA REST using actual
/// PG/CH/S3; persistent bundle and canonical outside-count parity after replay.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MetricTraceEvidenceIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // kapsam: CountExcludedTelemetryInputsAsync
    // A's canonical count excludes the three B metrics and two B spans once,
    // while response contents are checked not to disclose B's payload.
    [Fact]
    public async Task Four_production_providers_preserve_exact_decisions_and_distinct_outside_counts()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("evidence-A", "A", f.Clock.GetUtcNow().AddMinutes(-1));
        await f.SourceAsync("evidence-B", "B", f.Clock.GetUtcNow().AddMinutes(-1));
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        for (var i = 0; i < 5; i++)
        {
            foreach (var (offset, value) in new[] { (20UL, 10L), (10UL, 20L) })
            {
                var metric = TelemetryDbFixture.Metrics("evidence-A", f.Now - offset * 1000000000UL + (ulong)i * 1000000000UL, false);
                metric.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Gauge.DataPoints[0].AsInt = value;
                await f.EmitAsync(ingest, metric, TelemetrySignal.Metrics);
            }
        }
        for (var i = 0; i < 3; i++)
            await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("evidence-B", f.Now - 9000000000UL + (ulong)i, false), TelemetrySignal.Metrics);
        await f.EmitAsync(ingest, TelemetryDbFixture.Traces("evidence-B", f.Now - 9000000000UL, 2), TelemetrySignal.Traces);
        var traces = TelemetryDbFixture.Traces("evidence-A", f.Now - 9000000000UL, 2);
        var spans = traces.ResourceSpans[0].ScopeSpans[0].Spans;
        spans[0].ParentSpanId = Google.Protobuf.ByteString.Empty; spans[1].ParentSpanId = spans[0].SpanId;
        spans[0].Links.Clear(); spans[1].Links.Clear();
        await f.EmitAsync(ingest, traces, TelemetrySignal.Traces);
        await ingest.ReplayArchiveAsync(Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        var settings = api.App.Services.GetRequiredService<IOptions<MetricEvidenceOptions>>().Value;
        settings.ThresholdRules = [new() { StableId = "explicit-A", Version = 7, MetricName = "same", Unit = "bytes", OwnerGroups = ["A"], Threshold = "20" }];
        var input = new RcaRequest { From = f.Clock.GetUtcNow().AddSeconds(-10), To = f.Clock.GetUtcNow(),
            BaselineFrom = f.Clock.GetUtcNow().AddSeconds(-20), BaselineTo = f.Clock.GetUtcNow().AddSeconds(-10) };
        using var response = await api.PostAsync("/v1/rca/", JsonSerializer.Serialize(input));
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        var report = JsonSerializer.Deserialize<JsonElement>(text);
        var bundleId = report.GetProperty("bundle_id").GetGuid();
        var bundle = (await new EvidenceBundleStore(f.Factory).GetAsync(bundleId, Ct))!;
        TelemetryDbFixture.Evidence("s04-provider-diagnostic", bundle);
        Assert.True(bundle.OutOfScopeCount == 5, JsonSerializer.Serialize(new { bundle.ExcludedInputRecords, bundle.Slices }));
        Assert.True(bundle.ExcludedInputRecords.Measured);
        Assert.Equal(3, bundle.ExcludedInputRecords.Kinds.Single(k => k.Kind == EvidenceKind.Metric).Count);
        Assert.Equal(2, bundle.ExcludedInputRecords.Kinds.Single(k => k.Kind == EvidenceKind.Trace).Count);
        var expected = new[] { "metrics.baseline", "metrics.threshold", "traces.error-propagation", "traces.service-dependency" };
        foreach (var id in expected)
        {
            var slice = Assert.Single(bundle.Slices, s => s.ProviderId == id);
            Assert.True(slice.Status != EvidenceStatus.Failed, $"{id}: {slice.Detail}");
            Assert.NotEqual(EvidenceStatus.NotRegistered, slice.Status);
            Assert.NotNull(slice.Telemetry);
        }
        var baseline = bundle.Slices.Single(s => s.ProviderId == "metrics.baseline");
        Assert.Equal(EvidenceStatus.Gathered, baseline.Status); Assert.Single(baseline.Items);
        Assert.Equal("Changed", Assert.Single(baseline.Telemetry!.Decisions).State);
        Assert.Equal("2", baseline.Telemetry.Decisions[0].Values["ratio_numerator"]);
        var threshold = bundle.Slices.Single(s => s.ProviderId == "metrics.threshold");
        Assert.Equal(EvidenceStatus.Gathered, threshold.Status); Assert.Single(threshold.Items);
        Assert.DoesNotContain("secret-for-evidence-B", text, StringComparison.Ordinal);
        var stored = BundleSerializer.Serialize(bundle);
        settings.ChangeFactor = 99; settings.ThresholdRules[0].Version = 8; settings.ThresholdRules[0].Threshold = "999";
        Assert.Equal(stored, BundleSerializer.Serialize((await new EvidenceBundleStore(f.Factory).GetAsync(bundleId, Ct))!));
        var reopened = await TelemetryApiIntegrationTests.Json(api, "/v1/rca/" + bundleId);
        // PostgreSQL jsonb reorders nested dictionary keys. Content identity must
        // survive that real storage transformation, not just a .NET roundtrip.
        Assert.Equal(report.GetProperty("content_hash").GetString(), reopened.GetProperty("content_hash").GetString());
        Assert.Equal(report.GetProperty("content_hash").GetString(), bundle.ContentHash);
        await using var storedDb = await f.Factory.CreateDbContextAsync(Ct);
        Assert.Equal((await storedDb.EvidenceBundles.SingleAsync(b => b.Id == bundleId, Ct)).ContentHash, bundle.ContentHash);
        Assert.Equal(5, reopened.GetProperty("out_of_scope_count").GetInt64());
        Assert.Equal(5, reopened.GetProperty("excluded_input_records").GetProperty("total").GetInt64());
        var audit = await f.Db.AuditLog.AsNoTracking().Where(r => r.Subject == api.Subject("A")).ToArrayAsync(Ct);
        Assert.Single(audit, r => r.Action == "telemetry.Metrics.excluded-inputs");
        Assert.Single(audit, r => r.Action == "telemetry.Traces.excluded-inputs");
        Assert.NotEmpty(audit);
        TelemetryDbFixture.Evidence("s04-provider-bundle-count", new { report, reopened, bundle, audit });
    }

    [Fact]
    public async Task Outside_one_provider_count_three()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("outside-only", "B", f.Clock.GetUtcNow().AddMinutes(-1));
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        for (var i = 0; i < 3; i++)
            await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("outside-only", f.Now - 1_000_000_000UL + (ulong)i, false), TelemetrySignal.Metrics);
        var now = f.Clock.GetUtcNow();
        var window = new RcaWindow { From = now.AddSeconds(-10), To = now, BaselineFrom = now.AddSeconds(-20), BaselineTo = now.AddSeconds(-10) };
        var excluded = await ExcludedInputRecords.MeasureAsync(f.Query, window, AccessScope.ForGroups("one", ["A"]), Ct);
        var bundle = new EvidenceBundle { Id = Guid.NewGuid(), SchemaVersion = 2, GatheredAt = now, Window = window,
            Scope = new(["A"], false), Trust = WindowTrust.Unmeasured, ExcludedInputs = excluded,
            Slices = [new() { ProviderId = "one", Kind = EvidenceKind.Metric, Status = EvidenceStatus.Empty, OutOfScopeCount = 3 }] };
        Assert.Equal(3, bundle.OutOfScopeCount); Assert.True(excluded.Measured);
    }

    [Fact]
    public async Task Rca_global_invalid_windows_reject_before_providers()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        var end = f.Clock.GetUtcNow(); var start = end.AddMinutes(-10);
        var initialBundles = await f.Db.EvidenceBundles.CountAsync(Ct);
        var initialRuns = await f.Db.RcaRuns.CountAsync(Ct);
        foreach (var baseline in new[] { (start.AddMinutes(-5), start.AddMinutes(5)), (end.AddMinutes(10), end.AddMinutes(20)) })
        {
            using var response = await api.PostAsync("/v1/rca/", JsonSerializer.Serialize(new RcaRequest
            { From = start, To = end, BaselineFrom = baseline.Item1, BaselineTo = baseline.Item2 }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(initialBundles, await f.Db.EvidenceBundles.CountAsync(Ct));
        Assert.Equal(initialRuns, await f.Db.RcaRuns.CountAsync(Ct));
        Assert.False(await f.Db.AuditLog.AnyAsync(r => r.Subject == api.Subject("A"), Ct));
    }
}
