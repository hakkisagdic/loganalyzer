using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TopologyDeclaredEdgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("depends_on", true)]
    [InlineData("contains", true)]
    [InlineData("connects_to", true)]
    [InlineData("observed", false)]
    [InlineData("DependsOn", false)]
    public void Relation_wire_whitelist_is_exact(string relation, bool accepted) =>
        Assert.Equal(accepted, TopologyEdgeRelations.Valid(relation));

    [Theory]
    [InlineData(TopologyNodeKind.Source, TopologyNodeKind.Service, true)]
    [InlineData(TopologyNodeKind.Service, TopologyNodeKind.ServiceInstance, true)]
    [InlineData(TopologyNodeKind.ServiceInstance, TopologyNodeKind.Service, false)]
    [InlineData(TopologyNodeKind.Source, TopologyNodeKind.Network, false)]
    public void Contains_uses_only_authoritative_hierarchy(TopologyNodeKind from, TopologyNodeKind to, bool expected) =>
        Assert.Equal(expected, TopologyEdgeRelations.ValidEndpoints(TopologyEdgeRelations.Contains, from, to));

    [Fact]
    public void Observed_write_payload_is_rejected()
    {
        var input = new TopologyDeclaredEdgeInput(
            TopologyIdentity.Node(TopologyNodeKind.Source, Guid.NewGuid()),
            TopologyIdentity.Node(TopologyNodeKind.Source, Guid.NewGuid()),
            TopologyEdgeRelations.DependsOn, Provenance: "observed");
        Assert.NotNull(TopologyEdgeRegistry.Validate(input));
    }

    [Fact]
    public async Task Versioned_lifecycle_preserves_history_and_audit()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.AddRange(new SourceEntity { SourceId = "SA", OwnerGroup = "A" },
                new SourceEntity { SourceId = "SB", OwnerGroup = "A" });
            await db.SaveChangesAsync(Ct);
        }
        string from, to;
        await using (var db = factory.CreateDbContext())
        {
            from = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SA", Ct)).Id;
            to = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SB", Ct)).Id;
        }
        var registry = new TopologyEdgeRegistry(factory);
        var scope = AccessScope.ForGroups("actor", ["A"]);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        var created = await registry.CreateAsync(scope, true, input, Ct);
        Assert.Equal(201, created.Status);
        Assert.Equal("1", created.Edge!.Version);
        Assert.Equal("declared", created.Edge.Provenance);
        Assert.Equal(1m, created.Edge.Confidence);
        Assert.True(created.Edge.Directed);
        Assert.Equal(409, (await registry.CreateAsync(scope, true, input, Ct)).Status);
        var changed = await registry.UpdateAsync(scope, true, created.Edge.Id,
            input with { Relation = TopologyEdgeRelations.ConnectsTo, Version = "1" }, Ct);
        Assert.Equal(200, changed.Status);
        Assert.Equal("2", changed.Edge!.Version);
        Assert.Equal(409, (await registry.DeleteAsync(scope, true, created.Edge.Id, 1, Ct)).Status);
        Assert.Equal(204, (await registry.DeleteAsync(scope, true, created.Edge.Id, 2, Ct)).Status);
        Assert.Equal(409, (await registry.DeleteAsync(scope, true, created.Edge.Id, 2, Ct)).Status);
        await using var check = factory.CreateDbContext();
        var history = await check.TopologyDeclaredEdgeHistory.OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(3, history.Length);
        Assert.Equal(history[0].ToNano, history[1].FromNano);
        Assert.Equal(history[1].ToNano, history[2].FromNano);
        Assert.NotNull(history[2].DeletedAt);
        Assert.Equal(3, await check.AuditLog.CountAsync(Ct));
    }

    [Fact]
    public async Task Int64_max_edge_update_conflicts_without_state_audit_or_epoch_change_but_delete_succeeds()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.AddRange(new SourceEntity { SourceId = "SA", OwnerGroup = "A" },
                new SourceEntity { SourceId = "SB", OwnerGroup = "A" });
            await db.SaveChangesAsync(Ct);
        }
        string from, to;
        await using (var db = factory.CreateDbContext())
        {
            from = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SA", Ct)).Id;
            to = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SB", Ct)).Id;
        }
        var registry = new TopologyEdgeRegistry(factory);
        var scope = AccessScope.ForGroups("admin-A", ["A"]);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        var created = (await registry.CreateAsync(scope, true, input, Ct)).Edge!;
        long epoch;
        await using (var db = factory.CreateDbContext())
        {
            (await db.TopologyDeclaredEdges.SingleAsync(e => e.Id == created.Id, Ct)).Version = long.MaxValue;
            (await db.TopologyDeclaredEdgeHistory.SingleAsync(h => h.EdgeId == created.Id && h.ToNano == null, Ct))
                .EdgeVersion = long.MaxValue;
            await db.SaveChangesAsync(Ct);
            epoch = (await db.TopologyReadState.SingleAsync(Ct)).Epoch;
        }

        var rejected = await registry.UpdateAsync(scope, true, created.Id,
            input with { Version = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Relation = TopologyEdgeRelations.ConnectsTo }, Ct);
        Assert.Equal(409, rejected.Status);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(TopologyEdgeRelations.DependsOn,
                (await db.TopologyDeclaredEdges.SingleAsync(e => e.Id == created.Id, Ct)).Relation);
            Assert.Single(await db.TopologyDeclaredEdgeHistory.Where(h => h.EdgeId == created.Id).ToArrayAsync(Ct));
            Assert.Single(await db.AuditLog.ToArrayAsync(Ct));
            Assert.Equal(epoch, (await db.TopologyReadState.SingleAsync(Ct)).Epoch);
        }

        Assert.Equal(204, (await registry.DeleteAsync(scope, true, created.Id, long.MaxValue, Ct)).Status);
        await using var deleted = factory.CreateDbContext();
        var edge = await deleted.TopologyDeclaredEdges.SingleAsync(e => e.Id == created.Id, Ct);
        Assert.NotNull(edge.DeletedAt);
        Assert.Equal(long.MaxValue, edge.Version);
        Assert.Equal(2, await deleted.TopologyDeclaredEdgeHistory.CountAsync(h => h.EdgeId == created.Id, Ct));
        Assert.Equal(2, await deleted.AuditLog.CountAsync(Ct));
    }

    [Fact]
    public async Task Both_endpoint_owners_are_required_for_create_and_update()
    {
        using var factory = new InMemoryControlPlaneFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.AddRange(new SourceEntity { SourceId = "SA", OwnerGroup = "A" },
                new SourceEntity { SourceId = "SB", OwnerGroup = "B" });
            await db.SaveChangesAsync(Ct);
        }
        string from, to;
        await using (var db = factory.CreateDbContext())
        {
            from = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SA", Ct)).Id;
            to = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "SB", Ct)).Id;
        }
        var registry = new TopologyEdgeRegistry(factory);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        Assert.Equal(403, (await registry.CreateAsync(AccessScope.ForGroups("actor", ["A"]), true, input, Ct)).Status);
        Assert.Equal(403, (await registry.CreateAsync(AccessScope.ForGroups("actor", ["A", "B"]), false, input, Ct)).Status);
        var created = await registry.CreateAsync(AccessScope.ForGroups("actor", ["A", "B"]), true, input, Ct);
        Assert.Equal(201, created.Status);
        Assert.Equal(403, (await registry.UpdateAsync(AccessScope.ForGroups("actor", ["A"]), true,
            created.Edge!.Id, input with { Version = "1" }, Ct)).Status);
        await using var check = factory.CreateDbContext();
        Assert.Single(await check.AuditLog.ToArrayAsync(Ct));
    }
}
