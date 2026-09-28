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
}
