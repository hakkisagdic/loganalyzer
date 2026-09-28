using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only real PostgreSQL: production single/CSV writes bind one
/// immutable source node to committed history, rollback atomically, and do not
/// invent pre-migration topology history. Resolver fixture tests the actual SQL join.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyIdentityIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Single_and_csv_inventory_identity()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-2));
        string identity;
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            db.HistoryClock = clock;
            Assert.Equal(201, SourceOwnershipHistoryIntegrationTests.Status(await SourceOwnershipHistoryIntegrationTests.Upsert(db,
                new() { SourceId = "SA", OwnerGroup = "A", Hostname = "old" })));
            identity = (await db.TopologyNodes.SingleAsync(Ct)).Id;
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            db.HistoryClock = clock;
            Assert.Equal(200, SourceOwnershipHistoryIntegrationTests.Status(await SourceOwnershipHistoryIntegrationTests.Csv(db,
                "SA,B,new", "SB,A,other")));
        }
        await using var check = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(2, await check.TopologyNodes.CountAsync(Ct));
        var node = await check.TopologyNodes.SingleAsync(n => n.SourceId == "SA", Ct);
        Assert.Equal(identity, node.Id); Assert.Equal("B", node.OwnerGroup); Assert.Equal("new", node.DisplayName);
        Assert.Equal(TopologyNodeKind.Source, TopologyIdentity.Kind(node.Id));
        var history = await check.TopologyNodeHistory.Where(h => h.NodeId == identity).OrderBy(h => h.Revision).ToArrayAsync(Ct);
        Assert.Equal(new[] { "A", "B" }, history.Select(h => h.OwnerGroup));
        Assert.Equal(history[0].ToNano, history[1].FromNano);
        var ownerHistory = await check.SourceOwnershipHistory.Where(h => h.SourceId == "SA").OrderBy(h => h.Revision).ToArrayAsync(Ct);
        Assert.Equal(ownerHistory.Select(h => (long?)h.Revision), history.Select(h => h.SourceHistoryRevision));
        Assert.Equal(ownerHistory.Select(h => h.EffectiveFromNano), history.Select(h => h.FromNano));
        Assert.Equal(2, (await check.TopologyReadState.SingleAsync(Ct)).Epoch);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Topology_failure_rolls_back_inventory_history_and_epoch(bool csv)
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using (var db = await factory.CreateDbContextAsync(Ct))
            await SourceOwnershipHistoryIntegrationTests.Upsert(db, new() { SourceId = "SA", OwnerGroup = "A" });
        await using var admin = await factory.CreateDbContextAsync(Ct);
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bizigo.reject_topology_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'topology fixture write failure'; END $$;
            CREATE TRIGGER topology_test BEFORE INSERT ON bizigo.topology_node_history
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_topology_test();
            """, Ct);
        try
        {
            await using var update = await factory.CreateDbContextAsync(Ct);
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                if (csv) await SourceOwnershipHistoryIntegrationTests.Csv(update, "SA,B,new", "SB,B,second");
                else await SourceOwnershipHistoryIntegrationTests.Upsert(update, new() { SourceId = "SA", OwnerGroup = "B" });
            });
            await using var check = await factory.CreateDbContextAsync(Ct);
            Assert.Equal("A", (await check.Sources.SingleAsync(Ct)).OwnerGroup);
            Assert.Equal("A", (await check.TopologyNodes.SingleAsync(Ct)).OwnerGroup);
            Assert.Null((await check.TopologyNodeHistory.SingleAsync(Ct)).ToNano);
            Assert.Null((await check.SourceOwnershipHistory.SingleAsync(Ct)).EffectiveToNano);
            Assert.Equal(1, (await check.TopologyReadState.SingleAsync(Ct)).Epoch);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TRIGGER topology_test ON bizigo.topology_node_history; DROP FUNCTION bizigo.reject_topology_test();", Ct);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Concurrent_transfer_retains_one_identity_and_revision_chain()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using (var db = await factory.CreateDbContextAsync(Ct))
            await SourceOwnershipHistoryIntegrationTests.Upsert(db, new() { SourceId = "SA", OwnerGroup = "A" });
        await using var first = await factory.CreateDbContextAsync(Ct);
        await using var second = await factory.CreateDbContextAsync(Ct);
        await first.Sources.LoadAsync(Ct); await second.Sources.LoadAsync(Ct);
        var results = await Task.WhenAll(
            SourceOwnershipHistoryIntegrationTests.Upsert(first, new() { SourceId = "SA", OwnerGroup = "B" }),
            SourceOwnershipHistoryIntegrationTests.Upsert(second, new() { SourceId = "SA", OwnerGroup = "C" }));
        Assert.Equal(new[] { 200, 409 }, results.Select(SourceOwnershipHistoryIntegrationTests.Status).Order());
        await using var check = await factory.CreateDbContextAsync(Ct);
        var node = await check.TopologyNodes.SingleAsync(Ct);
        var history = await check.TopologyNodeHistory.OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(2, history.Length); Assert.All(history, h => Assert.Equal(node.Id, h.NodeId));
        Assert.Equal(history[0].ToNano, history[1].FromNano);
        Assert.Equal(node.OwnerGroup, history[1].OwnerGroup);
        Assert.Equal(2, (await check.TopologyReadState.SingleAsync(Ct)).Epoch);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Registry_sql_join_preserves_sources_and_half_open_history()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var serviceA = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        var serviceB = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            foreach (var (source, owner, node) in new[] { ("SA", "A", serviceA), ("SB", "B", serviceB) })
            {
                db.TopologyBindings.Add(new() { BindingId = Guid.NewGuid(), SourceId = source, ServiceNamespace = "n",
                    ServiceName = "checkout", ServiceNodeId = node, TargetNodeId = node, InstanceId = "", FromNano = 100 });
                db.TopologyNodeHistory.Add(new() { NodeId = node, OwnerGroup = owner, DisplayName = "checkout",
                    NodeVersion = 1, Enabled = true, FromNano = 100 });
            }
            await db.SaveChangesAsync(Ct);
        }
        var resolver = new HistoricalTopologyBindings(factory);
        TopologyBindingRequest Request(string source, string owner, ulong time) => new(new("leaf", source, owner, 1, time, "known"), "n", "checkout", null);
        var actual = await resolver.ResolveAsync([Request("SA", "A", 99), Request("SA", "A", 100), Request("SB", "B", 101)], Ct);
        Assert.Equal("MissingBinding", actual[0].Reason);
        Assert.Equal(serviceA, actual[1].NodeId); Assert.Equal(serviceB, actual[2].NodeId);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Bootstrap_starts_at_migration_not_source_creation()
    {
        var name = "topology_bootstrap_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^topology_bootstrap_[a-f0-9]{32}$", name);
        await using var admin = new ControlPlaneFactory(stack.PostgresConnectionString).CreateDbContext();
#pragma warning disable EF1003 // Owned generated identifier checked above; not caller input.
        await admin.Database.ExecuteSqlRawAsync("CREATE DATABASE " + name, Ct);
#pragma warning restore EF1003
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(stack.PostgresConnectionString) { Database = name }.ConnectionString;
            await using var db = new ControlPlaneFactory(connection).CreateDbContext();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260927100000_AddMissingEvidenceKinds", Ct);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO bizigo.sources
                (source_id,owner_group,enabled,encoding,source_class,vendor,product,upstream,vlan,firmware,created_at,updated_at)
                VALUES ('old','A',true,'auto','default','','','','','','2000-01-01T00:00:00Z',now())
                """, Ct);
            await migrator.MigrateAsync(cancellationToken: Ct);
            var node = await db.TopologyNodes.SingleAsync(Ct);
            var history = await db.TopologyNodeHistory.SingleAsync(Ct);
            Assert.Equal(node.Id, history.NodeId);
            Assert.True(history.FromNano > TopologyIdentity.Nano(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            var id = node.Id;
            await migrator.MigrateAsync(cancellationToken: Ct);
            Assert.Equal(id, (await db.TopologyNodes.SingleAsync(Ct)).Id);
            Assert.Equal(1, await db.TopologyNodeHistory.CountAsync(Ct));
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync("20260927100000_AddMissingEvidenceKinds", Ct));
            Assert.Equal(id, (await db.TopologyNodes.SingleAsync(Ct)).Id);
            Assert.Equal(1, await db.TopologyNodeHistory.CountAsync(Ct));
        }
        finally
        {
#pragma warning disable EF1003 // Exact generated owned database only.
            await admin.Database.ExecuteSqlRawAsync("DROP DATABASE " + name + " WITH (FORCE)", Ct);
#pragma warning restore EF1003
        }
    }
    [Fact, Trait("Category", "Integration")]
    public async Task Save_without_accept_preserves_pending_states_without_duplicate_writes()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
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

    [Theory, Trait("Category", "Integration")]
    [InlineData("absent")]
    [InlineData("gap")]
    [InlineData("overlap")]
    public async Task Existing_binding_with_corrupt_node_history_is_not_missing(string corruption)
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
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
