using System.Diagnostics;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>A source transfer and a two-endpoint edge mutation must serialize on
/// the production inventory lock; failed audit writes must roll back the whole edge mutation.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyEdgeConcurrentOwnerOracleIntegrationTests(DevStackFixture stack)
{
    [Fact]
    public async Task D05_Concurrent_endpoint_transfer_and_edge_update_keep_scope_history_and_audit_atomic()
    {
        var token = TestContext.Current.CancellationToken;
        var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var fromSource = "d05-from-" + suffix;
        var toSource = "d05-to-" + suffix;
        var scopeA = AccessScope.ForGroups("d05-admin-A", ["A"]);
        var scopeAB = AccessScope.ForGroups("d05-admin-AB", ["A", "B"]);
        await using (var seed = await factory.CreateDbContextAsync(token))
        {
            seed.Sources.AddRange(new SourceEntity { SourceId = fromSource, OwnerGroup = "A" },
                new SourceEntity { SourceId = toSource, OwnerGroup = "A" });
            await seed.SaveChangesAsync(token);
        }

        string from;
        string to;
        await using (var lookup = await factory.CreateDbContextAsync(token))
        {
            from = (await lookup.TopologyNodes.SingleAsync(n => n.SourceId == fromSource, token)).Id;
            to = (await lookup.TopologyNodes.SingleAsync(n => n.SourceId == toSource, token)).Id;
        }
        var registry = new TopologyEdgeRegistry(factory);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        var created = await registry.CreateAsync(scopeA, true, input, token);
        Assert.Equal(201, created.Status);
        var edgeId = created.Edge!.Id;

        // Hold the exact production inventory lock before releasing either
        // contender. Both must become visible as PG lock waiters while neither
        // can yet commit; task scheduling alone is not a concurrency oracle.
        await using var gate = new NpgsqlConnection(stack.PostgresConnectionString);
        await gate.OpenAsync(token);
        await using var gateTransaction = (NpgsqlTransaction)await gate.BeginTransactionAsync(token);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(735031)", gate, gateTransaction))
            await hold.ExecuteNonQueryAsync(token);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = Task.Run(async () =>
        {
            await start.Task;
            await using var db = await factory.CreateDbContextAsync(token);
            (await db.Sources.SingleAsync(s => s.SourceId == toSource, token)).OwnerGroup = "B";
            await db.SaveChangesAsync(token);
        }, token);
        var update = Task.Run(async () =>
        {
            await start.Task;
            return await registry.UpdateAsync(scopeA, true, edgeId,
                input with { Version = "1", Relation = TopologyEdgeRelations.ConnectsTo }, token);
        }, token);
        int blocked;
        try
        {
            start.SetResult();
            blocked = await WaitForInventoryLockWaitersAsync(stack.PostgresConnectionString,
                transfer, update, token);
        }
        finally
        {
            await gateTransaction.RollbackAsync(CancellationToken.None);
        }
        await Task.WhenAll(transfer, update);
        var updateResult = await update;
        Assert.Equal(2, blocked);
        Assert.Contains(updateResult.Status, new[] { 200, 403 });

        await using (var check = await factory.CreateDbContextAsync(token))
        {
            var current = await check.TopologyDeclaredEdges.AsNoTracking().SingleAsync(e => e.Id == edgeId, token);
            var expectedVersion = updateResult.Status == 200 ? 2 : 1;
            Assert.Equal((long)expectedVersion, current.Version);
            Assert.Equal(updateResult.Status == 200 ? TopologyEdgeRelations.ConnectsTo : TopologyEdgeRelations.DependsOn,
                current.Relation);
            Assert.Equal(expectedVersion, await check.TopologyDeclaredEdgeHistory.CountAsync(h => h.EdgeId == edgeId, token));
            Assert.Equal(expectedVersion, await check.AuditLog.CountAsync(a => a.Resource == edgeId.ToString() &&
                a.Action.StartsWith("topology.edge."), token));
            Assert.Equal("B", (await check.Sources.SingleAsync(s => s.SourceId == toSource, token)).OwnerGroup);
        }

        // Regardless of which contender won, A-only authority is now stale.
        var version = updateResult.Status == 200 ? "2" : "1";
        var nextRelation = updateResult.Status == 200
            ? TopologyEdgeRelations.DependsOn : TopologyEdgeRelations.ConnectsTo;
        Assert.Equal(403, (await registry.UpdateAsync(scopeA, true, edgeId,
            input with { Version = version }, token)).Status);

        await using var admin = await factory.CreateDbContextAsync(token);
        var beforeEpoch = (await admin.TopologyReadState.SingleAsync(token)).Epoch;
        var beforeHistory = await admin.TopologyDeclaredEdgeHistory.CountAsync(h => h.EdgeId == edgeId, token);
        var beforeAudit = await admin.AuditLog.CountAsync(a => a.Resource == edgeId.ToString(), token);
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bizigo.reject_d05_edge_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.action LIKE 'topology.edge.%' THEN RAISE EXCEPTION 'd05 audit rollback'; END IF;
                RETURN NEW; END $$;
            CREATE TRIGGER reject_d05_edge_audit BEFORE INSERT ON bizigo.audit_log
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_d05_edge_audit();
            """, token);
        try
        {
            Assert.Equal(503, (await registry.UpdateAsync(scopeAB, true, edgeId,
                input with { Version = version, Relation = nextRelation }, token)).Status);
            await using var check = await factory.CreateDbContextAsync(token);
            Assert.Equal(beforeEpoch, (await check.TopologyReadState.SingleAsync(token)).Epoch);
            Assert.Equal(long.Parse(version, System.Globalization.CultureInfo.InvariantCulture),
                (await check.TopologyDeclaredEdges.SingleAsync(e => e.Id == edgeId, token)).Version);
            Assert.Equal(beforeHistory, await check.TopologyDeclaredEdgeHistory.CountAsync(h => h.EdgeId == edgeId, token));
            Assert.Equal(beforeAudit, await check.AuditLog.CountAsync(a => a.Resource == edgeId.ToString(), token));
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_d05_edge_audit ON bizigo.audit_log; DROP FUNCTION bizigo.reject_d05_edge_audit();",
                CancellationToken.None);
        }

        Assert.Equal(200, (await registry.UpdateAsync(scopeAB, true, edgeId,
            input with { Version = version, Relation = nextRelation }, token)).Status);
        await using var final = await factory.CreateDbContextAsync(token);
        var saved = await final.TopologyDeclaredEdges.SingleAsync(e => e.Id == edgeId, token);
        Assert.Equal("A", saved.FromOwnerGroup);
        Assert.Equal("B", saved.ToOwnerGroup);
        Assert.Equal(nextRelation, saved.Relation);
    }

    private static async Task<int> WaitForInventoryLockWaitersAsync(string connection,
        Task transfer, Task update, CancellationToken token)
    {
        await using var probe = new NpgsqlConnection(connection);
        await probe.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)::int FROM pg_locks
            WHERE locktype = 'advisory' AND granted = false
              AND classid = 0::oid AND objid = 735031::oid AND objsubid = 1
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """, probe);
        var deadline = Stopwatch.StartNew();
        var observed = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            observed = (int)(await command.ExecuteScalarAsync(token) ?? 0);
            if (observed >= 2 || transfer.IsCompleted || update.IsCompleted) break;
            await Task.Delay(TimeSpan.FromMilliseconds(50), token);
        }
        return observed;
    }
}
