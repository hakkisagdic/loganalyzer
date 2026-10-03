using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologyRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TopologyNodeInput Service(string owner = "A", string source = "SA") =>
        new(TopologyNodeKind.Service, "checkout", owner, true, [new(source, "n", "checkout")]);

    [Theory]
    [InlineData("kind")]
    [InlineData("blank")]
    [InlineData("source")]
    [InlineData("duplicate")]
    [InlineData("namespace")]
    [InlineData("instance")]
    [InlineData("foreignservice")]
    [InlineData("version")]
    public void Invalid_node_or_alias_is_rejected(string error)
    {
        var valid = Service();
        var invalid = error switch
        {
            "kind" => valid with { Kind = (TopologyNodeKind)99 },
            "blank" => valid with { DisplayName = " " },
            "source" => valid with { SourceId = "SA" },
            "duplicate" => valid with { Bindings = [valid.Bindings[0], valid.Bindings[0]] },
            "namespace" => valid with { Bindings = [new("SA", null!, "checkout")] },
            "instance" => valid with { Kind = TopologyNodeKind.ServiceInstance },
            "foreignservice" => valid with { Bindings = [new("SA", "n", "checkout", "service:invalid")] },
            _ => valid with { Version = 0 },
        };
        Assert.NotNull(TopologyRegistry.Validate(invalid));
        Assert.Null(TopologyRegistry.Validate(valid));
    }

    [Fact]
    public void Unassigned_is_a_negative_resolution_bucket_not_an_authoritative_owner()
    {
        Assert.NotNull(TopologyRegistry.Validate(Service(OwnerGroups.Unassigned)));
        Assert.Null(TopologyRegistry.Validate(Service("A")));
    }

    [Fact]
    public async Task Versioned_registry_keeps_identity_and_audits_each_committed_change()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        { db.Sources.Add(new() { SourceId = "SA", OwnerGroup = "A" }); await db.SaveChangesAsync(Ct); }
        var registry = new TopologyRegistry(factory);
        var scope = AccessScope.ForGroups("admin-A", ["A"]);
        var input = Service();
        var created = await registry.CreateAsync(scope, true, input, Ct);
        Assert.Equal(201, created.Status);
        Assert.Equal(1, created.Node!.Version);
        var renamed = await registry.UpdateAsync(scope, true, created.Node.Id, input with { DisplayName = "renamed", Version = 1 }, Ct);
        Assert.Equal(200, renamed.Status); Assert.Equal(created.Node.Id, renamed.Node!.Id);
        Assert.Equal(409, (await registry.UpdateAsync(scope, true, created.Node.Id, input with { Version = 1 }, Ct)).Status);
        Assert.Equal(204, (await registry.DeleteAsync(scope, true, created.Node.Id, 2, Ct)).Status);
        Assert.Equal(409, (await registry.DeleteAsync(scope, true, created.Node.Id, 2, Ct)).Status);
        await using var check = factory.CreateDbContext();
        Assert.Equal(3, await check.TopologyNodeHistory.CountAsync(h => h.NodeId == created.Node.Id, Ct));
        var binding = Assert.Single(await check.TopologyBindings.ToArrayAsync(Ct));
        Assert.NotNull(binding.ToNano); Assert.True(binding.SourceHistoryRevision > 0);
        Assert.Equal(3, await check.AuditLog.CountAsync(Ct));
        Assert.All(await check.AuditLog.ToArrayAsync(Ct), a => Assert.Equal("admin-A", a.Subject));
    }

    [Fact]
    public async Task Int64_max_node_update_conflicts_without_state_audit_or_epoch_change_but_delete_succeeds()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        { db.Sources.Add(new() { SourceId = "SA", OwnerGroup = "A" }); await db.SaveChangesAsync(Ct); }
        var registry = new TopologyRegistry(factory);
        var scope = AccessScope.ForGroups("admin-A", ["A"]);
        var created = (await registry.CreateAsync(scope, true, Service(), Ct)).Node!;
        long epoch;
        await using (var db = factory.CreateDbContext())
        {
            (await db.TopologyNodes.SingleAsync(n => n.Id == created.Id, Ct)).Version = long.MaxValue;
            (await db.TopologyNodeHistory.SingleAsync(h => h.NodeId == created.Id && h.ToNano == null, Ct))
                .NodeVersion = long.MaxValue;
            await db.SaveChangesAsync(Ct);
            epoch = (await db.TopologyReadState.SingleAsync(Ct)).Epoch;
        }

        var rejected = await registry.UpdateAsync(scope, true, created.Id,
            Service() with { Version = long.MaxValue, DisplayName = "must-not-commit" }, Ct);
        Assert.Equal(409, rejected.Status);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("checkout", (await db.TopologyNodes.SingleAsync(n => n.Id == created.Id, Ct)).DisplayName);
            Assert.Single(await db.TopologyNodeHistory.Where(h => h.NodeId == created.Id).ToArrayAsync(Ct));
            Assert.Single(await db.AuditLog.ToArrayAsync(Ct));
            Assert.Equal(epoch, (await db.TopologyReadState.SingleAsync(Ct)).Epoch);
        }

        Assert.Equal(204, (await registry.DeleteAsync(scope, true, created.Id, long.MaxValue, Ct)).Status);
        await using var deleted = factory.CreateDbContext();
        var node = await deleted.TopologyNodes.SingleAsync(n => n.Id == created.Id, Ct);
        Assert.True(node.Deleted);
        Assert.Equal(long.MaxValue, node.Version);
        Assert.Equal(2, await deleted.TopologyNodeHistory.CountAsync(h => h.NodeId == created.Id, Ct));
        Assert.Equal(2, await deleted.AuditLog.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false, "A", "A", "SA")]
    [InlineData(true, "A", "B", "SB")]
    [InlineData(true, "A", "A", "SB")]
    public async Task Admin_and_source_and_target_scope_are_independent(bool admin, string actor, string owner, string source)
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        { db.Sources.AddRange(new SourceEntity { SourceId = "SA", OwnerGroup = "A" }, new SourceEntity { SourceId = "SB", OwnerGroup = "B" }); await db.SaveChangesAsync(Ct); }
        Assert.Equal(403, (await new TopologyRegistry(factory).CreateAsync(AccessScope.ForGroups("actor", [actor]), admin, Service(owner, source), Ct)).Status);
        await using var check = factory.CreateDbContext();
        Assert.Equal(2, await check.TopologyNodes.CountAsync(Ct));
        Assert.Empty(await check.TopologyBindings.ToArrayAsync(Ct));
        Assert.Empty(await check.AuditLog.ToArrayAsync(Ct));
    }

    [Fact]
    public async Task Inventory_update_does_not_revive_a_tombstoned_source_node()
    {
        using var factory = new InMemoryControlPlaneFactory();
        string id;
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.Add(new() { SourceId = "SA", OwnerGroup = "A" }); await db.SaveChangesAsync(Ct);
            id = (await db.TopologyNodes.SingleAsync(Ct)).Id;
        }
        Assert.Equal(204, (await new TopologyRegistry(factory).DeleteAsync(AccessScope.ForGroups("admin", ["A"]), true, id, 1, Ct)).Status);
        await using (var db = factory.CreateDbContext())
        { (await db.Sources.SingleAsync(Ct)).Hostname = "new-name"; await db.SaveChangesAsync(Ct); }
        await using var check = factory.CreateDbContext();
        Assert.True((await check.TopologyNodes.SingleAsync(Ct)).Deleted);
        Assert.False((await check.TopologyNodeHistory.SingleAsync(h => h.ToNano == null, Ct)).Enabled);
    }

    [Fact]
    public async Task Source_transfer_preserves_old_history_and_rechecks_old_authority()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.Add(new() { SourceId = "SA", OwnerGroup = "A" });
            await db.SaveChangesAsync(Ct);
        }
        var registry = new TopologyRegistry(factory);
        var node = (await registry.CreateAsync(AccessScope.ForGroups("admin-A", ["A"]), true, Service(), Ct)).Node!;
        await using (var db = factory.CreateDbContext())
        {
            (await db.Sources.SingleAsync(Ct)).OwnerGroup = "B";
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(403, (await registry.UpdateAsync(AccessScope.ForGroups("stale-A", ["A"]), true,
            node.Id, Service() with { Version = 1, DisplayName = "stale" }, Ct)).Status);
        Assert.Equal(200, (await registry.UpdateAsync(AccessScope.ForGroups("admin-AB", ["A", "B"]), true,
            node.Id, Service("B") with { Version = 1, DisplayName = "transferred" }, Ct)).Status);

        await using var check = factory.CreateDbContext();
        var history = await check.TopologyNodeHistory.Where(h => h.NodeId == node.Id).OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(new[] { "A", "B" }, history.Select(h => h.OwnerGroup));
        Assert.Equal(history[0].ToNano, history[1].FromNano);
        Assert.Equal("transferred", (await check.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct)).DisplayName);
        var transfer = await check.TopologyOwnerHistory.SingleAsync(h => h.NodeId == node.Id, Ct);
        Assert.Equal("A", transfer.OldOwner);
        Assert.Equal("B", transfer.NewOwner);
        Assert.Equal(2, transfer.NodeVersion);
        Assert.Equal("admin-AB", transfer.ChangedBy);
    }
}
