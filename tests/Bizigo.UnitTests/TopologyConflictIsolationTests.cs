using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyConflictIsolationTests
{
    private static readonly AccessScope ScopeA = AccessScope.ForGroups("reader-a", ["A"]);
    private static readonly AccessScope ScopeB = AccessScope.ForGroups("reader-b", ["B"]);
    private static readonly string A1 = Node(1), A2 = Node(2), B1 = Node(3), B2 = Node(4);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthy_A_list_detail_path_unchanged_by_B_conflict()
    {
        var graph = Graph();
        Assert.Equal("healthy", Assert.Single((await graph.SearchEdgesAsync(new(2000), ScopeA, Ct)).Items).Id);
        Assert.NotNull(await graph.GetEdgeAsync("healthy", 2000, ScopeA, Ct));
        Assert.Equal(TopologyGraphResultStatus.Found, (await graph.PathAsync(new(A1, A2, 2000), ScopeA, Ct)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await graph.PathAsync(new(A2, A1, 2000), ScopeA, Ct)).Status);
        Assert.Equal("healthy", Assert.Single((await graph.NeighborhoodAsync(new(A1, 2000), ScopeA, Ct)).Neighbors).EdgeId);
    }

    [Fact]
    public async Task Related_B_conflict_fails_closed_without_published_proof()
    {
        var graph = Graph();
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.SearchEdgesAsync(new(2000), ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.GetEdgeAsync("old-b", 2000, ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.PathAsync(new(B1, B2, 2000), ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.NeighborhoodAsync(new(B1, 2000), ScopeB, Ct));
        Assert.Empty((await graph.SearchEdgesAsync(new(2000, Provenance: TopologyProvenance.Declared), ScopeB, Ct)).Items);
    }

    [Fact]
    public async Task Orphan_and_outside_window_conflicts_are_route_local()
    {
        var orphan = new TopologyGraphSnapshot(9, [Edge("healthy", A1, A2, "A")])
        {
            ConflictCandidates = [new("B", B1, 1500, 3000)],
        };
        var graph = new TopologyGraphQueryService(new MemorySource(orphan));
        Assert.Single((await graph.SearchEdgesAsync(new(2000), ScopeA, Ct)).Items);
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.SearchEdgesAsync(new(2000), ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.NeighborhoodAsync(new(B1, 2000), ScopeB, Ct));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.PathAsync(new(B1, B2, 2000), ScopeB, Ct));
        Assert.Empty((await graph.SearchEdgesAsync(new(2000, FromUnixNano: 1000, ToUnixNano: 1500), ScopeB, Ct)).Items);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await graph.PathAsync(new(B1, B2, 2000, FromUnixNano: 1000, ToUnixNano: 1500), ScopeB, Ct)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await graph.PathAsync(new(B2, A2, 2000), ScopeB, Ct)).Status);
    }

    [Fact]
    public async Task Legacy_unattributed_marker_requires_migration_only_for_observed_routes()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9,
            [Edge("declared", A1, A2, "A", TopologyProvenance.Declared)])
        { ObservedMigrationRequired = true }));
        Assert.Equal("declared", Assert.Single((await graph.SearchEdgesAsync(
            new(2000, Provenance: TopologyProvenance.Declared), ScopeA, Ct)).Items).Id);
        Assert.NotNull(await graph.GetEdgeAsync("declared", 2000, ScopeA, Ct));
        await Assert.ThrowsAsync<TopologyObservedMigrationRequiredException>(() => graph.SearchEdgesAsync(new(2000), ScopeA, Ct));
        await Assert.ThrowsAsync<TopologyObservedMigrationRequiredException>(() => graph.PathAsync(new(A1, A2, 2000), ScopeA, Ct));
    }

    [Fact]
    public async Task First_publication_child_conflict_blocks_parent_neighborhood_and_count()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9, [])
        {
            ConflictCandidates = [new("A", A1, 1000, 5000)],
            ConflictArcs = [new(A1, A2, "A", "A", 2000, 5000)],
        }));
        await Assert.ThrowsAsync<TopologyConflictException>(() => graph.NeighborhoodAsync(new(A1, 3000,
            FromUnixNano: 1500, ToUnixNano: 2500), ScopeA, Ct));
        var count = await graph.CountExternalNeighborsAsync(new(A1, 3000,
            FromUnixNano: 1500, ToUnixNano: 2500), ScopeA, Ct);
        Assert.Null(count.Count);
        Assert.Equal("QueryUnavailable", count.Reason);
    }

    [Fact]
    public async Task Directed_path_ignores_weakly_connected_conflict_branch()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9,
            [Edge("x-s", A1, A2, "A"), Edge("s-t", A2, B1, "A"), Edge("x-c", A1, B2, "A")])
        {
            ConflictCandidates = [new("A", B2, 1500, 3000)],
        }));
        var path = await graph.PathAsync(new(A2, B1, 2000), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, path.Status);
        Assert.Equal(new[] { "s-t" }, path.EdgeIds);
    }

    [Fact]
    public async Task Directed_path_ignores_forward_conflict_branch_that_cannot_reach_target()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9,
            [Edge("s-t", A1, A2, "A"), Edge("s-c", A1, B1, "A")])
        {
            ConflictArcs = [new(B1, B2, "A", "A", 1500, 3000)],
            ConflictCandidates = [new("A", B1, 1500, 3000)],
        }));
        var path = await graph.PathAsync(new(A1, A2, 2000), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, path.Status);
        Assert.Equal(new[] { "s-t" }, path.EdgeIds);
    }

    [Fact]
    public async Task Known_negative_binding_cannot_fail_unrelated_same_owner_path()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9,
            [Edge("healthy", A1, A2, "A")])
        {
            ConflictCandidates = [new("A", null, 1500, 3000)
                { SourceId = "unbound-U", ResolutionReason = "Unresolved" }],
        }));
        Assert.Equal(TopologyGraphResultStatus.Found,
            (await graph.PathAsync(new(A1, A2, 2000), ScopeA, Ct)).Status);
        Assert.Single((await graph.SearchEdgesAsync(new(2000), ScopeA, Ct)).Items);
    }

    [Fact]
    public async Task Missing_parent_is_scoped_not_verified_not_a_measured_zero()
    {
        var graph = new TopologyGraphQueryService(new MemorySource(new TopologyGraphSnapshot(9, [])
        {
            UnresolvedParents = [new("A", "source-A", A2, "MissingParent", 1500, 3000)],
        }));
        var neighborhood = await graph.NeighborhoodAsync(new(A2, 2000), ScopeA, Ct);
        Assert.Null(neighborhood.ExternalNeighborCount);
        Assert.Equal("MissingParent", neighborhood.ExternalNeighborReason);
        var path = await graph.PathAsync(new(A1, A2, 2000), ScopeA, Ct);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, path.Status);
        Assert.Equal("MissingParent", path.Reason);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await graph.PathAsync(new(A1, A2, 2000), ScopeB, Ct)).Status);
    }

    private static TopologyGraphQueryService Graph() => new(new MemorySource(new TopologyGraphSnapshot(9,
        [Edge("healthy", A1, A2, "A")])
    {
        ConflictedEdges = [Edge("old-b", B1, B2, "B")],
        ConflictCandidates = [new("B", B1, 1500, 3000), new("B", B2, 1500, 3000)],
    }));

    private static TopologyEdgeProjection Edge(string id, string from, string to, string owner,
        TopologyProvenance provenance = TopologyProvenance.Observed) =>
        new(id, from, to, TopologyRelation.DependsOn, provenance, true, 0.5m,
            owner, owner, TopologyEdgeVisibility.SameOwner, 1000, 1500, 3000, 9, 1, false);

    private static string Node(int number) => TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse($"00000000-0000-0000-0000-{number:D12}"));

    private sealed class MemorySource(TopologyGraphSnapshot snapshot) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
