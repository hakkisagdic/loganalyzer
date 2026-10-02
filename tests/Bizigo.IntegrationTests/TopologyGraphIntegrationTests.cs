using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.IntegrationTests;

/// <summary>
/// The scoped topology read contract against a single committed snapshot. The
/// concrete PostgreSQL/ClickHouse snapshot source can replace this source
/// without changing any public read or visibility semantics.
/// </summary>
public sealed class TopologyGraphIntegrationTests
{
    private static readonly AccessScope ScopeA = AccessScope.ForGroups("topology-integration", ["A"]);
    private static readonly string A = Node(1), B = Node(2), Hidden = Node(3);

    // kapsam: SearchTopologyNodesAsync
    // kapsam: GetTopologyNodeAsync
    // kapsam: SearchTopologyEdgesAsync
    // kapsam: GetTopologyEdgeAsync
    // kapsam: GetTopologyNeighborhoodAsync
    // kapsam: GetTopologyPathAsync
    // kapsam: GetTopologyCommonAncestorAsync
    // kapsam: CountExternalTopologyNeighborsAsync
    // kapsam: ResolveTopologySourceNodesAsync
    [Fact]
    [Trait("Category", "Integration")]
    public async Task All_reads_hide_foreign_content_and_report_only_distinct_boundary_count()
    {
        var visible = Edge("visible", A, B, "A", "A");
        var crossing1 = Edge("cross-1", A, Hidden, "A", "B");
        var crossing2 = Edge("cross-2", A, Hidden, "A", "B") with { Provenance = TopologyProvenance.Observed };
        var hidden = Edge("hidden", Hidden, Node(4), "B", "A");
        var nodes = new[]
        {
            Projection(A, "A"), Projection(B, "A"), Projection(Hidden, "B"), Projection(Node(4), "A"),
        };
        var service = new TopologyGraphQueryService(new MemorySource(new(17, [visible, crossing1, crossing2, hidden])
        {
            Nodes = nodes,
            Evidence = [new("hidden", "proof-secret", "trace-secret", "span-secret", 10)],
        }));

        var nodePage = await service.SearchNodesAsync(new(100), ScopeA, TestContext.Current.CancellationToken);
        Assert.Equal([A, B, Node(4)], nodePage.Items.Select(static node => node.Id));
        Assert.Null(await service.GetNodeAsync(Hidden, 100, ScopeA, TestContext.Current.CancellationToken));

        var edgePage = await service.SearchEdgesAsync(new(100), ScopeA, TestContext.Current.CancellationToken);
        Assert.Equal(["visible"], edgePage.Items.Select(static edge => edge.Id));
        Assert.Null(await service.GetEdgeAsync("hidden", 100, ScopeA, TestContext.Current.CancellationToken));

        var neighborhood = await service.NeighborhoodAsync(new(A, 100), ScopeA, TestContext.Current.CancellationToken);
        Assert.Equal([B], neighborhood.Neighbors.Select(static item => item.NodeId));
        Assert.Equal(1, neighborhood.ExternalNeighborCount);

        var path = await service.PathAsync(new(A, Node(4), 100), ScopeA, TestContext.Current.CancellationToken);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, path.Status);
        Assert.Empty(path.Nodes);
        Assert.Empty(path.EdgeIds);

        var ancestor = await service.CommonAncestorAsync(new([B, Node(4)], 100), ScopeA,
            TestContext.Current.CancellationToken);
        Assert.Equal(TopologyGraphResultStatus.NotVerified, ancestor.Status);
        Assert.Null(ancestor.NodeId);
        Assert.Empty(ancestor.Paths);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Cursor_binding_and_revision_rejects_partial_filter_scope_and_snapshot_changes()
    {
        var source = new MemorySource(new(17, [Edge("ab", A, B, "A", "A")])
        {
            Nodes = [Projection(A, "A"), Projection(B, "A")],
        });
        var service = new TopologyGraphQueryService(source);
        var first = await service.SearchNodesAsync(new(100, 1), ScopeA, TestContext.Current.CancellationToken);
        Assert.NotNull(first.Cursor);

        await Assert.ThrowsAsync<TopologyCursorException>(() => service.SearchNodesAsync(
            new(100, 1, first.Cursor![..^1]), ScopeA, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TopologyCursorException>(() => service.SearchNodesAsync(
            new(101, 1, first.Cursor), ScopeA, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TopologyCursorException>(() => service.SearchNodesAsync(
            new(100, 1, first.Cursor), AccessScope.ForGroups("other", ["A"]), TestContext.Current.CancellationToken));

        source.Snapshot = source.Snapshot with { PublishedSequence = 18 };
        await Assert.ThrowsAsync<TopologySnapshotUnavailableException>(() => service.SearchNodesAsync(
            new(100, 1, first.Cursor), ScopeA, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Logical_expiration_applies_while_physical_edge_remains_in_snapshot()
    {
        var source = new MemorySource(new(1, [Edge("ttl", A, B, "A", "A") with { EffectiveExpiry = 500 }]));
        var service = new TopologyGraphQueryService(source);
        Assert.Equal(TopologyGraphResultStatus.Found,
            (await service.PathAsync(new(A, B, 499), ScopeA, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(TopologyGraphResultStatus.Unreachable,
            (await service.PathAsync(new(A, B, 500), ScopeA, TestContext.Current.CancellationToken)).Status);
        Assert.Single(source.Snapshot.Edges);
    }

    private static TopologyNodeProjection Projection(string id, string owner) =>
        new(id, TopologyNodeKind.Service, id, owner, true, false, 1, 0, null);

    private static TopologyEdgeProjection Edge(string id, string from, string to, string fromOwner, string toOwner) =>
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Declared, true, 1, fromOwner, toOwner,
            fromOwner == toOwner ? TopologyEdgeVisibility.SameOwner : TopologyEdgeVisibility.CrossOwner,
            0, 1000, null, 1, 1, false);

    private static string Node(int value) => TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private sealed class MemorySource(TopologyGraphSnapshot snapshot) : ITopologyGraphSnapshotSource
    {
        public TopologyGraphSnapshot Snapshot { get; set; } = snapshot;

        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publishedSequence is not null && publishedSequence != Snapshot.PublishedSequence)
                throw new TopologySnapshotUnavailableException(publishedSequence.Value);
            return Task.FromResult(Snapshot);
        }
    }
}
