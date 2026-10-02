using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyOutsideCountTests
{
    [Fact]
    public async Task Unknown_external_count_stays_null_not_zero()
    {
        var nodeId = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000062"));
        var graph = new TopologyGraphQueryService(new FailingSource());
        var result = await graph.CountExternalNeighborsAsync(new(nodeId, 1000),
            AccessScope.ForGroups("reader-A", ["A"]), TestContext.Current.CancellationToken);
        Assert.Null(result.Count);
        Assert.Equal("QueryUnavailable", result.Reason);
    }

    private sealed class FailingSource : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromException<TopologyGraphSnapshot>(new IOException("read unavailable"));
    }
}
