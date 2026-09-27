using System.Reflection;
using System.Security.Claims;
using System.Text;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: proves actual single/CSV endpoint writes atomically
/// maintain PostgreSQL ownership history, including rollback and concurrent transfer.</summary>
[Collection(DevStackCollection.Name)]
public sealed class SourceOwnershipHistoryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static async Task<IResult> Upsert(ControlPlaneDbContext db, SourceUpsertRequest request) =>
        await (Task<IResult>)typeof(SourcesEndpoints).GetMethod("UpsertAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request, db, new Admin(), Ct])!;
    internal static async Task<IResult> Csv(ControlPlaneDbContext db, params string[] rows)
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes("source_id,owner_group,hostname\n" + string.Join('\n', rows)));
        context.Request.Body = body;
        return await (Task<IResult>)typeof(SourcesEndpoints).GetMethod("ImportCsvAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [context.Request, db, new Admin(), Ct])!;
    }
    internal static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;
    internal sealed class Admin : ICurrentUser
    { public AccessScope Scope => AccessScope.System("history-admin"); public ClaimsPrincipal? Principal => null; }

    [Fact, Trait("Category", "Integration")]
    public async Task Single_upsert_and_csv_commit_source_and_history_atomically()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            db.HistoryClock = clock;
            Assert.Equal(201, Status(await Upsert(db, new() { SourceId = "single", OwnerGroup = "A", Hostname = "old" })));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            db.HistoryClock = clock;
            Assert.Equal(200, Status(await Csv(db, "single,B,new", "csv,A,csv-host")));
        }
        await using var check = await factory.CreateDbContextAsync(Ct);
        var history = await check.SourceOwnershipHistory.OrderBy(h => h.Revision).ToArrayAsync(Ct);
        Assert.Equal(3, history.Length);
        var single = history.Where(h => h.SourceId == "single").ToArray();
        Assert.Equal(new[] { "A", "B" }, single.Select(h => h.OwnerGroup));
        Assert.Equal(single[1].EffectiveFromNano, single[0].EffectiveToNano);
        Assert.Equal("new", single[1].Hostname);
        Assert.Equal("B", (await check.Sources.SingleAsync(s => s.SourceId == "single", Ct)).OwnerGroup);
        Assert.Single(history, h => h.SourceId == "csv" && h.OwnerGroup == "A");
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_failure_rolls_back_source_and_csv_batch(bool csv)
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using (var db = await factory.CreateDbContextAsync(Ct))
            Assert.Equal(201, Status(await Upsert(db, new() { SourceId = "existing", OwnerGroup = "A" })));
        await using var admin = await factory.CreateDbContextAsync(Ct);
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION bizigo.reject_history_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture history write failure'; END $$;
            CREATE TRIGGER history_fixture BEFORE INSERT ON bizigo.source_ownership_history
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_history_fixture();
            """, Ct);
        try
        {
            await using var update = await factory.CreateDbContextAsync(Ct);
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                if (csv) await Csv(update, "existing,B,new", "new,A,new-source");
                else await Upsert(update, new() { SourceId = "existing", OwnerGroup = "B" });
            });
            await using var check = await factory.CreateDbContextAsync(Ct);
            Assert.Equal("A", (await check.Sources.SingleAsync(Ct)).OwnerGroup);
            var history = Assert.Single(await check.SourceOwnershipHistory.ToArrayAsync(Ct));
            Assert.Equal("A", history.OwnerGroup); Assert.Null(history.EffectiveToNano);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TRIGGER history_fixture ON bizigo.source_ownership_history; DROP FUNCTION bizigo.reject_history_fixture();", Ct);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Concurrent_transfers_have_monotonic_nonoverlapping_intervals()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using (var db = await factory.CreateDbContextAsync(Ct))
            await Upsert(db, new() { SourceId = "device", OwnerGroup = "A" });
        await using var first = await factory.CreateDbContextAsync(Ct);
        await using var second = await factory.CreateDbContextAsync(Ct);
        // Both production endpoint calls load the same tracked original version.
        await first.Sources.LoadAsync(Ct); await second.Sources.LoadAsync(Ct);
        var results = await Task.WhenAll(Upsert(first, new() { SourceId = "device", OwnerGroup = "B" }),
            Upsert(second, new() { SourceId = "device", OwnerGroup = "C" }));
        Assert.Equal(new[] { 200, 409 }, results.Select(Status).Order().ToArray());
        await using var check = await factory.CreateDbContextAsync(Ct);
        var rows = await check.SourceOwnershipHistory.OrderBy(h => h.Revision).ToArrayAsync(Ct);
        Assert.Equal(2, rows.Length); Assert.True(rows[1].Revision > rows[0].Revision);
        Assert.Equal(rows[1].EffectiveFromNano, rows[0].EffectiveToNano);
        Assert.Null(rows[1].EffectiveToNano);
        Assert.Equal(rows[1].OwnerGroup, (await check.Sources.SingleAsync(Ct)).OwnerGroup);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Bootstrap_does_not_invent_preexisting_history()
    {
        // A dedicated real DB starts at the previously deployed migration.
        var name = "history_bootstrap_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^history_bootstrap_[a-f0-9]{32}$", name);
        var original = new ControlPlaneFactory(stack.PostgresConnectionString);
        await using var admin = original.CreateDbContext();
#pragma warning disable EF1003 // Identifier is exclusively the verified generated hex name above, not a SQL value.
        await admin.Database.ExecuteSqlRawAsync("CREATE DATABASE " + name, Ct);
#pragma warning restore EF1003
        var connection = new NpgsqlConnectionStringBuilder(stack.PostgresConnectionString) { Database = name }.ConnectionString;
        try
        {
            var factory = new ControlPlaneFactory(connection);
            await using var db = factory.CreateDbContext();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260915082750_AddRcaModelBoundary", Ct);
            // Old schema/write shape: cannot invoke the new history hook yet.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO bizigo.sources
                (source_id,owner_group,enabled,encoding,source_class,vendor,product,upstream,vlan,firmware,created_at,updated_at)
                VALUES ('old','B',true,'auto','default','','','','','','2000-01-01T00:00:00Z',now())
                """, Ct);
            var before = (decimal)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
            await migrator.MigrateAsync(cancellationToken: Ct);
            var row = Assert.Single(await db.SourceOwnershipHistory.ToArrayAsync(Ct));
            Assert.True(row.EffectiveFromNano >= before - 1000);
            var resolved = await new HistoricalTelemetryOwners(factory).ResolveAsync([new("leaf", 100, ["old"])], Ct);
            Assert.Equal("_unassigned", Assert.Single(resolved).OwnerGroup);
        }
        finally
        {
#pragma warning disable EF1003 // Same owned, verified database identifier; identifiers cannot be value parameters.
            await admin.Database.ExecuteSqlRawAsync("DROP DATABASE " + name + " WITH (FORCE)", Ct);
#pragma warning restore EF1003
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Alias_disable_cache_restart_use_committed_history()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        { db.HistoryClock = clock; await Upsert(db, new() { SourceId = "device", OwnerGroup = "A", Hostname = "old" }); }
        var directory = new SourceDirectory(factory); await directory.RefreshAsync(Ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        { db.HistoryClock = clock; await Upsert(db, new() { SourceId = "device", OwnerGroup = "B", Hostname = "new" }); }
        var transfer = checked((ulong)(clock.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
        // The historical resolver reads committed revisions, even while the
        // dispatcher's unrelated current-source cache has not been refreshed.
        var result = await directory.HistoricalOwners.ResolveAsync([new("old", transfer - 1, ["old"]), new("new", transfer, ["new"])], Ct);
        Assert.Equal(new[] { "A", "B" }, result.Select(r => r.OwnerGroup));
        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        { db.HistoryClock = clock; await Upsert(db, new() { SourceId = "device", OwnerGroup = "B", Hostname = "new", Enabled = false }); }
        var disabled = transfer + 1000000000;
        var restarted = new SourceDirectory(new ControlPlaneFactory(stack.PostgresConnectionString));
        var after = await restarted.HistoricalOwners.ResolveAsync([new("leaf", disabled, ["new"])], Ct);
        Assert.Equal("disabled", Assert.Single(after).Reason);
    }
}
