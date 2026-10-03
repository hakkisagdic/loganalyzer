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
