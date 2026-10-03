using System.Data.Common;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologyAuditTests
{
    private static readonly string NodeId = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("00000000-0000-0000-0000-000000000061"));

    [Fact]
    public async Task Read_audit_failure_never_returns_data()
    {
        var audit = new FakeAudit { Fail = true };
        using var fixture = new Fixture(audit);
        await Assert.ThrowsAsync<IOException>(() => fixture.Query.SearchTopologyNodesAsync(
            new(1000), AccessScope.ForGroups("actor-A", ["A"]), TestContext.Current.CancellationToken));
        Assert.Equal("topology.nodes.list", Assert.Single(audit.Records).Action);
    }

    [Fact]
    public async Task Healthy_read_audit_commits_actor_scope_and_count()
    {
        var audit = new FakeAudit();
        using var fixture = new Fixture(audit);
        var page = await fixture.Query.SearchTopologyNodesAsync(new(1000),
            AccessScope.ForGroups("actor-A", ["A"]), TestContext.Current.CancellationToken);
        Assert.Equal(NodeId, Assert.Single(page.Items).Id);
        var record = Assert.Single(audit.Records);
        Assert.Equal("actor-A", record.Subject);
        Assert.Equal(1, record.RowCount);
        Assert.Contains("A", record.Scope, StringComparison.Ordinal);
        Assert.True(record.Succeeded);
    }

    [Fact]
    public async Task Unrelated_owner_conflict_keeps_healthy_audit_success_and_related_failure()
    {
        var audit = new FakeAudit();
        var a = NodeId;
        var b = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000062"));
        var snapshot = new TopologyGraphSnapshot(0,
            [new TopologyEdgeProjection("healthy", a, a, TopologyRelation.DependsOn,
                TopologyProvenance.Observed, true, 0.5m, "A", "A", TopologyEdgeVisibility.SameOwner,
                0, 1000, 2000, 0, 1, false)])
        { ConflictCandidates = [new TopologyConflictProjection("B", b, 1000, 2000)] };
        using var fixture = new Fixture(audit, snapshot);
        Assert.Single((await fixture.Query.SearchTopologyEdgesAsync(new(1001),
            AccessScope.ForGroups("actor-A", ["A"]), TestContext.Current.CancellationToken)).Items);
        await Assert.ThrowsAsync<TopologyConflictException>(() => fixture.Query.SearchTopologyEdgesAsync(new(1001),
            AccessScope.ForGroups("actor-B", ["B"]), TestContext.Current.CancellationToken));
        Assert.Collection(audit.Records,
            record => { Assert.Equal("topology.edges.list", record.Action); Assert.True(record.Succeeded); Assert.Equal(1, record.RowCount); },
            record => { Assert.Equal("topology.edges.list", record.Action); Assert.False(record.Succeeded); Assert.Equal(0, record.RowCount); });
    }

    [Fact]
    public async Task Unknown_outside_count_audits_failure_while_measured_zero_audits_success()
    {
        var scope = AccessScope.ForGroups("actor-A", ["A"]);
        var query = new TopologyNeighborhoodQuery(NodeId, 1000);
        var failureAudit = new FakeAudit();
        using (var failed = new Fixture(failureAudit, readFailure: new DriverUnavailableException()))
        {
            var unavailable = await failed.Query.CountExternalTopologyNeighborsAsync(query, scope,
                TestContext.Current.CancellationToken);
            Assert.Null(unavailable.Count);
            Assert.Equal("QueryUnavailable", unavailable.Reason);
        }
        var failedRecord = Assert.Single(failureAudit.Records);
        Assert.Equal("topology.neighbors.outside", failedRecord.Action);
        Assert.False(failedRecord.Succeeded);
        Assert.Contains("outcome=Failed", failedRecord.Details, StringComparison.Ordinal);
        Assert.Equal(0, failedRecord.RowCount);

        var successAudit = new FakeAudit();
        using (var healthy = new Fixture(successAudit))
        {
            var measured = await healthy.Query.CountExternalTopologyNeighborsAsync(query, scope,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, measured.Count);
            Assert.Null(measured.Reason);
        }
        var successRecord = Assert.Single(successAudit.Records);
        Assert.True(successRecord.Succeeded);
        Assert.Contains("outcome=Success", successRecord.Details, StringComparison.Ordinal);
    }

    private sealed class DriverUnavailableException : DbException { }

    private sealed class FakeAudit : IAuditSink
    {
        public bool Fail { get; init; }
        public List<AuditRecord> Records { get; } = [];
        public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Fail ? Task.FromException(new IOException("audit store unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ClickHouseContext _storage = new(new ClickHouseOptions());
        private readonly ControlPlaneDbContext _controlPlane = new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase("topology-audit-" + Guid.NewGuid()).Options);
        public ScopedQuery Query { get; }

        public Fixture(IAuditSink audit, TopologyGraphSnapshot? snapshot = null, Exception? readFailure = null)
        {
            var graph = new TopologyGraphQueryService(new MemorySource(snapshot, readFailure));
            Query = new ScopedQuery(new EventReader(_storage), new ChangeEventReader(_storage),
                new CorrelationReader(_storage), new EventWriter(_storage), _controlPlane, audit,
                topology: graph);
        }

        public void Dispose() { _controlPlane.Dispose(); _storage.Dispose(); }
    }

    private sealed class MemorySource(TopologyGraphSnapshot? snapshot, Exception? readFailure) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            readFailure is not null ? Task.FromException<TopologyGraphSnapshot>(readFailure) :
            Task.FromResult(snapshot ?? new TopologyGraphSnapshot(0, [])
            {
                Nodes = [new TopologyNodeProjection(NodeId, TopologyNodeKind.Service, "visible", "A", true,
                    false, 1, 0, null)],
            });
    }
}
