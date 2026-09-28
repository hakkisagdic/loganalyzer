using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologyHistoryBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_without_accept_preserves_pending_states_without_duplicate_writes()
    {
        var factory = new InMemoryControlPlaneFactory();
        await using var db = factory.CreateDbContext();
        var source = new SourceEntity { SourceId = "source", OwnerGroup = "A" };
        db.Sources.Add(source);
        await db.SaveChangesAsync(false, Ct);
        Assert.Equal(EntityState.Added, db.Entry(source).State);
        await using (var check = factory.CreateDbContext())
        {
            Assert.Single(await check.Sources.ToArrayAsync(Ct));
            Assert.Single(await check.SourceOwnershipHistory.ToArrayAsync(Ct));
            Assert.Single(await check.TopologyNodeHistory.ToArrayAsync(Ct));
        }
        db.ChangeTracker.AcceptAllChanges();
        source.OwnerGroup = "B";
        await db.SaveChangesAsync(false, Ct);
        Assert.Equal(EntityState.Modified, db.Entry(source).State);
        Assert.Equal("A", db.Entry(source).OriginalValues.GetValue<string>(nameof(SourceEntity.OwnerGroup)));
        db.ChangeTracker.AcceptAllChanges();
        await using (var saved = factory.CreateDbContext())
        {
            Assert.Equal((await saved.Sources.SingleAsync(Ct)).UpdatedAt, source.UpdatedAt);
            Assert.Equal(source.UpdatedAt, db.Entry(source).OriginalValues.GetValue<DateTimeOffset>(nameof(SourceEntity.UpdatedAt)));
        }
        db.Sources.Remove(source);
        await db.SaveChangesAsync(false, Ct);
        Assert.Equal(EntityState.Deleted, db.Entry(source).State);
        db.ChangeTracker.AcceptAllChanges();
        await using var final = factory.CreateDbContext();
        Assert.Empty(await final.Sources.ToArrayAsync(Ct));
        Assert.Equal(3, await final.SourceOwnershipHistory.CountAsync(Ct));
        Assert.Equal(3, await final.TopologyNodeHistory.CountAsync(Ct));
        Assert.Equal(3, (await final.TopologyReadState.SingleAsync(Ct)).Epoch);
        Assert.False((await final.TopologyNodes.SingleAsync(Ct)).Enabled);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("gap")]
    [InlineData("overlap")]
    public async Task Existing_binding_with_corrupt_node_history_is_not_missing(string corruption)
    {
        var factory = new InMemoryControlPlaneFactory();
        var node = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        await using (var db = factory.CreateDbContext())
        {
            db.TopologyBindings.Add(new() { BindingId = Guid.NewGuid(), SourceId = "SA", ServiceNamespace = "n",
                ServiceName = "checkout", ServiceNodeId = node, TargetNodeId = node, InstanceId = "", FromNano = 0 });
            if (corruption != "absent") db.TopologyNodeHistory.Add(new() { NodeId = node, OwnerGroup = "A",
                DisplayName = "checkout", NodeVersion = 1, Enabled = true, FromNano = corruption == "gap" ? 101 : 0 });
            if (corruption == "overlap") db.TopologyNodeHistory.Add(new() { NodeId = node, OwnerGroup = "A",
                DisplayName = "checkout", NodeVersion = 2, Enabled = true, FromNano = 50, ToNano = 150 });
            await db.SaveChangesAsync(Ct);
        }
        var resolver = new HistoricalTopologyBindings(factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ResolveAsync([
            new(new("leaf", "SA", "A", 1, 100, "known"), "n", "checkout", null)], Ct));
        // A genuinely absent alias remains an explicit negative decision.
        var missing = await resolver.ResolveAsync([
            new(new("leaf", "SA", "A", 1, 100, "known"), "n", "unknown", null)], Ct);
        Assert.Equal("MissingBinding", Assert.Single(missing).Reason);
    }
}
