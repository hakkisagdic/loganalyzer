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

        public Fixture(IAuditSink audit)
        {
            var graph = new TopologyGraphQueryService(new MemorySource());
            Query = new ScopedQuery(new EventReader(_storage), new ChangeEventReader(_storage),
                new CorrelationReader(_storage), new EventWriter(_storage), _controlPlane, audit,
                topology: graph);
        }

        public void Dispose() { _controlPlane.Dispose(); _storage.Dispose(); }
    }

    private sealed class MemorySource : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
            Task.FromResult(new TopologyGraphSnapshot(0, [])
            {
                Nodes = [new TopologyNodeProjection(NodeId, TopologyNodeKind.Service, "visible", "A", true,
                    false, 1, 0, null)],
            });
    }
}
