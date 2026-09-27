using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: actual inventory transfer, replay, REST and production
/// providers prove historical ownership survives both HTTP and RCA consumption.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TelemetryEvidenceScopeIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact] public Task Metric_rest_historical_scope() => Historical(false, false);
    [Fact] public Task Trace_rest_historical_scope() => Historical(true, false);
    [Fact] public Task Metric_provider_historical_scope() => Historical(false, true);
    [Fact] public Task Trace_provider_historical_scope() => Historical(true, true);
    [Fact] public Task Allowed_A_provider_series() => Historical(false, true, false);
    [Fact] public Task Allowed_A_provider_edge() => Historical(true, true, false);

    private async Task Historical(bool traces, bool provider, bool includeOther = true)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("s04-moving", "A", f.Clock.GetUtcNow().AddSeconds(-30));
        var boundary = checked((ulong)await f.SourceAsync("s04-moving", "B", f.Clock.GetUtcNow().AddSeconds(-5)));
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        foreach (var after in new[] { false, true })
        {
            if (after && !includeOther) continue;
            var at = after ? boundary : boundary - 1_000_000_000UL;
            if (traces)
            {
                var request = TelemetryDbFixture.Traces("s04-moving", at, 2);
                var spans = request.ResourceSpans[0].ScopeSpans[0].Spans;
                spans[0].ParentSpanId = ByteString.Empty; spans[1].ParentSpanId = spans[0].SpanId;
                foreach (var span in spans)
                {
                    span.Links.Clear();
                    if (after) span.TraceId = ByteString.CopyFrom(Convert.FromHexString("ffeeddccbbaa99887766554433221100"));
                }
                await f.EmitAsync(ingest, request, signal);
            }
            else
                for (var i = 0; i < 5; i++)
                {
                    var request = TelemetryDbFixture.Metrics("s04-moving", at + (ulong)i, false);
                    request.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Gauge.DataPoints[0].AsInt = after ? 10000 : 10;
                    await f.EmitAsync(ingest, request, signal);
                }
        }
        await ingest.ReplayArchiveAsync(Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: provider);
        if (!provider)
        {
            var path = (traces ? "/v1/traces" : "/v1/metrics") + $"?from_nano={f.Now - 10_000_000_000UL}&to_nano={f.Now}";
            foreach (var owner in new[] { "A", "B" })
            {
                var body = await TelemetryApiIntegrationTests.Json(api, path, owner);
                var rows = body.GetProperty("records").EnumerateArray().ToArray();
                Assert.Equal(traces ? 2 : 5, rows.Length);
                Assert.All(rows, row => Assert.Equal(owner, row.GetProperty("owner_group").GetString()));
            }
            return;
        }
        var settings = api.App.Services.GetRequiredService<IOptions<MetricEvidenceOptions>>().Value;
        settings.ThresholdRules = [new() { StableId = "scope-A", MetricName = "same", Unit = "bytes", OwnerGroups = ["A"], Threshold = "50" }];
        using var scope = api.App.Services.CreateScope();
        var implementation = scope.ServiceProvider.GetServices<IEvidenceProvider>()
            .Single(p => p.Id == (traces ? "traces.error-propagation" : "metrics.threshold"));
        var now = f.Clock.GetUtcNow();
        var result = await implementation.GatherAsync(new RcaWindow { From = now.AddSeconds(-10), To = now,
            BaselineFrom = now.AddSeconds(-20), BaselineTo = now.AddSeconds(-10) },
            AccessScope.ForGroups(api.Subject("A"), ["A"]), GatherBudget.Default, Ct);
        Assert.NotEqual(EvidenceStatus.Failed, result.Status);
        if (traces)
        {
            Assert.Single(result.Items);
            Assert.DoesNotContain("ffeeddccbbaa99887766554433221100", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(result.Items);
            var values = Assert.Single(result.Telemetry!.Decisions).Values;
            Assert.Equal("5", values["event_samples"]); Assert.Equal("10", values["event_numerator"]);
        }
    }

    [Fact]
    public async Task Allowed_A_scalar_and_root_span_remain_visible()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("s04-allowed", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("s04-allowed", f.Now, false), TelemetrySignal.Metrics);
        var traces = TelemetryDbFixture.Traces("s04-allowed", f.Now);
        traces.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId = ByteString.Empty;
        traces.ResourceSpans[0].ScopeSpans[0].Spans[0].Links.Clear();
        await f.EmitAsync(ingest, traces, TelemetrySignal.Traces);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct);
        foreach (var path in new[] { "/v1/metrics", "/v1/traces" })
        {
            var body = await TelemetryApiIntegrationTests.Json(api, path + $"?from_nano={f.Now - 1}&to_nano={f.Now + 1}");
            Assert.Equal("A", Assert.Single(body.GetProperty("records").EnumerateArray()).GetProperty("owner_group").GetString());
        }
    }
}
