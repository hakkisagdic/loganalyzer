using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyGroupedAncestorTests
{
    private static readonly AccessScope Scope = AccessScope.ForGroups("grouped-reader", ["A"]);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_target_in_two_source_groups_has_two_strict_positive_witnesses()
    {
        var root = Node(TopologyNodeKind.Source, 1);
        var target = Node(TopologyNodeKind.Service, 2);
        var graph = Graph([Depends("root-target", root, target)]);
        var result = await graph.GroupedCommonAncestorAsync(new([[target], [target]], 1500), Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
        Assert.Equal(root, result.NodeId);
        Assert.Equal(2, result.Paths.Count);
        Assert.All(result.Paths, path =>
        {
            Assert.Equal(target, path.TargetNodeId);
            Assert.Equal(new[] { "root-target" }, path.EdgeIds);
        });
    }

    [Fact]
    public async Task Group_minimum_positive_distances_rank_by_max_then_sum_then_identity()
    {
        var rootA = Node(TopologyNodeKind.Source, 1);
        var rootB = Node(TopologyNodeKind.Source, 2);
        var aMid = Node(TopologyNodeKind.Service, 3);
        var bFirstMid = Node(TopologyNodeKind.Service, 4);
        var bSecondMid = Node(TopologyNodeKind.Service, 5);
        var first = Node(TopologyNodeKind.Service, 6);
        var second = Node(TopologyNodeKind.Service, 7);
        var graph = Graph([Depends("a-first", rootA, first), Depends("a-mid", rootA, aMid),
            Depends("mid-second", aMid, second), Depends("b-first-mid", rootB, bFirstMid),
            Depends("b-second-mid", rootB, bSecondMid), Depends("b-mid-first", bFirstMid, first),
            Depends("b-mid-second", bSecondMid, second)]);
        var result = await graph.GroupedCommonAncestorAsync(new([[first], [second]], 1500), Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
        Assert.Equal(rootA, result.NodeId); // max=2, sum=3 versus B max=2, sum=4
        Assert.Equal(new[] { "a-first", "a-mid", "mid-second" },
            result.Paths.SelectMany(static path => path.EdgeIds));
    }

    private static TopologyGraphQueryService Graph(IReadOnlyList<TopologyEdgeProjection> edges) =>
        new(new MemorySource(new TopologyGraphSnapshot(9, edges)));

    private static TopologyEdgeProjection Depends(string id, string from, string to) =>
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Declared, true, 1,
            "A", "A", TopologyEdgeVisibility.SameOwner, 1000, 1000, null, 9, 1, false);

    private static string Node(TopologyNodeKind kind, int number) => TopologyIdentity.Node(kind,
        Guid.Parse($"00000000-0000-0000-0000-{number:D12}"));

    private sealed class MemorySource(TopologyGraphSnapshot snapshot) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
