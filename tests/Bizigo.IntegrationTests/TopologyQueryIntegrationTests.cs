using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>V07: plans are captured from the production reader, never reimplemented in the fixture.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyQueryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Declared_only_snapshot_works_during_observed_repair_without_clickhouse_read()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var owner = "declared-repair-" + Guid.NewGuid().ToString("N");
        var scope = AccessScope.ForGroups(owner, [owner]);
        var registry = new TopologyRegistry(fixture.Factory);
        var from = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "from", owner, true, []), Ct)).Node!.Id;
        var to = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "to", owner, true, []), Ct)).Node!.Id;
        var declared = await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(from, to, "depends_on"), Ct);
        Assert.Equal(201, declared.Status);

        var revision = new RepairingRevision();
        var observedRead = false;
        var observed = new TopologyObservedSnapshotReader(fixture.Storage)
        {
            ObserveQuery = _ => observedRead = true,
        };
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(
            fixture.Factory, observed, new TopologyPublicationFence(revision)));
        var asOf = (decimal)DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds() * 1_000_000m;

        var nodes = await graph.SearchNodesAsync(new(asOf), scope, Ct);
        Assert.Contains(nodes.Items, node => node.Id == from);
        Assert.Contains(nodes.Items, node => node.Id == to);
        var edges = await graph.SearchEdgesAsync(new(asOf,
            Provenance: TopologyProvenance.Declared), scope, Ct);
        Assert.Single(edges.Items);
        Assert.False(observedRead);
        Assert.Equal(4, revision.Modes.Count);
        Assert.All(revision.Modes, mode => Assert.Equal(TopologyReadMode.DeclaredOnly, mode));

        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            graph.SearchEdgesAsync(new(asOf, Provenance: TopologyProvenance.Observed), scope, Ct));
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            graph.GetEdgeAsync(Guid.NewGuid().ToString("D"), asOf, scope, Ct));
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            graph.PathAsync(new(from, to, asOf), scope, Ct));
        Assert.False(observedRead);
        Assert.Equal(3, revision.Modes.Count(mode => mode == TopologyReadMode.ObservedOrMixed));
    }

    private sealed class RepairingRevision : ITopologyPublicationRevisionSource
    {
        public List<TopologyReadMode> Modes { get; } = [];

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken) =>
            ReadAsync(TopologyReadMode.ObservedOrMixed, cancellationToken);

        public Task<TopologyPublicationRevision> ReadAsync(TopologyReadMode mode,
            CancellationToken cancellationToken)
        {
            Modes.Add(mode);
            return mode == TopologyReadMode.DeclaredOnly
                ? Task.FromResult(new TopologyPublicationRevision(0, 0))
                : throw new TopologyObservedRepairUnavailableException();
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Production_sql_explain()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var plans = new List<TopologySqlPlan>();
        var observed = new TopologyObservedSnapshotReader(fixture.Storage) { ObserveQuery = plans.Add };
        var revisions = new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage));
        var source = new TopologyGraphSnapshotSource(fixture.Factory, observed,
            new TopologyPublicationFence(revisions)) { ObserveQuery = plans.Add };
        var scope = AccessScope.ForGroups("topology-plan-reader", ["topology-plan-owner"]);
        await fixture.SourceAsync("topology-plan-source", "topology-plan-owner");
        var registry = new TopologyRegistry(fixture.Factory);
        var parentNode = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "plan-parent", "topology-plan-owner", true, []), Ct)).Node!.Id;
        var childNode = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "plan-child", "topology-plan-owner", true, []), Ct)).Node!.Id;
        Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(parentNode, childNode, "depends_on"), Ct)).Status);
        var otherScope = AccessScope.ForGroups("topology-plan-other-reader", ["topology-plan-other-owner"]);
        var otherFrom = (await registry.CreateAsync(otherScope, true,
            new(TopologyNodeKind.Service, "other-parent", "topology-plan-other-owner", true, []), Ct)).Node!.Id;
        var otherTo = (await registry.CreateAsync(otherScope, true,
            new(TopologyNodeKind.Service, "other-child", "topology-plan-other-owner", true, []), Ct)).Node!.Id;
        Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(otherScope, true,
            new(otherFrom, otherTo, "depends_on"), Ct)).Status);
        var parent = Span(Guid.NewGuid(), "1111111111111111", string.Empty, parentNode, fixture.Now);
        var child = Span(Guid.NewGuid(), "2222222222222222", "1111111111111111", childNode,
            fixture.Now + 1000);
        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        var publisher = new TopologyPublicationCoordinator(fixture.Factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        await writer.WriteAsync([parent, child], Ct);
        var otherParent = Span(Guid.NewGuid(), "1111111111111111", string.Empty,
            otherFrom, fixture.Now,
            "11223344556677889900aabbccddeeff", "topology-plan-other-owner", "topology-plan-other-source");
        var otherChild = Span(Guid.NewGuid(), "2222222222222222", "1111111111111111",
            otherTo, fixture.Now + 1000,
            "11223344556677889900aabbccddeeff", "topology-plan-other-owner", "topology-plan-other-source");
        await writer.WriteAsync([otherParent, otherChild], Ct);
        var oldStart = fixture.Now - 2 * (ulong)TopologyExpiry.NanosecondsPerDay;
        var oldParent = Span(Guid.NewGuid(), "1111111111111111", string.Empty, parentNode,
            oldStart, "22334455667788990011aabbccddeeff") with
            { RetentionDays = 90, ObservedRetentionDays = 90 };
        var oldChild = Span(Guid.NewGuid(), "2222222222222222", "1111111111111111", childNode,
            oldStart + 1000, "22334455667788990011aabbccddeeff") with
            { RetentionDays = 90, ObservedRetentionDays = 90 };
        await writer.WriteAsync([oldParent, oldChild], Ct);
        var projected = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        // The fixture clock is fixed while PG registry mutations use their
        // own wall clock. Leave enough headroom for those historical writes
        // and keep the observed pair inside the explicit event-time window.
        var clock = (decimal)fixture.Now + 60_000_000_000m;
        var windowFrom = (decimal)fixture.Now;
        await SeedDeclaredPlanDistractorsAsync(fixture.Factory, otherFrom, otherTo, clock, Ct);
        var observedResult = await new TopologyGraphQueryService(source).SearchEdgesAsync(new(clock,
            Provenance: TopologyProvenance.Observed, FromUnixNano: windowFrom,
            ToUnixNano: clock), scope, Ct);
        Assert.Equal(projected.EdgeId, Assert.Single(observedResult.Items).Id);
        var declaredResult = await new TopologyGraphQueryService(source).SearchEdgesAsync(new(clock,
            Provenance: TopologyProvenance.Declared, FromUnixNano: windowFrom,
            ToUnixNano: clock), scope, Ct);
        Assert.Single(declaredResult.Items);

        var clickhouse = new Dictionary<string, bool>(StringComparer.Ordinal);
        var pruned = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var plan in plans.Where(static plan => plan.Route != "declared-edges"))
        {
            var explain = await (plan.ExplainAsync
                ?? throw new InvalidDataException("Production topology SQL has no bound EXPLAIN replay."))(Ct);
            clickhouse.Add(plan.Route, explain.Contains("ReadFromMergeTree", StringComparison.Ordinal));
            var granules = Regex.Matches(explain, @"Granules: (\d+)/(\d+)")
                .Select(match => (
                    Selected: int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    Total: int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
            pruned.Add(plan.Route, granules.Any(static count => count.Selected > 0
                && count.Selected < count.Total));
        }
        var declared = Assert.Single(plans, static plan => plan.Route == "declared-edges");
        var postgres = await (declared.ExplainAsync
            ?? throw new InvalidDataException("Production declared SQL has no bound EXPLAIN replay."))(Ct);

        // Never persist bound values, plan predicates or hidden row identities.
        TelemetryDbFixture.Evidence("topology-production-explain", new
        {
            Routes = plans.Select(plan => new { plan.Route, plan.BoundParameterNames }),
            ClickHouseReadNodes = clickhouse,
            ClickHousePrunedGranules = pruned,
            PostgreSqlHasPlan = postgres.Length > 0,
        });
        var edges = Assert.Single(plans, static plan => plan.Route == "observed-edges");
        var candidates = Assert.Single(plans, static plan => plan.Route == "observed-candidates");
        Assert.Contains("watermark", edges.BoundParameterNames);
        Assert.Contains("scope_groups", edges.BoundParameterNames);
        Assert.Contains("window_from", edges.BoundParameterNames);
        Assert.Contains("window_to", edges.BoundParameterNames);
        Assert.Contains("scope_groups", candidates.BoundParameterNames);
        Assert.Contains("candidate_limit", candidates.BoundParameterNames);
        Assert.Contains("scope_groups", declared.BoundParameterNames);
        Assert.Contains("state_clock", declared.BoundParameterNames);
        Assert.Contains("read_limit", declared.BoundParameterNames);
        Assert.NotEmpty(clickhouse);
        Assert.True(clickhouse["observed-candidates"]);
        Assert.True(clickhouse["observed-edges"]);
        // A conflict-free corpus legitimately explains the conflicts route
        // as an empty plan when its bound anchors vector is empty. The exact
        // production command is still replayed above; no fake anchor is added.
        Assert.Contains("conflicts", clickhouse.Keys);
        Assert.True(pruned["observed-candidates"]);
        Assert.NotEmpty(postgres);
        Assert.True(postgres.Contains("ix_topology_edge_hist_from_owner_clock", StringComparison.Ordinal)
            || postgres.Contains("ix_topology_edge_hist_to_owner_clock", StringComparison.Ordinal));
        Assert.All(plans.Where(static plan => plan.Route != "declared-edges"),
            plan => Assert.Contains("max_rows_to_read = 131072", plan.Sql, StringComparison.Ordinal));
    }

    private static async Task SeedDeclaredPlanDistractorsAsync(
        IDbContextFactory<ControlPlaneDbContext> factory, string fromNodeId,
        string toNodeId, decimal clock, CancellationToken cancellationToken)
    {
        // Perf-only historical rows in the isolated test database. They are
        // tombstoned and owner B, so removing the production owner predicate
        // would exceed the 4096-row graph capacity before public filtering.
        // The same corpus gives the normal PG optimizer real selectivity for
        // the scoped owner/history indexes; no enable_seqscan override.
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_edges_declared
                (id, from_node_id, to_node_id, relation, from_owner_group, to_owner_group,
                 directed, provenance, confidence, version, deleted_at, created_at,
                 updated_at, created_by, updated_by)
            SELECT md5('v07-plan-' || series.n::text)::uuid, {fromNodeId}, {toNodeId},
                'depends_on', 'topology-plan-other-owner', 'topology-plan-other-owner',
                true, 'declared', 1.00, 1, now(), now(), now(), 'v07-fixture', 'v07-fixture'
            FROM generate_series(1, 5000) AS series(n)
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_edge_declared_history
                (edge_id, from_node_id, to_node_id, relation, from_owner_group,
                 to_owner_group, edge_version, directed, provenance, confidence,
                 deleted_at, from_nano, to_nano)
            SELECT md5('v07-plan-' || series.n::text)::uuid, {fromNodeId}, {toNodeId},
                'depends_on', 'topology-plan-other-owner', 'topology-plan-other-owner',
                1, true, 'declared', 1.00, now(), {clock - 1000m}, NULL
            FROM generate_series(1, 5000) AS series(n)
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ANALYZE bizigo.topology_edge_declared_history", cancellationToken);
    }

    private static TelemetryRecord Span(Guid envelope, string span, string parent, string node, ulong start,
        string trace = "00112233445566778899aabbccddeeff", string owner = "topology-plan-owner",
        string source = "topology-plan-source")
    {
        var raw = JsonDocument.Parse("{\"traceId\":\"" + trace + "\",\"spanId\":\"" + span
            + "\",\"parentSpanId\":\"" + parent + "\",\"startTimeUnixNano\":\"" + start
            + "\",\"endTimeUnixNano\":\"" + (start + 1) + "\",\"name\":\"op\",\"kind\":2}").RootElement.Clone();
        var binding = new TelemetryOwnerBinding(span, source, owner, 7, start, "known");
        var topology = new TopologyLeafBinding(span, start, source, owner, 7,
            node, null, 5, null, 9, "display", "Resolved");
        return new(1, envelope, envelope.ToString("N") + "/" + span, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), binding, start, "op", "svc", "Client", "", 0, false,
            trace, span, 1, new string('c', 64), JsonDocument.Parse("{}").RootElement.Clone(),
            JsonDocument.Parse("{}").RootElement.Clone(), "", "", null, raw)
        { Topology = topology, TopologyBindingsSha256 = new string('d', 64) };
    }
}
