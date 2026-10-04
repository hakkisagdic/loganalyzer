using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyAncestorScoringTests
{
    private static readonly AccessScope Scope = AccessScope.ForGroups("ancestor-scoring", ["A"]);
    private static readonly string First = Node(20), Second = Node(21);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ancestor_scoring_prefers_lower_max_hops_over_ordinal_id()
    {
        var shorterMaximum = Node(2);
        var ordinalFirst = Node(1);
        var result = await Query([
            Edge("a1", shorterMaximum, Node(30)), Edge("a2", Node(30), First),
            Edge("a3", shorterMaximum, Node(31)), Edge("a4", Node(31), Second),
            Edge("b1", ordinalFirst, First), Edge("b2", ordinalFirst, Node(32)),
            Edge("b3", Node(32), Node(33)), Edge("b4", Node(33), Second),
        ]).CommonAncestorAsync(new([First, Second], 1000), Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
        Assert.Equal(shorterMaximum, result.NodeId);
        Assert.Equal([2, 2], result.Paths.Select(static path => path.EdgeIds.Count).ToArray());
    }

    [Fact]
    public async Task Ancestor_scoring_prefers_lower_total_hops_when_maximum_ties()
    {
        var ordinalFirst = Node(1);
        var shorterTotal = Node(2);
        var result = await Query([
            Edge("a1", ordinalFirst, Node(30)), Edge("a2", Node(30), First),
            Edge("a3", ordinalFirst, Node(31)), Edge("a4", Node(31), Second),
            Edge("b1", shorterTotal, First), Edge("b2", shorterTotal, Node(32)),
            Edge("b3", Node(32), Second),
        ]).CommonAncestorAsync(new([First, Second], 1000), Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
        Assert.Equal(shorterTotal, result.NodeId);
        Assert.Equal([1, 2], result.Paths.Select(static path => path.EdgeIds.Count).ToArray());
    }

    [Fact]
    public async Task Ancestor_scoring_uses_ordinal_id_only_after_both_scores_tie()
    {
        var ordinalFirst = Node(1);
        var ordinalSecond = Node(2);
        var result = await Query([
            Edge("a1", ordinalFirst, First), Edge("a2", ordinalFirst, Node(30)),
            Edge("a3", Node(30), Second), Edge("b1", ordinalSecond, Node(31)),
            Edge("b2", Node(31), First), Edge("b3", ordinalSecond, Second),
        ]).CommonAncestorAsync(new([First, Second], 1000), Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
        Assert.Equal(ordinalFirst, result.NodeId);
        Assert.Equal([1, 2], result.Paths.Select(static path => path.EdgeIds.Count).ToArray());
    }

    private static TopologyGraphQueryService Query(IReadOnlyList<TopologyEdgeProjection> edges) =>
        new(new MemorySource(new(9, edges)));

    private static TopologyEdgeProjection Edge(string id, string from, string to) =>
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Declared, true, 1,
            "A", "A", TopologyEdgeVisibility.SameOwner, 0, 1000, null, 9, 1, false);

    private static string Node(int number) => TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse($"00000000-0000-0000-0000-{number:D12}"));

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
