using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Storage.ClickHouse;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>Actual PG inventory/audit + CH keyset pages through production DI providers.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TraceEvidenceBudgetIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("traces.error-propagation")]
    [InlineData("traces.service-dependency")]
    public async Task Trace_provider_real_database_budget_boundaries(string providerId)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("trace-budget", "A", f.Clock.GetUtcNow().AddMinutes(-1));
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var request = TelemetryDbFixture.Traces("trace-budget", f.Now - 1_000_000_000UL, 201);
        var template = request.ResourceSpans[0];
        var resources = template.ScopeSpans[0].Spans.Select((span, i) =>
        {
            var resource = template.Clone();
            resource.ScopeSpans[0].Spans.Clear();
            var node = span.Clone(); node.Links.Clear();
            node.ParentSpanId = i == 0 ? ByteString.Empty : template.ScopeSpans[0].Spans[i - 1].SpanId;
            resource.ScopeSpans[0].Spans.Add(node);
            resource.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "service-" + i;
            return resource;
        }).ToArray();
        request.ResourceSpans.Clear(); request.ResourceSpans.Add(resources);
        await f.EmitAsync(ingest, request, TelemetrySignal.Traces);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        using var services = api.App.Services.CreateScope();
        var provider = services.ServiceProvider.GetServices<IEvidenceProvider>().Single(p => p.Id == providerId);
        var limits = api.App.Services.GetRequiredService<IOptions<TelemetryEvidenceOptions>>().Value;
        var scope = AccessScope.ForGroups(api.Subject("A"), ["A"]);
        var now = f.Clock.GetUtcNow();
        var window = new RcaWindow { From = now.AddSeconds(-10), To = now,
            BaselineFrom = now.AddSeconds(-20), BaselineTo = now.AddSeconds(-10), SourceIds = ["trace-budget"] };
        var all = await f.Query.SearchTelemetryAsync(f.Window(TelemetrySignal.Traces, "trace-budget") with { Limit = 1000 }, scope, Ct);
        Assert.Equal(201, all.Records.Count); Assert.False(all.Partial);
        var exactBytes = all.Records.Sum(r => JsonSerializer.SerializeToUtf8Bytes(r, RawSignalCodec.Json).Length);
        var results = new List<object>();
        foreach (var dimension in new[] { "span", "relation", "page", "byte" })
        foreach (var delta in new[] { -1, 0, 1 })
        {
            limits.MaxRecords = 1000; limits.MaxRelations = 2000; limits.MaxPages = 10; limits.MaxBytes = 4 * 1024 * 1024;
            switch (dimension)
            {
                case "span": limits.MaxRecords = 201 + delta; break;
                case "relation": limits.MaxRelations = 200 + delta; break;
                case "page": limits.MaxPages = 3 + delta; break;
                case "byte": limits.MaxBytes = exactBytes + delta; break;
            }
            var queries = new List<TelemetrySqlPlan>(); f.Reader.ObserveQuery = queries.Add;
            var slice = await provider.GatherAsync(window, scope, new GatherBudget(1000, TimeSpan.FromSeconds(10)), Ct);
            var partial = delta < 0;
            Assert.Equal(EvidenceStatus.Gathered, slice.Status); Assert.Equal(partial, slice.Truncated);
            Assert.Equal(partial ? "NotComparable" : "Evaluated", slice.Telemetry!.Evaluation);
            var decision = Assert.Single(slice.Telemetry.Decisions);
            Assert.Equal(partial ? "Partial" : "Complete", decision.State);
            Assert.Equal(partial ? "BudgetExceeded" : "", decision.Reason);
            using var edges = JsonDocument.Parse(decision.Values["error_edges"]!);
            Assert.Equal(partial ? 199 : 200, edges.RootElement.GetArrayLength());
            Assert.Equal(providerId == "traces.error-propagation" ? 1 : partial ? 199 : 200, slice.Items.Count);
            Assert.Equal(dimension == "page" && partial ? 2 : 3, queries.Count);
            Assert.All(queries, plan => Assert.Contains("logical_id", plan.Sql, StringComparison.Ordinal));
            results.Add(new { dimension, delta, exactBytes, slice, queries });
        }
        f.Reader.ObserveQuery = null;
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.Subject == api.Subject("A") && a.Action == "telemetry.Traces.search").ToArrayAsync(Ct);
        Assert.Equal(36, audit.Length); // 1 byte-oracle read + (12 * 3 - one stopped page).
        Assert.All(audit, a => Assert.True(a.Succeeded));
        TelemetryDbFixture.Evidence("s04-trace-budget-" + providerId, new { results, audit });
    }
}
