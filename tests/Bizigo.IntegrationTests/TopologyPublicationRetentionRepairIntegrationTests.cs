using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Real PG receipts + CH immutable manifests/copy/EXCHANGE; Planner-owned Docker run.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyPublicationRetentionRepairIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_0014_initialization_certifies_four_live_tables_and_downgrade_fails_closed()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        var runner = new TopologyPublicationRepairRunner(factory, storage, gate);

        var first = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, first.Status);
        Assert.Equal(1L, first.Generation);
        var stamp = await gate.RequireReadyAsync(Ct);
        Assert.Equal(first.Generation!.Value, stamp.Generation);
        var repeated = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, repeated.Status);
        Assert.Equal(first.Generation, repeated.Generation);
        Assert.Equal(stamp, await gate.RequireReadyAsync(Ct));

        var migration = await new ClickHouseMigrator(storage)
            .MigrateAsync(DevStackSetup.RepoPath("db/clickhouse"), Ct);
        Assert.Contains("0014_topology_publication_versions", migration.AlreadyApplied);
        Assert.Equal(stamp, await gate.RequireReadyAsync(Ct));

        await ExecuteAsync(storage, "DROP TABLE topology_parent_resolution", Ct);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(
            () => gate.RequireReadyAsync(Ct));
        var afterDowngrade = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.RepairIncomplete, afterDowngrade.Status);
        Assert.Equal(1L, afterDowngrade.Generation);
    }

    [Fact]
    public async Task Tampered_non_ttl_lifecycle_template_is_not_certified_on_empty_install()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        await ExecuteAsync(storage, "ALTER TABLE topology_edge_lifecycle_v0014_template "
            + "MODIFY TTL toDateTime(intDiv(last_seen, 1000000000))", Ct);
        var runner = new TopologyPublicationRepairRunner(factory, storage, gate);
        var result = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.RepairIncomplete, result.Status);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(
            () => gate.RequireReadyAsync(Ct));
    }

    [Fact]
    public async Task Ready_certificate_cannot_be_reused_for_another_clickhouse_database()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var firstStorage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var firstGate = new TopologyObservedRepairReadiness(factory, firstStorage);
        Assert.Equal(TopologyRepairInitializationStatus.Ready,
            (await new TopologyPublicationRepairRunner(factory, firstStorage, firstGate)
                .InitializeAsync(TopologyRepairStartMode.Startup, Ct)).Status);

        using var differentStorage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        var wrongGate = new TopologyObservedRepairReadiness(factory, differentStorage);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(
            () => wrongGate.RequireReadyAsync(Ct));
        var wrongStartup = await new TopologyPublicationRepairRunner(factory, differentStorage, wrongGate)
            .InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.RepairIncomplete, wrongStartup.Status);
        Assert.Equal(1L, wrongStartup.Generation);
        Assert.Equal(1L, (await firstGate.RequireReadyAsync(Ct)).Generation);
    }

    [Fact]
    public async Task Interrupted_first_exchange_resumes_same_bound_generation_with_new_copy_attempt()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        var inspector = new TopologyRepairSchemaInspector(storage);
        var before = (await inspector.ReadCanonicalAsync(Ct)).ToDictionary(table => table.Name,
            table => table.Uuid, StringComparer.Ordinal);
        var checkpoint = new FailAfterFirstExchange();
        var first = await new TopologyPublicationRepairRunner(factory, storage, gate,
            checkpoint).InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.RepairIncomplete, first.Status);
        Assert.Equal(1, checkpoint.ReachedCount);
        Assert.Equal(0, checkpoint.LastTableIndex);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(
            () => gate.RequireReadyAsync(Ct));

        var interrupted = (await inspector.ReadCanonicalAsync(Ct)).ToDictionary(table => table.Name,
            table => table.Uuid, StringComparer.Ordinal);
        Assert.NotEqual(before["topology_edges_observed"], interrupted["topology_edges_observed"]);
        foreach (var untouched in TopologyRepairSchemaInspector.CanonicalNames.Skip(1))
            Assert.Equal(before[untouched], interrupted[untouched]);
        string failedAttempt;
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var phase = await db.Database.SqlQueryRaw<string>(
                "SELECT phase AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1").SingleAsync(Ct);
            var generation = await db.Database.SqlQueryRaw<long>(
                "SELECT generation AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1").SingleAsync(Ct);
            var certificateCount = await db.Database.SqlQueryRaw<long>(
                "SELECT count(*) AS \"Value\" FROM bizigo.topology_repair_state "
                + "WHERE id=1 AND certificate_digest IS NOT NULL").SingleAsync(Ct);
            failedAttempt = await db.Database.SqlQueryRaw<string>(
                "SELECT copy_attempt_id::text AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1")
                .SingleAsync(Ct);
            Assert.Equal("Repairing", phase);
            Assert.Equal(1L, generation);
            Assert.Equal(0L, certificateCount);
        }

        // A new runner stands in for the restarted process. The first copy's
        // UUID is durable in PG; the original remains retained for audit.
        var resumed = await new TopologyPublicationRepairRunner(factory, storage, gate)
            .InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, resumed.Status);
        Assert.Equal(1L, resumed.Generation);
        Assert.Equal(1L, (await gate.RequireReadyAsync(Ct)).Generation);
        var complete = (await inspector.ReadCanonicalAsync(Ct)).ToDictionary(table => table.Name,
            table => table.Uuid, StringComparer.Ordinal);
        foreach (var name in TopologyRepairSchemaInspector.CanonicalNames)
            Assert.NotEqual(interrupted[name], complete[name]);
        await using var db = await factory.CreateDbContextAsync(Ct);
        var successfulAttempt = await db.Database.SqlQueryRaw<string>(
            "SELECT copy_attempt_id::text AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1")
            .SingleAsync(Ct);
        Assert.NotEqual(failedAttempt, successfulAttempt);
        var attemptCount = await db.Database.SqlQueryRaw<int>(
            "SELECT jsonb_array_length(allowed_copy_identity_json -> 'topology_edges_observed') "
            + "AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1").SingleAsync(Ct);
        Assert.Equal(3, attemptCount); // old canonical + failed copy + successful copy
    }

    [Fact]
    public async Task Missing_committed_manifest_never_becomes_ready_after_operator_drain()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        var runner = new TopologyPublicationRepairRunner(factory, storage, gate);
        var key = new string('a', 64);

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE bizigo.topology_read_state
                SET published_sequence = 1, epoch = epoch + 1 WHERE id = 1
                """, Ct));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bizigo.topology_publication_receipts(publication_key, publication_sequence)
                VALUES ({key}, 1)
                """, Ct);
        }
        await new TopologyPublicationWatermarkWriter(
            new TopologyPublicationWatermarkReader(storage), storage).CommitAsync(1, Ct);

        var startup = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.MaintenanceRequired, startup.Status);
        Assert.Equal(1L, await runner.RecordOperatorDrainAsync("integration-operator",
            "Old projector processes drained and kept stopped for this isolated repair.",
            DateTimeOffset.UtcNow.AddMinutes(10), Ct));
        var resumed = await runner.InitializeAsync(TopologyRepairStartMode.OperatorResume, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.RepairIncomplete, resumed.Status);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(
            () => gate.RequireReadyAsync(Ct));
        await using var inspect = await factory.CreateDbContextAsync(Ct);
        var phase = await inspect.Database.SqlQueryRaw<string>(
            "SELECT phase AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1").SingleAsync(Ct);
        Assert.Equal("Repairing", phase);
    }

    [Fact]
    public async Task Operator_rebuild_uses_frozen_manifest_to_restore_missing_physical_proof()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        await AssertUninitializedAsync(factory);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        var runner = new TopologyPublicationRepairRunner(factory, storage, gate);
        Assert.Equal(TopologyRepairInitializationStatus.Ready,
            (await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct)).Status);

        var parentNode = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        var childNode = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        var start = checked((ulong)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
        var trace = Guid.NewGuid().ToString("N");
        var parent = Span(trace, Guid.NewGuid(), "parent", "0011223344556677", "", parentNode, start);
        var child = Span(trace, Guid.NewGuid(), "child", "8899aabbccddeeff",
            "0011223344556677", childNode, start + 1000);
        var edge = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        var watermark = new TopologyPublicationWatermarkReader(storage);
        var coordinator = new TopologyPublicationCoordinator(factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, storage));
        var projector = new TopologyObservedProjector(storage, coordinator.PublishAsync,
            readPendingKey: coordinator.ReadPendingKeyAsync);
        await new TelemetryWriter(storage, new HistoricalTelemetryOwners(factory), projector)
            .WriteAsync([parent, child], Ct);
        Assert.Equal(1UL, await watermark.ReadAsync(Ct));
        Assert.Equal(1UL, await CountEdgeAsync(storage, edge.EdgeId, Ct));

        // A physical-row loss cannot be repaired from current owner/node
        // inventory. Explicit next-generation attestation invokes a fresh
        // copy from the original immutable publication manifest.
        await using (var connection = new ClickHouseConnection(storage.Options.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE topology_edges_observed DELETE WHERE edge_id = {edge:String} SETTINGS mutations_sync = 2";
            command.AddParameter("edge", edge.EdgeId);
            await command.ExecuteNonQueryAsync(Ct);
        }
        Assert.Equal(0UL, await CountEdgeAsync(storage, edge.EdgeId, Ct));
        Assert.Equal(2L, await runner.RecordOperatorDrainAsync("integration-operator",
            "Old projector processes drained and kept stopped through copy and certificate.",
            DateTimeOffset.UtcNow.AddMinutes(10), Ct));
        var result = await runner.InitializeAsync(TopologyRepairStartMode.OperatorResume, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, result.Status);
        Assert.Equal(2L, result.Generation);
        Assert.Equal(2L, (await gate.RequireReadyAsync(Ct)).Generation);
        Assert.Equal(1UL, await CountEdgeAsync(storage, edge.EdgeId, Ct));
        Assert.Equal(1UL, await watermark.ReadAsync(Ct));
        await ExecuteAsync(storage, "OPTIMIZE TABLE topology_edges_observed FINAL", Ct);
        Assert.Equal(1UL, await CountEdgeAsync(storage, edge.EdgeId, Ct));
        Assert.Equal(TopologyRepairInitializationStatus.Ready,
            (await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct)).Status);
    }

    private static async Task<ulong> CountEdgeAsync(ClickHouseContext storage, string edgeId,
        CancellationToken token)
    {
        await using var connection = new ClickHouseConnection(storage.Options.ConnectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count() FROM topology_edges_observed FINAL WHERE edge_id = {edge:String}";
        command.AddParameter("edge", edgeId);
        return Convert.ToUInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task AssertUninitializedAsync(IDbContextFactory<ControlPlaneDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(Ct);
        var phase = await db.Database.SqlQueryRaw<string>(
            "SELECT phase AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1").SingleAsync(Ct);
        var readStateRows = await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM bizigo.topology_read_state WHERE id=1").SingleAsync(Ct);
        var attestationRows = await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM bizigo.topology_repair_attestations").SingleAsync(Ct);
        Assert.Equal("Uninitialized", phase);
        Assert.Equal(1L, readStateRows);
        Assert.Equal(0L, attestationRows);
    }

    private static async Task ExecuteAsync(ClickHouseContext storage, string sql, CancellationToken token)
    {
        await using var connection = new ClickHouseConnection(storage.Options.ConnectionString);
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static TelemetryRecord Span(string trace, Guid envelope, string leaf, string spanId,
        string parentSpanId, string node, ulong start)
    {
        var span = "{\"traceId\":\"" + trace + "\",\"spanId\":\"" + spanId
            + "\",\"parentSpanId\":\"" + parentSpanId + "\",\"startTimeUnixNano\":\""
            + start.ToString(CultureInfo.InvariantCulture) + "\",\"endTimeUnixNano\":\""
            + (start + 1).ToString(CultureInfo.InvariantCulture) + "\",\"name\":\"op\",\"kind\":2}";
        var owner = new TelemetryOwnerBinding(leaf, "SA", "A", 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, "SA", "A", 7,
            node, null, 5, null, 9, "display", "Resolved");
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        return new(1, envelope, envelope.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), owner, start, "op", "svc", "Client", "", 0, false,
            trace, spanId, 1, new string('c', 64), Json("{}"), Json("{}"), "", "", null, Json(span))
        { Topology = topology, TopologyBindingsSha256 = new string('d', 64) };
    }

    private sealed class FailAfterFirstExchange : ITopologyRepairCheckpoints
    {
        public int ReachedCount { get; private set; }
        public int LastTableIndex { get; private set; } = -1;

        public Task ReachAsync(string stage, int tableIndex, CancellationToken cancellationToken)
        {
            Assert.Equal("after-canonical-exchange", stage);
            ReachedCount++;
            LastTableIndex = tableIndex;
            if (tableIndex == 0) throw new IOException("Simulated process loss after first canonical exchange.");
            return Task.CompletedTask;
        }
    }
}
