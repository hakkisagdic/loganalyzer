using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyPublicationIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publication_cursor_interleavings_use_one_contiguous_two_store_fence()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        var watermarkReader = new TopologyPublicationWatermarkReader(storage);
        var watermarkWriter = new TopologyPublicationWatermarkWriter(watermarkReader, storage);
        var readiness = await InitializeReadyAsync(factory, storage);
        var revisions = new TopologyPublicationRevisionSource(factory, watermarkReader, readiness);
        var publisher = new TopologyPublicationCoordinator(factory, watermarkReader, watermarkWriter);

        var initial = await revisions.ReadAsync(Ct);
        Assert.Equal(0UL, initial.ClickHouseWatermark);
        Assert.NotNull(initial.RepairStamp);
        ulong insertedSequence = 0;
        var published = await publisher.PublishAsync(new string('a', 64), async (sequence, token) =>
        {
            insertedSequence = sequence;
            Assert.Equal(0UL, await watermarkReader.ReadAsync(token));
            await using var beforeAck = await factory.CreateDbContextAsync(token);
            var state = await beforeAck.TopologyReadState.SingleOrDefaultAsync(token);
            Assert.True(state is null || state.PublishedSequence == 0);
        }, Ct);

        Assert.Equal(1UL, insertedSequence);
        Assert.Equal(1UL, published);
        var afterFirstPublication = await revisions.ReadAsync(Ct);
        Assert.Equal(initial.PostgresEpoch + 1, afterFirstPublication.PostgresEpoch);
        Assert.Equal(1UL, afterFirstPublication.ClickHouseWatermark);
        Assert.Equal(initial.RepairStamp, afterFirstPublication.RepairStamp);
        Assert.Equal(1UL, await publisher.PublishAsync(new string('a', 64), (_, _) =>
            throw new InvalidOperationException("A committed identity must not replay its callback."), Ct));

        // PG committed but CH ACK was lost. Public reads reject the gap; the
        // next publisher repairs sequence 2 before allocating sequence 3.
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var state = await db.TopologyReadState.SingleAsync(Ct);
            state.PublishedSequence = 2;
            state.Epoch++;
            await db.SaveChangesAsync(Ct);
        }
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() => revisions.ReadAsync(Ct));
        Assert.Equal(3UL, await publisher.PublishAsync(new string('b', 64), (sequence, _) =>
        {
            Assert.Equal(3UL, sequence);
            return Task.CompletedTask;
        }, Ct));
        var afterRecovery = await revisions.ReadAsync(Ct);
        Assert.Equal(initial.PostgresEpoch + 3, afterRecovery.PostgresEpoch);
        Assert.Equal(3UL, afterRecovery.ClickHouseWatermark);
        Assert.Equal(initial.RepairStamp, afterRecovery.RepairStamp);
        Assert.Equal(1UL, await publisher.PublishAsync(new string('a', 64), (_, _) =>
            throw new InvalidOperationException("A late duplicate must retain its original receipt."), Ct));
    }

    [Fact]
    public async Task Failed_publication_hole_never_advances_the_committed_watermark()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await InitializeReadyAsync(factory, storage);
        var watermarkReader = new TopologyPublicationWatermarkReader(storage);
        var publisher = new TopologyPublicationCoordinator(factory, watermarkReader,
            new TopologyPublicationWatermarkWriter(watermarkReader, storage));

        var key = new string('c', 64);
        await Assert.ThrowsAsync<IOException>(() => publisher.PublishAsync(key, (_, _) =>
            throw new IOException("projection insert failed"), Ct));
        Assert.Equal(key, await publisher.ReadPendingKeyAsync(Ct));
        Assert.Equal(0UL, await watermarkReader.ReadAsync(Ct));
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.True(await db.TopologyReadState.AllAsync(s => s.PublishedSequence == 0, Ct));

        // CH may already contain rows at sequence 1. A different event may
        // never reuse that sequence and publish the orphaned projection.
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() => publisher.PublishAsync(
            new string('d', 64), (_, _) => Task.CompletedTask, Ct));
        Assert.Equal(1UL, await publisher.PublishAsync(key, (sequence, _) =>
        {
            Assert.Equal(1UL, sequence);
            return Task.CompletedTask;
        }, Ct));
        Assert.Equal(1UL, await watermarkReader.ReadAsync(Ct));
        Assert.Null(await publisher.ReadPendingKeyAsync(Ct));
    }

    private static async Task<ITopologyObservedRepairReadiness> InitializeReadyAsync(
        IDbContextFactory<ControlPlaneDbContext> factory, ClickHouseContext storage)
    {
        var initialized = await DevStackSetup.InitializeTopologyPublicationAsync(factory, storage, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, initialized.Status);
        ITopologyObservedRepairReadiness readiness = new TopologyObservedRepairReadiness(factory, storage);
        Assert.NotNull(await readiness.RequireReadyAsync(Ct));
        return readiness;
    }
}
