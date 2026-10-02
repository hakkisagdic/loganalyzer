using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyGraphTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AccessScope ScopeA = AccessScope.ForGroups("graph-a", ["A"]);
    private static readonly string R = Node(1), A = Node(2), B = Node(3), C = Node(4), D = Node(5), X = Node(6), Y = Node(7);

    [Fact]
    public async Task Shortest_directed_paths()
    {
        var query = Query(OracleEdges());
        Assert.Equal([R, A, C], (await query.PathAsync(new(R, C, 1000), ScopeA, Ct)).Nodes);
        Assert.Equal([R, A, C, D], (await query.PathAsync(new(R, D, 1000), ScopeA, Ct)).Nodes);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, (await query.PathAsync(new(C, R, 1000), ScopeA, Ct)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, (await query.PathAsync(new(X, D, 1000), ScopeA, Ct)).Status);
    }

    [Fact]
    public async Task Path_pages_complete_hops_with_boundary_node()
    {
        var query = Query(OracleEdges());
        var first = await query.PathAsync(new(R, D, 1000, 1), ScopeA, Ct);
        Assert.Equal([R, A], first.Nodes);
        Assert.Single(first.EdgeIds);
        Assert.NotNull(first.Cursor);
        var second = await query.PathAsync(new(R, D, 1000, 1, first.Cursor), ScopeA, Ct);
        Assert.Equal([A, C], second.Nodes);
        Assert.Single(second.EdgeIds);
        Assert.NotEqual(first.EdgeIds[0], second.EdgeIds[0]);
        Assert.NotNull(second.Cursor);
        var third = await query.PathAsync(new(R, D, 1000, 1, second.Cursor), ScopeA, Ct);
        Assert.Equal([C, D], third.Nodes);
        Assert.Single(third.EdgeIds);
        Assert.Null(third.Cursor);
        Assert.Equal([R, A, C, D], first.Nodes.Concat(second.Nodes.Skip(1)).Concat(third.Nodes.Skip(1)));
        await Assert.ThrowsAsync<TopologyCursorException>(() =>
            query.PathAsync(new(R, D, 1000, 1, first.Cursor![..^1]), ScopeA, Ct));
    }

    [Fact]
    public async Task Common_ancestor_selection()
    {
        var query = Query(OracleEdges());
        var ab = await query.CommonAncestorAsync(new([A, B], 1000), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, ab.Status); Assert.Equal(R, ab.NodeId);
        var cd = await query.CommonAncestorAsync(new([C, D], 1000), ScopeA, Ct);
        Assert.Equal(A, cd.NodeId); Assert.Equal([1, 2], cd.Paths.Select(path => path.EdgeIds.Count).Order().ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.CommonAncestorAsync(new([A], 1000), ScopeA, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => query.CommonAncestorAsync(new([A, A], 1000), ScopeA, Ct));
    }

    [Fact]
    public async Task Cycle_selfloop_and_order()
    {
        var expected = await Query(OracleEdges()).PathAsync(new(R, D, 1000), ScopeA, Ct);
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var shuffled = OracleEdges().OrderBy(_ => random.Next()).ToArray();
            var actual = await Query(shuffled).PathAsync(new(R, D, 1000), ScopeA, Ct);
            Assert.Equal(expected.Nodes, actual.Nodes); Assert.Equal(expected.EdgeIds, actual.EdgeIds);
        }
    }

    [Fact]
    public async Task Hidden_bridge_no_claim()
    {
        var hidden = new[] { Edge("h1", R, B, "A", "B"), Edge("h2", B, C, "B", "A"), Edge("h3", R, B, "A", "B",
            TopologyProvenance.Observed) };
        var query = Query(hidden);
        var path = await query.PathAsync(new(R, C, 1000), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, path.Status); Assert.Empty(path.Nodes); Assert.Empty(path.EdgeIds);
        var neighborhood = await query.NeighborhoodAsync(new(R, 1000), ScopeA, Ct);
        Assert.Empty(neighborhood.Neighbors); Assert.Equal(1, neighborhood.ExternalNeighborCount);
    }

    [Fact]
    public async Task Cursor_is_stable_and_partial_cursor_never_restarts()
    {
        var query = Query([Edge("1", R, A), Edge("2", R, B), Edge("3", R, C)]);
        var first = await query.NeighborhoodAsync(new(R, 1000, 1), ScopeA, Ct);
        Assert.Single(first.Neighbors); Assert.NotNull(first.Cursor);
        var second = await query.NeighborhoodAsync(new(R, 1000, 1, first.Cursor), ScopeA, Ct);
        Assert.Single(second.Neighbors); Assert.NotEqual(first.Neighbors[0].NodeId, second.Neighbors[0].NodeId);
        await Assert.ThrowsAsync<TopologyCursorException>(() =>
            query.NeighborhoodAsync(new(R, 1000, 1, first.Cursor![..^1]), ScopeA, Ct));
        await Assert.ThrowsAsync<TopologyCursorException>(() =>
            query.NeighborhoodAsync(new(R, 1000, 1, first.Cursor, TopologyRelation.Contains), ScopeA, Ct));
    }

    [Fact]
    public async Task Logical_expiry_hides_physical_edge_at_deadline()
    {
        var edge = Edge("ttl", R, A) with { EffectiveExpiry = 500 };
        var service = Query([edge]);
        var before = await service.PathAsync(new(R, A, 499), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, before.Status);
        Assert.Equal(500, before.EarliestEvidenceExpiryUnixNano);
        var edgePage = await service.SearchEdgesAsync(new(499), ScopeA, Ct);
        Assert.Equal(500, edgePage.EarliestEvidenceExpiryUnixNano);
        var neighborhood = await service.NeighborhoodAsync(new(R, 499), ScopeA, Ct);
        Assert.Equal(500, neighborhood.EarliestEvidenceExpiryUnixNano);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, (await Query([edge]).PathAsync(new(R, A, 500), ScopeA, Ct)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, (await Query([edge]).PathAsync(new(R, A, 501), ScopeA, Ct)).Status);
    }

    [Fact]
    public async Task Observed_window_is_half_open_validated_and_bound_to_cursor()
    {
        var observed = new[]
        {
            Edge("1", R, A, provenance: TopologyProvenance.Observed),
            Edge("2", R, B, provenance: TopologyProvenance.Observed),
        };
        var query = Query(observed);
        Assert.Equal(TopologyGraphResultStatus.Found,
            (await query.PathAsync(new(R, A, 2000, FromUnixNano: 1000, ToUnixNano: 1001), ScopeA, Ct)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await query.PathAsync(new(R, A, 2000, FromUnixNano: 0, ToUnixNano: 1000), ScopeA, Ct)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            query.PathAsync(new(R, A, 2000, FromUnixNano: 0), ScopeA, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            query.PathAsync(new(R, A, 2000, FromUnixNano: 1000, ToUnixNano: 2001), ScopeA, Ct));

        var first = await query.NeighborhoodAsync(new(R, 2000, 1, FromUnixNano: 1000, ToUnixNano: 1001), ScopeA, Ct);
        Assert.NotNull(first.Cursor);
        await Assert.ThrowsAsync<TopologyCursorException>(() => query.NeighborhoodAsync(
            new(R, 2000, 1, first.Cursor, FromUnixNano: 999, ToUnixNano: 1001), ScopeA, Ct));
    }

    [Fact]
    public async Task Cursor_expiry_includes_later_visible_pages_but_excludes_hidden_and_filtered_edges()
    {
        var edges = new[]
        {
            Edge("a", R, A, provenance: TopologyProvenance.Observed) with { EffectiveExpiry = 1100 },
            Edge("b", R, B, provenance: TopologyProvenance.Observed) with { EffectiveExpiry = 1050 },
            Edge("hidden", R, C, "A", "B", TopologyProvenance.Observed) with { EffectiveExpiry = 1020 },
            Edge("filtered", R, D, provenance: TopologyProvenance.Observed) with
            { EffectiveExpiry = 1030, Relation = TopologyRelation.Contains },
        };
        var query = Query(edges);
        var first = await query.SearchEdgesAsync(new(1001, 1, Provenance: TopologyProvenance.Observed,
            Relation: TopologyRelation.DependsOn), ScopeA, Ct);
        Assert.Equal("a", Assert.Single(first.Items).Id);
        Assert.Equal(1050, first.EarliestEvidenceExpiryUnixNano);
        Assert.NotNull(first.Cursor);
        var second = await query.SearchEdgesAsync(new(1001, 1, first.Cursor,
            TopologyRelation.DependsOn, TopologyProvenance.Observed), ScopeA, Ct);
        Assert.Equal("b", Assert.Single(second.Items).Id);
        Assert.Equal(1050, second.EarliestEvidenceExpiryUnixNano);

        var neighborhood = await query.NeighborhoodAsync(new(R, 1001, 1,
            Relation: TopologyRelation.DependsOn), ScopeA, Ct);
        Assert.Equal(A, Assert.Single(neighborhood.Neighbors).NodeId);
        Assert.Equal(1050, neighborhood.EarliestEvidenceExpiryUnixNano);
    }

    [Fact]
    public async Task Mapped_unassigned_owner_does_not_authorize_topology_reads()
    {
        var scope = AccessScope.ForGroups("idp-mapped", ["A", OwnerGroups.Unassigned]);
        var unassigned = Node(8);
        var edge = Edge("unknown", R, unassigned, "A", OwnerGroups.Unassigned);
        var snapshot = new TopologyGraphSnapshot(9, [edge])
        {
            Nodes =
            [
                new(R, TopologyNodeKind.Service, "root", "A", true, false, 1, 0, null),
                new(unassigned, TopologyNodeKind.Service, "unknown", OwnerGroups.Unassigned, true, false, 1, 0, null),
            ],
        };
        var query = new TopologyGraphQueryService(new MemorySource(snapshot));
        Assert.Equal([R], (await query.SearchNodesAsync(new(1001), scope, Ct)).Items.Select(static node => node.Id));
        Assert.Null(await query.GetNodeAsync(unassigned, 1001, scope, Ct));
        Assert.Empty((await query.SearchEdgesAsync(new(1001), scope, Ct)).Items);
        Assert.Null(await query.GetEdgeAsync(edge.Id, 1001, scope, Ct));
        Assert.Empty((await query.NeighborhoodAsync(new(R, 1001), scope, Ct)).Neighbors);
        Assert.Equal(TopologyGraphResultStatus.NotVerified,
            (await query.PathAsync(new(R, unassigned, 1001), scope, Ct)).Status);
        Assert.True(TopologyIdentity.CanReadEdge(AccessScope.System("admin"), "A", OwnerGroups.Unassigned));
    }

    [Fact]
    public async Task Edge_evidence_detail_pages_without_silent_truncation()
    {
        var edge = Edge("proof", R, A);
        var evidence = Enumerable.Range(1, 201)
            .Select(index => new TopologyEvidenceReference(edge.Id, $"occ-{index:D3}", "trace", "span", index))
            .ToArray();
        var query = new TopologyGraphQueryService(new MemorySource(new(9, [edge]) { Evidence = evidence }));
        var first = await query.GetEdgeAsync(edge.Id, 1000, ScopeA, null, 200, Ct);
        Assert.NotNull(first);
        Assert.Equal(200, first.Evidence.Count);
        Assert.NotNull(first.EvidenceCursor);
        var second = await query.GetEdgeAsync(edge.Id, 1000, ScopeA, first.EvidenceCursor, 200, Ct);
        Assert.NotNull(second);
        Assert.Equal("occ-201", Assert.Single(second.Evidence).Id);
        Assert.Null(second.EvidenceCursor);
        await Assert.ThrowsAsync<TopologyCursorException>(() =>
            query.GetEdgeAsync(edge.Id, 1000, AccessScope.ForGroups("other", ["A"]), first.EvidenceCursor, 200, Ct));
    }

    private static TopologyGraphQueryService Query(IReadOnlyList<TopologyEdgeProjection> edges) =>
        new(new MemorySource(new(9, edges)));

    private static TopologyEdgeProjection[] OracleEdges() =>
    [
        Edge("ra", R, A), Edge("rb", R, B), Edge("ac", A, C), Edge("bc", B, C), Edge("cd", C, D),
        Edge("da", D, A), Edge("xy", X, Y), Edge("cc", C, C),
    ];

    private static TopologyEdgeProjection Edge(string id, string from, string to, string fromOwner = "A", string toOwner = "A",
        TopologyProvenance provenance = TopologyProvenance.Declared) => new(id, from, to, TopologyRelation.DependsOn,
        provenance, true, provenance == TopologyProvenance.Declared ? 1 : 0.5m, fromOwner, toOwner,
        fromOwner == toOwner ? TopologyEdgeVisibility.SameOwner : TopologyEdgeVisibility.CrossOwner,
        0, 1000, null, 9, 1, false);

    private static string Node(int value) => TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

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
}
