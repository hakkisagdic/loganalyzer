using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>Physical cross-owner projection and public scope/count matrix on real PG/CH.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyFinalScopeOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Q02_Observed_edge_list_detail_neighborhood_path_ancestor_and_evidence_need_both_owners()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var physical = await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE from_node_id = '"
            + seed.Parent + "' AND to_node_id = '" + seed.Child + "'");
        Assert.Equal("1", physical.Trim()); // no owner-duplicated edge row
        var visible = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.ScopeAB, Ct);
        var edge = Assert.Single(visible.Items);
        Assert.Equal(seed.Parent, edge.FromNode);
        Assert.Equal(seed.Child, edge.ToNode);
        Assert.Equal("A", edge.FromOwnerGroup);
        Assert.Equal("B", edge.ToOwnerGroup);
        Assert.Equal(TopologyEdgeVisibility.CrossOwner, edge.Visibility);
        var detail = await seed.Query.GetTopologyEdgeAsync(edge.Id, seed.ReadClock, seed.ScopeAB, Ct);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Evidence.Count);
        Assert.NotNull(await seed.Query.GetTopologyNodeAsync(seed.Parent, seed.ReadClock, seed.ScopeA, Ct));
        Assert.Null(await seed.Query.GetTopologyNodeAsync(seed.Child, seed.ReadClock, seed.ScopeA, Ct));
        Assert.NotNull(await seed.Query.GetTopologyNodeAsync(seed.Child, seed.ReadClock, seed.ScopeB, Ct));
        Assert.Null(await seed.Query.GetTopologyNodeAsync(seed.Parent, seed.ReadClock, seed.ScopeB, Ct));
        Assert.Contains((await seed.Query.SearchTopologyNodesAsync(new(seed.ReadClock), seed.ScopeAB, Ct)).Items,
            node => node.Id == seed.Parent);
        Assert.Contains((await seed.Query.SearchTopologyNodesAsync(new(seed.ReadClock), seed.ScopeAB, Ct)).Items,
            node => node.Id == seed.Child);
        Assert.Equal(TopologyGraphResultStatus.Found, (await seed.Query.GetTopologyPathAsync(
            new(seed.Parent, seed.Child, seed.ReadClock), seed.ScopeAB, Ct)).Status);
        var ancestorAB = await seed.Query.GetTopologyCommonAncestorAsync(new(
            [seed.Parent, seed.Child], seed.ReadClock), seed.ScopeAB, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, ancestorAB.Status);
        Assert.Equal(seed.Root, ancestorAB.NodeId);
        Assert.Equal(new[] { seed.Parent, seed.Child }.Order(StringComparer.Ordinal),
            ancestorAB.Paths.Select(path => path.TargetNodeId).Order(StringComparer.Ordinal));
        Assert.All(ancestorAB.Paths, proof =>
        {
            Assert.Equal(seed.Root, proof.Nodes[0]);
            Assert.Equal(proof.TargetNodeId, proof.Nodes[^1]);
            Assert.Equal(proof.Nodes.Count - 1, proof.EdgeIds.Count);
        });

        foreach (var scope in new[] { seed.ScopeA, seed.ScopeB, seed.ScopeC })
        {
            Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
                Provenance: TopologyProvenance.Observed), scope, Ct)).Items);
            Assert.Null(await seed.Query.GetTopologyEdgeAsync(edge.Id, seed.ReadClock, scope, Ct));
            var path = await seed.Query.GetTopologyPathAsync(new(seed.Parent, seed.Child,
                seed.ReadClock), scope, Ct);
            Assert.Empty(path.EdgeIds);
            Assert.NotEqual(TopologyGraphResultStatus.Found, path.Status);
            var ancestor = await seed.Query.GetTopologyCommonAncestorAsync(new(
                [seed.Parent, seed.Child], seed.ReadClock), scope, Ct);
            Assert.Empty(ancestor.Paths);
            Assert.NotEqual(TopologyGraphResultStatus.Found, ancestor.Status);
        }
        Assert.DoesNotContain((await seed.Query.GetTopologyNeighborhoodAsync(new(seed.Parent, seed.ReadClock),
            seed.ScopeA, Ct)).Neighbors, n => n.EdgeId == edge.Id);
        Assert.DoesNotContain((await seed.Query.GetTopologyNeighborhoodAsync(new(seed.Child, seed.ReadClock),
            seed.ScopeB, Ct)).Neighbors, n => n.EdgeId == edge.Id);
        Assert.Contains((await seed.Query.GetTopologyNeighborhoodAsync(new(seed.Parent, seed.ReadClock),
            seed.ScopeAB, Ct)).Neighbors, n => n.EdgeId == edge.Id);

        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        var absent = "service:" + Guid.NewGuid().ToString("D");
        foreach (var owner in new[] { "A", "B" })
        {
            using var hidden = await api.GetAsync("/v1/topology/edges/" + edge.Id, owner);
            using var unknown = await api.GetAsync("/v1/topology/edges/" + absent, owner);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            Assert.Equal(await unknown.Content.ReadAsStringAsync(Ct),
                await hidden.Content.ReadAsStringAsync(Ct));
            using var list = await api.GetAsync("/v1/topology/edges?provenance=observed", owner);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var body = await list.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain(edge.Id, body, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.TraceId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(owner == "A" ? seed.Child : seed.Parent, body, StringComparison.Ordinal);
            var hiddenNode = owner == "A" ? seed.Child : seed.Parent;
            var visibleNode = owner == "A" ? seed.Parent : seed.Child;
            using var hiddenPath = await api.GetAsync("/v1/topology/path?fromNode=" + visibleNode
                + "&toNode=" + hiddenNode, owner);
            using var unknownPath = await api.GetAsync("/v1/topology/path?fromNode=" + visibleNode
                + "&toNode=" + absent, owner);
            Assert.Equal(HttpStatusCode.NotFound, hiddenPath.StatusCode);
            Assert.Equal(await unknownPath.Content.ReadAsStringAsync(Ct),
                await hiddenPath.Content.ReadAsStringAsync(Ct));
            using var hiddenAncestor = await api.GetAsync("/v1/topology/ancestors?nodeId=" + visibleNode
                + "&nodeId=" + hiddenNode, owner);
            using var unknownAncestor = await api.GetAsync("/v1/topology/ancestors?nodeId=" + visibleNode
                + "&nodeId=" + absent, owner);
            Assert.Equal(HttpStatusCode.NotFound, hiddenAncestor.StatusCode);
            Assert.Equal(await unknownAncestor.Content.ReadAsStringAsync(Ct),
                await hiddenAncestor.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Q03_Distinct_hidden_neighbor_count_is_one_then_zero_outside_window()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture, declaredDependency: false);
        var edges = new TopologyEdgeRegistry(fixture.Factory);
        Assert.Equal(201, (await edges.CreateAsync(seed.ScopeAB, true,
            new(seed.Parent, seed.Child, "connects_to"), Ct)).Status);
        // Same hidden node B is incident through declared connects_to and
        // observed depends_on, but counts only once across provenance/relation.
        var countA = await seed.Query.CountExternalTopologyNeighborsAsync(
            new(seed.Parent, seed.ReadClock), seed.ScopeA, Ct);
        var countB = await seed.Query.CountExternalTopologyNeighborsAsync(
            new(seed.Child, seed.ReadClock), seed.ScopeB, Ct);
        Assert.Equal(1, countA.Count);
        Assert.Equal(1, countB.Count);
        Assert.Null(countA.Reason);
        Assert.Equal(0, (await seed.Query.CountExternalTopologyNeighborsAsync(
            new(seed.Parent, seed.ReadClock), seed.ScopeAB, Ct)).Count);
        Assert.Equal(0, (await seed.Query.CountExternalTopologyNeighborsAsync(
            new(seed.Parent, seed.ReadClock, Relation: TopologyRelation.DependsOn,
                FromUnixNano: seed.ReadClock - 10 * TopologyExpiry.NanosecondsPerDay,
                ToUnixNano: seed.ReadClock - 9 * TopologyExpiry.NanosecondsPerDay), seed.ScopeA, Ct)).Count);

        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        using var visible = await api.GetAsync("/v1/topology/nodes/" + seed.Parent + "/neighbors", "A");
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        using var body = JsonDocument.Parse(await visible.Content.ReadAsStringAsync(Ct));
        Assert.Equal("1", body.RootElement.GetProperty("outside_neighbor_count").GetString());
        using var hiddenAnchor = await api.GetAsync("/v1/topology/nodes/" + seed.Child + "/neighbors", "A");
        using var unknownAnchor = await api.GetAsync("/v1/topology/nodes/service:"
            + Guid.NewGuid().ToString("D") + "/neighbors", "A");
        Assert.Equal(HttpStatusCode.NotFound, hiddenAnchor.StatusCode);
        Assert.Equal(await unknownAnchor.Content.ReadAsStringAsync(Ct),
            await hiddenAnchor.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Q03_Real_observed_store_failure_never_reports_measured_zero()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture, declaredDependency: false);
        var backup = "topology_observed_fault_" + Guid.NewGuid().ToString("N")[..12];
        await fixture.SqlAsync("RENAME TABLE topology_edges_observed TO " + backup);
        try
        {
            var count = await seed.Query.CountExternalTopologyNeighborsAsync(
                new(seed.Parent, seed.ReadClock), seed.ScopeA, Ct);
            Assert.Null(count.Count);
            Assert.Equal("QueryUnavailable", count.Reason);
        }
        finally
        {
            await fixture.SqlAsync("RENAME TABLE " + backup + " TO topology_edges_observed");
        }
    }

    private sealed record Seed(IScopedQuery Query, AccessScope ScopeA, AccessScope ScopeB,
        AccessScope ScopeAB, AccessScope ScopeC, string Root, string Parent, string Child, string TraceId,
        decimal ReadClock);

    private static async Task<Seed> SeedAsync(TelemetryDbFixture fixture, bool declaredDependency = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "scope-a-" + suffix;
        var sourceB = "scope-b-" + suffix;
        var scopeA = AccessScope.ForGroups("scope-A-" + suffix, ["A"]);
        var scopeB = AccessScope.ForGroups("scope-B-" + suffix, ["B"]);
        var scopeAB = AccessScope.ForGroups("scope-AB-" + suffix, ["A", "B"]);
        var scopeC = AccessScope.ForGroups("scope-C-" + suffix, ["C"]);
        await fixture.SourceAsync(sourceA, "A");
        await fixture.SourceAsync(sourceB, "B");
        var registry = new TopologyRegistry(fixture.Factory);
        var parent = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "scope-parent-" + suffix, "A", true,
                [new(sourceA, "n", "scope-parent")]), Ct)).Node!.Id;
        var child = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "scope-child-" + suffix, "B", true,
                [new(sourceB, "n", "scope-child")]), Ct)).Node!.Id;
        var root = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "scope-root-" + suffix, "A", true, []), Ct)).Node!.Id;
        Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scopeAB, true,
            new(root, parent, "depends_on"), Ct)).Status);
        if (declaredDependency)
            Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scopeAB, true,
                new(parent, child, "depends_on"), Ct)).Status);

        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        var publisher = new TopologyPublicationCoordinator(fixture.Factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        var trace = Guid.NewGuid().ToString("N");
        var start = fixture.Now;
        await writer.WriteAsync([
            Span(Guid.NewGuid(), trace, "1111111111111111", "", "a", sourceA, "A", parent, start),
            Span(Guid.NewGuid(), trace, "2222222222222222", "1111111111111111", "b",
                sourceB, "B", child, start + 1000),
        ], Ct);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            topologyClock: new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(10)));
        return new(query, scopeA, scopeB, scopeAB, scopeC, root, parent, child, trace,
            TopologyIdentity.Nano(DateTimeOffset.UtcNow.AddSeconds(5)));
    }

    private static TelemetryRecord Span(Guid envelope, string trace, string span, string parentSpan, string leaf,
        string source, string owner, string node, ulong start)
    {
        var raw = JsonDocument.Parse("{\"traceId\":\"" + trace + "\",\"spanId\":\"" + span
            + "\",\"parentSpanId\":\"" + parentSpan + "\",\"startTimeUnixNano\":\"" + start
            + "\",\"endTimeUnixNano\":\"" + (start + 1) + "\",\"name\":\"op\",\"kind\":2}").RootElement.Clone();
        var binding = new TelemetryOwnerBinding(leaf, source, owner, 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, source, owner, 7,
            node, null, 5, null, 9, "display", "Resolved");
        return new(1, envelope, envelope.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), binding, start, "op", "svc", "Client", "", 0, false,
            trace, span, 1, new string('c', 64), JsonDocument.Parse("{}").RootElement.Clone(),
            JsonDocument.Parse("{}").RootElement.Clone(), "", "", null, raw)
        { Topology = topology, TopologyBindingsSha256 = new string('d', 64) };
    }
}
