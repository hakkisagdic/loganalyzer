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

    [Fact]
    public async Task Historical_asof_cannot_revive_observed_edge_after_current_expiry()
    {
        var other = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000063"));
        var edge = new TopologyEdgeProjection("expired-observed", NodeId, other,
            TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.5m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 0, 900, 2000, 0, 1, false);
        var snapshot = new TopologyGraphSnapshot(0, [edge]);
        var scope = AccessScope.ForGroups("actor-A", ["A"]);
        using (var before = new Fixture(new FakeAudit(), snapshot, fixedNowNano: 1900))
        {
            Assert.Equal(edge.Id, (await before.Query.GetTopologyEdgeAsync(edge.Id, 1000, scope,
                TestContext.Current.CancellationToken))?.Edge.Id);
        }
        using (var after = new Fixture(new FakeAudit(), snapshot, fixedNowNano: 2000))
        {
            Assert.Null(await after.Query.GetTopologyEdgeAsync(edge.Id, 1000, scope,
                TestContext.Current.CancellationToken));
            Assert.Empty((await after.Query.SearchTopologyEdgesAsync(new(1000), scope,
                TestContext.Current.CancellationToken)).Items);
            Assert.Empty((await after.Query.SearchTopologyEdgesAsync(new TopologyEdgeQuery(1000)
                { ExpiryReadClockUnixNano = 1900 }, scope,
                TestContext.Current.CancellationToken)).Items);
            Assert.Equal(TopologyGraphResultStatus.Unreachable,
                (await after.Query.GetTopologyPathAsync(new(NodeId, other, 1000), scope,
                    TestContext.Current.CancellationToken)).Status);
        }
    }

    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    [InlineData(2001, false)]
    public async Task Scoped_server_nano_clock_enforces_exact_expiry_boundary(
        int serverNano, bool expectedVisible)
    {
        var other = TopologyIdentity.Node(TopologyNodeKind.Service,
            Guid.Parse("00000000-0000-0000-0000-000000000064"));
        var edge = new TopologyEdgeProjection("exact-expiry", NodeId, other,
            TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.5m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 0, 900, 2000, 0, 1, false);
        using var fixture = new Fixture(new FakeAudit(), new TopologyGraphSnapshot(0, [edge]),
            fixedExpiryNano: serverNano);
        var scope = AccessScope.ForGroups("actor-A", ["A"]);
        var detail = await fixture.Query.GetTopologyEdgeAsync(edge.Id, 1000, scope,
            TestContext.Current.CancellationToken);
        var list = await fixture.Query.SearchTopologyEdgesAsync(new TopologyEdgeQuery(1000)
        { ExpiryReadClockUnixNano = 1 }, scope, TestContext.Current.CancellationToken);
        Assert.Equal(expectedVisible, detail is not null);
        Assert.Equal(expectedVisible, list.Items.Any(candidate => candidate.Id == edge.Id));
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

        public Fixture(IAuditSink audit, TopologyGraphSnapshot? snapshot = null,
            Exception? readFailure = null, long fixedNowNano = 1500,
            decimal? fixedExpiryNano = null)
        {
            var graph = new TopologyGraphQueryService(new MemorySource(snapshot, readFailure));
            Query = new ScopedQuery(new EventReader(_storage), new ChangeEventReader(_storage),
                new CorrelationReader(_storage), new EventWriter(_storage), _controlPlane, audit,
                topology: graph, topologyClock: new FixedTimeProvider(fixedNowNano),
                expiryNanoClock: fixedExpiryNano is decimal exact
                    ? new FixedExpiryNanoClock(exact) : null);
        }

        public void Dispose() { _controlPlane.Dispose(); _storage.Dispose(); }
    }

    private sealed class FixedTimeProvider(long unixNano) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(unixNano / 100);
    }

    private sealed class FixedExpiryNanoClock(decimal unixNano) : ITopologyExpiryNanoClock
    {
        public decimal NowUnixNano() => unixNano;
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
