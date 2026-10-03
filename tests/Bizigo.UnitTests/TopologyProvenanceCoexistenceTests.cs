using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyProvenanceCoexistenceTests
{
    [Fact]
    public async Task Declared_and_observed_parallel_edges_remain_separate_proofs()
    {
        var from = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000071"));
        var to = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000072"));
        TopologyEdgeProjection Edge(string id, TopologyProvenance provenance) => new(id, from, to,
            TopologyRelation.DependsOn, provenance, true, provenance == TopologyProvenance.Declared ? 1m : 0.5m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 100, 500, null, 1, 1, false);
        var service = new TopologyGraphQueryService(new MemorySource([Edge("declared", TopologyProvenance.Declared),
            Edge("observed", TopologyProvenance.Observed)]));

        var all = await service.SearchEdgesAsync(new(1000), AccessScope.ForGroups("reader-A", ["A"]),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, all.Items.Count);
        Assert.Equal(["declared", "observed"], all.Items.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([TopologyProvenance.Declared, TopologyProvenance.Observed],
            all.Items.Select(e => e.Provenance).Order().ToArray());
    }

    private sealed class MemorySource(IReadOnlyList<TopologyEdgeProjection> edges) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromResult(new TopologyGraphSnapshot(1, edges));
    }
}
