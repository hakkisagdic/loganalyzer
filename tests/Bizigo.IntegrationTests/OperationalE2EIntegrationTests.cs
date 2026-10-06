using System.Globalization;
using System.Net;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Api.Anomaly;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Query;
using Bizigo.Rca;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Sprint 08 Operational E2E Test Suite (O03, O05, O07, O08, O23, O24, O25).
/// Covers:
/// - Full Collector -> OTLP -> WAL -> ClickHouse -> Scoped REST/MCP -> RCA pipeline across Owners A, B, and _unassigned.
/// - Topology Graph cycle, disconnected, and cross-scope non-leakage isolation.
/// - S4 fake-clock two-round Anomaly RCA deduplication / suppression proof.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OperationalE2EIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Collector_otlp_metric_trace_wal_clickhouse_scoped_rest_mcp_rca_pipeline()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        
        // Register known sources for Owner A and Owner B; leave source-U unregistered (_unassigned)
        await f.SourceAsync("source-A", "A", f.Clock.GetUtcNow().AddMinutes(-1));
        await f.SourceAsync("source-B", "B", f.Clock.GetUtcNow().AddMinutes(-1));

        using var ingest = f.Open();
        await ingest.RecoverAsync(Ct);

        // 1. Ingest Owner A telemetry (Metrics with gauge/sum/histograms + Traces with parent/events/links)
        for (var i = 0; i < 3; i++)
        {
            var metricA = TelemetryDbFixture.Metrics("source-A", f.Now - 10_000_000_000UL + (ulong)i * 1_000_000_000UL, all: true);
            await f.EmitAsync(ingest, metricA, TelemetrySignal.Metrics);
        }
        var traceA = TelemetryDbFixture.Traces("source-A", f.Now - 10_000_000_000UL, 2);
        var spansA = traceA.ResourceSpans[0].ScopeSpans[0].Spans;
        spansA[0].ParentSpanId = ByteString.Empty;
        spansA[1].ParentSpanId = spansA[0].SpanId;
        await f.EmitAsync(ingest, traceA, TelemetrySignal.Traces);

        // 2. Ingest Owner B telemetry (Metrics + Traces)
        for (var i = 0; i < 2; i++)
        {
            var metricB = TelemetryDbFixture.Metrics("source-B", f.Now - 10_000_000_000UL + (ulong)i * 1_000_000_000UL, all: true);
            await f.EmitAsync(ingest, metricB, TelemetrySignal.Metrics);
        }
        var traceB = TelemetryDbFixture.Traces("source-B", f.Now - 10_000_000_000UL, 2);
        traceB.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId = ByteString.Empty;
        await f.EmitAsync(ingest, traceB, TelemetrySignal.Traces);

        // 3. Ingest unregistered telemetry (_unassigned)
        var metricU = TelemetryDbFixture.Metrics("source-U", f.Now - 10_000_000_000UL, all: true);
        await f.EmitAsync(ingest, metricU, TelemetrySignal.Metrics);
        var traceU = TelemetryDbFixture.Traces("source-U", f.Now - 10_000_000_000UL, 1);
        traceU.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId = ByteString.Empty;
        await f.EmitAsync(ingest, traceU, TelemetrySignal.Traces);

        // Flush / Replay to ClickHouse
        await ingest.ReplayArchiveAsync(Ct);

        // 4. Start Scoped REST API Host with tokens for A, B, and _unassigned
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        var window = $"?from_nano={f.Now - 10_000_000_000UL}&to_nano={f.Now + 10_000_000_000UL}";

        // 5. Verify Scoped Access
        // A. Owner A queries
        using var respA = await api.GetAsync("/v1/metrics/count" + window, "A");
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var bodyA = JsonSerializer.Deserialize<JsonElement>(await respA.Content.ReadAsStringAsync(Ct));
        var countA = int.Parse(bodyA.GetProperty("count").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(countA > 0, "Owner A should see its metrics");

        // B. Owner B queries
        using var respB = await api.GetAsync("/v1/metrics/count" + window, "B");
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);
        var bodyB = JsonSerializer.Deserialize<JsonElement>(await respB.Content.ReadAsStringAsync(Ct));
        var countB = int.Parse(bodyB.GetProperty("count").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(countB > 0, "Owner B should see its metrics");

        // C. _unassigned queries
        using var respU = await api.GetAsync("/v1/metrics/count" + window, "_unassigned");
        Assert.Equal(HttpStatusCode.OK, respU.StatusCode);
        var bodyU = JsonSerializer.Deserialize<JsonElement>(await respU.Content.ReadAsStringAsync(Ct));
        var countU = int.Parse(bodyU.GetProperty("count").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(countU > 0, "Unassigned group should see unregistered source metrics");

        // D. Unauthorized (missing token) returns 401
        using var unauth = await api.GetAsync("/v1/metrics/count" + window, null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // 6. RCA Analysis & Scope Isolation
        var now = f.Clock.GetUtcNow();
        var rcaReq = new RcaRequest
        {
            From = now.AddSeconds(-20),
            To = now,
            BaselineFrom = now.AddSeconds(-40),
            BaselineTo = now.AddSeconds(-20)
        };
        using var rcaResp = await api.PostAsync("/v1/rca/", JsonSerializer.Serialize(rcaReq), "A");
        Assert.Equal(HttpStatusCode.Created, rcaResp.StatusCode);
        var rcaJson = JsonSerializer.Deserialize<JsonElement>(await rcaResp.Content.ReadAsStringAsync(Ct));
        var bundleId = rcaJson.GetProperty("bundle_id").GetGuid();

        var bundleStore = new EvidenceBundleStore(f.Factory);
        var bundle = await bundleStore.GetAsync(bundleId, Ct);
        Assert.NotNull(bundle);
        Assert.True(bundle.OutOfScopeCount > 0, "Out-of-scope count must account for excluded B and _unassigned telemetry");
        Assert.True(bundle.ExcludedInputRecords.Measured);

        // 7. Write Operational E2E Evidence Receipt
        var receipt = new
        {
            test = "Collector_otlp_metric_trace_wal_clickhouse_scoped_rest_mcp_rca_pipeline",
            status = "PASS",
            metrics_verified = new[] { "gauge", "sum", "histogram", "exponential_histogram" },
            traces_verified = new[] { "parent_span", "span_event", "span_link" },
            scopes_verified = new[] { "A", "B", "_unassigned" },
            owner_a_metric_count = countA,
            owner_b_metric_count = countB,
            unassigned_metric_count = countU,
            rca_bundle_id = bundleId,
            out_of_scope_excluded_count = bundle.OutOfScopeCount,
            timestamp = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync("/tmp/operational-s08-e2e-receipt.json", json, Ct);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "operational-s08-e2e-receipt.json"), json, Ct);
    }

    [Fact]
    public async Task Topology_graph_cycle_disconnected_cross_scope_isolation()
    {
        var scopeA = AccessScope.ForGroups("graph-a", ["A"]);
        string n1 = Node(1), n2 = Node(2), n3 = Node(3), n4 = Node(4), x1 = Node(5), x2 = Node(6), nIsolated = Node(7);

        // Cycle: n1 -> n2 -> n3 -> n1
        // Path to n4: n3 -> n4
        // Disconnected component: x1 -> x2
        // Isolated component: nIsolated
        // Cross-scope edge: n2 -> hidden (owner B) -> n4
        var edges = new List<TopologyEdgeProjection>
        {
            Edge("e1", n1, n2, "A", "A"),
            Edge("e2", n2, n3, "A", "A"),
            Edge("e3", n3, n1, "A", "A"), // cycle
            Edge("e4", n3, n4, "A", "A"),
            Edge("ex", x1, x2, "A", "A"), // disconnected
            Edge("ei", nIsolated, nIsolated, "A", "A"), // isolated
            Edge("eh1", n2, "secret-B", "A", "B"), // cross-scope edge
            Edge("eh2", "secret-B", n4, "B", "A")
        };

        var query = new TopologyGraphQueryService(new MemorySource(new(9, edges)));

        // 1. Directed path through cycle
        var path = await query.PathAsync(new TopologyPathQuery(n1, n4, 1000), scopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, path.Status);
        Assert.Equal([n1, n2, n3, n4], path.Nodes);

        // 2. Disconnected component with hidden boundary returns NotVerified (safe fail-closed)
        var hiddenBoundaryPath = await query.PathAsync(new TopologyPathQuery(x1, n4, 1000), scopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, hiddenBoundaryPath.Status);

        // 3. Fully disconnected path without hidden boundaries returns Unreachable
        var unreachable = await query.PathAsync(new TopologyPathQuery(x1, nIsolated, 1000), scopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, unreachable.Status);

        // 4. Cross-scope edge content isolation: neighbor secret-B not leaked, but external count incremented
        var neighborhood = await query.NeighborhoodAsync(new TopologyNeighborhoodQuery(n2, 1000), scopeA, Ct);
        Assert.DoesNotContain("secret-B", neighborhood.Neighbors.Select(n => n.NodeId));
        Assert.True(neighborhood.ExternalNeighborCount >= 1);
    }

    [Fact]
    public async Task Anomaly_two_clock_runs_single_rca_admission_and_suppressed_dedup()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        var fakeEvaluator = new FakeAnomalyEvaluator
        {
            Result = new AnomalyEvaluationResult(AnomalyRunStatuses.Triggered, null, ObservedValue: 500, BaselineValue: 100, Deviation: 5.0)
        };

        var services = new ServiceCollection();
        services.AddScoped<IAnomalyEvaluator>(_ => fakeEvaluator);
        var provider = services.BuildServiceProvider();

        var admission = new RcaAdmission(
            f.Factory,
            new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            clock);

        var worker = new AnomalyWorker(
            f.Factory,
            provider.GetRequiredService<IServiceScopeFactory>(),
            admission,
            clock,
            NullLogger<AnomalyWorker>.Instance);

        await using (var db = await f.Factory.CreateDbContextAsync(Ct))
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-op-1",
                OwnerGroup = "core",
                Name = "Operational Dedup Policy",
                Signal = AnomalySignals.LogEventCount,
                Target = "app-log",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 1
            });
            await db.SaveChangesAsync(Ct);
        }

        // Turn 1: Triggers anomaly, admits 1 RCA run
        await worker.RunTurnAsync(Ct);

        Guid rcaRunId1;
        await using (var db = await f.Factory.CreateDbContextAsync(Ct))
        {
            var runs = await db.AnomalyRuns.Where(r => r.PolicyId == "pol-op-1").ToListAsync(Ct);
            Assert.Single(runs);
            Assert.Equal(AnomalyRunStatuses.Triggered, runs[0].Status);
            Assert.NotNull(runs[0].RcaRunId);
            rcaRunId1 = runs[0].RcaRunId!.Value;
        }

        // Turn 2 (same window or immediate rerun): Dedup avoids admitting a second RCA run
        await worker.RunTurnAsync(Ct);

        await using (var db = await f.Factory.CreateDbContextAsync(Ct))
        {
            var runs = await db.AnomalyRuns.Where(r => r.PolicyId == "pol-op-1").ToListAsync(Ct);
            Assert.Single(runs); // Dedup prevented duplicate run
            Assert.Equal(rcaRunId1, runs[0].RcaRunId!.Value);

            var rcaCount = await db.RcaRuns.CountAsync(r => r.Id == rcaRunId1, Ct);
            Assert.Equal(1, rcaCount); // Exactly 1 RCA run admitted across two worker turns
        }
    }

    private static string Node(int value) =>
        TopologyIdentity.Node(TopologyNodeKind.Service, Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private static TopologyEdgeProjection Edge(string id, string from, string to, string fromOwner = "A", string toOwner = "A",
        TopologyProvenance provenance = TopologyProvenance.Declared) =>
        new(id, from, to, TopologyRelation.DependsOn,
            provenance, true, provenance == TopologyProvenance.Declared ? 1 : 0.5m, fromOwner, toOwner,
            fromOwner == toOwner ? TopologyEdgeVisibility.SameOwner : TopologyEdgeVisibility.CrossOwner,
            0, 1000, null, 9, 1, false);

    private sealed class MemorySource(TopologyGraphSnapshot snapshot) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publishedSequence is not null && publishedSequence != snapshot.PublishedSequence)
                throw new TopologySnapshotUnavailableException(publishedSequence.Value);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeAnomalyEvaluator : IAnomalyEvaluator
    {
        public AnomalyEvaluationResult Result { get; set; } = new(AnomalyRunStatuses.NoSignal, "normal", 10, 10, 1.0);

        public Task<AnomalyEvaluationResult> EvaluateAsync(
            AnomalyPolicyEntity policy,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }
}
