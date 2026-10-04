using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologyRepairRevisionTests
{
    [Fact]
    public void Repair_generation_or_certificate_changes_revision_even_with_same_epoch_and_watermark()
    {
        var ready = new TopologyPublicationRevision(7, 11)
        {
            RepairStamp = new(2, "certificate-a"),
        };
        Assert.NotEqual(ready, ready with { RepairStamp = new(3, "certificate-a") });
        Assert.NotEqual(ready, ready with { RepairStamp = new(2, "certificate-b") });
    }

    [Fact]
    public async Task Declared_revision_during_repair_uses_only_PG_and_observed_requires_certificate()
    {
        var factory = new MemoryFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.TopologyReadState.Add(new() { Id = 1, Epoch = 7, PublishedSequence = 11 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // No server listens here. A declared read must not ask ClickHouse for
        // a watermark or call the observed repair gate at all.
        using var context = new ClickHouseContext(new ClickHouseOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=bizigo;Username=bizigo;Password=bizigo",
        });
        var readiness = new RepairingGate();
        var source = new TopologyPublicationRevisionSource(factory,
            new TopologyPublicationWatermarkReader(context), readiness);
        var declared = await source.ReadAsync(TopologyReadMode.DeclaredOnly,
            TestContext.Current.CancellationToken);
        Assert.Equal(new TopologyPublicationRevision(7, 11), declared);
        Assert.Null(declared.RepairStamp);
        Assert.Equal(0, readiness.Calls);

        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            source.ReadAsync(TopologyReadMode.ObservedOrMixed, TestContext.Current.CancellationToken));
        Assert.Equal(1, readiness.Calls);
    }

    private sealed class RepairingGate : ITopologyObservedRepairReadiness
    {
        public int Calls { get; private set; }

        public Task<TopologyRepairReadStamp> RequireReadyAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new TopologyObservedRepairUnavailableException();
        }
    }

    private sealed class MemoryFactory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly DbContextOptions<ControlPlaneDbContext> _options =
            new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseInMemoryDatabase("topology-repair-revision-" + Guid.NewGuid()).Options;

        public ControlPlaneDbContext CreateDbContext() => new(_options);
    }
}
