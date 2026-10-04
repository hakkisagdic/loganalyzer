using Bizigo.Contracts;
using Bizigo.Query;
using System.Data.Common;

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

    [Fact]
    public async Task Driver_outage_is_unknown_not_a_measured_zero()
    {
        var nodeId = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000062"));
        var graph = new TopologyGraphQueryService(new FailingSource(new DriverUnavailableException()));
        var result = await graph.CountExternalNeighborsAsync(new(nodeId, 1000),
            AccessScope.ForGroups("reader-A", ["A"]), TestContext.Current.CancellationToken);
        Assert.Null(result.Count);
        Assert.Equal("QueryUnavailable", result.Reason);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_unknown_count()
    {
        var nodeId = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000062"));
        var graph = new TopologyGraphQueryService(new FailingSource(new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => graph.CountExternalNeighborsAsync(
            new(nodeId, 1000), AccessScope.ForGroups("reader-A", ["A"]), TestContext.Current.CancellationToken));
    }

    private sealed class DriverUnavailableException : DbException { }

    private sealed class FailingSource(Exception? failure = null) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromException<TopologyGraphSnapshot>(failure ?? new IOException("read unavailable"));
    }
}
