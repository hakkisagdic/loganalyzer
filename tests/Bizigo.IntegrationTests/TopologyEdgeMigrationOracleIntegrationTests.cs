using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>Upgrade from the actual predecessor schemas in private databases.
/// These tests intentionally do not use the already-migrated shared fixture.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyEdgeMigrationOracleIntegrationTests(DevStackFixture stack)
{
    private const string BeforeDeclared = "20261002224707_AddTopologyOwnerHistory";
    private const string Declared = "20261002231400_AddTopologyDeclaredEdges";
    private const string ConflictContext = "0011_topology_conflict_context";

    [Fact]
    public async Task D08_Postgres_previous_schema_upgrade_repeat_and_downgrade_preserve_edge_history()
    {
        var token = TestContext.Current.CancellationToken;
        var database = "s05_d08_pg_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(stack.PostgresConnectionString) { Database = database }.ConnectionString;
        await using var admin = new NpgsqlConnection(stack.PostgresConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            var factory = new ControlPlaneFactory(connection);
            await using (var old = factory.CreateDbContext())
            {
                await old.GetService<IMigrator>().MigrateAsync(BeforeDeclared, token);
                Assert.Null(await ScalarAsync(connection,
                    "SELECT to_regclass('bizigo.topology_edges_declared')::text", token));
                old.Sources.AddRange(new SourceEntity { SourceId = "d08-old-A", OwnerGroup = "A" },
                    new SourceEntity { SourceId = "d08-old-B", OwnerGroup = "A" });
                await old.SaveChangesAsync(token);
                Assert.Equal(2, await old.TopologyNodes.CountAsync(token));
            }

            await using (var upgrade = factory.CreateDbContext())
            {
                var migrator = upgrade.GetService<IMigrator>();
                await migrator.MigrateAsync(Declared, token);
                Assert.Equal("bizigo.topology_edges_declared", await ScalarAsync(connection,
                    "SELECT to_regclass('bizigo.topology_edges_declared')::text", token));
                await migrator.MigrateAsync(Declared, token);
                Assert.Equal(1, (await upgrade.Database.GetAppliedMigrationsAsync(token)).Count(id => id == Declared));
            }

            string from;
            string to;
            await using (var db = factory.CreateDbContext())
            {
                from = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "d08-old-A", token)).Id;
                to = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "d08-old-B", token)).Id;
            }
            var scope = AccessScope.ForGroups("d08-migration-admin", ["A"]);
            var edge = await new TopologyEdgeRegistry(factory).CreateAsync(scope, true,
                new(from, to, TopologyEdgeRelations.DependsOn), token);
            Assert.Equal(201, edge.Status);
            var edgeId = edge.Edge!.Id;

            await using (var downgrade = factory.CreateDbContext())
            {
                var error = await Assert.ThrowsAsync<NotSupportedException>(async () =>
                    await downgrade.GetService<IMigrator>().MigrateAsync(BeforeDeclared, token));
                Assert.Contains("archival downgrade", error.Message, StringComparison.Ordinal);
            }
            await using var preserved = factory.CreateDbContext();
            Assert.Equal(2, await preserved.Sources.CountAsync(token));
            Assert.Equal(2, await preserved.TopologyNodes.CountAsync(token));
            Assert.Equal(1, await preserved.TopologyDeclaredEdges.CountAsync(e => e.Id == edgeId, token));
            Assert.Equal(1, await preserved.TopologyDeclaredEdgeHistory.CountAsync(h => h.EdgeId == edgeId, token));
            Assert.Equal(1, (await preserved.Database.GetAppliedMigrationsAsync(token)).Count(id => id == Declared));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task D08_ClickHouse_previous_schema_upgrade_repeat_and_physical_downgrade_fail_closed()
    {
        var token = TestContext.Current.CancellationToken;
        var database = "s05_d08_ch_" + Guid.NewGuid().ToString("N");
        var connection = stack.ClickHouseConnectionString.Replace("Database=bizigo", "Database=" + database,
            StringComparison.Ordinal);
        Assert.NotEqual(stack.ClickHouseConnectionString, connection);
        var oldDirectory = Path.Combine(Path.GetTempPath(), database + "_old");
        Directory.CreateDirectory(oldDirectory);
        await stack.QueryScalarAsync(stack.ClickHouseConnectionString, $"CREATE DATABASE {database}", token);
        try
        {
            var fullDirectory = DevStackSetup.RepoPath("db/clickhouse");
            var newMigration = Path.Combine(fullDirectory, ConflictContext + ".sql");
            Assert.True(File.Exists(newMigration), "The additive conflict-context migration must ship with the release.");
            foreach (var file in Directory.GetFiles(fullDirectory, "*.sql")
                .Where(file => string.CompareOrdinal(Path.GetFileName(file), "0011_") < 0))
                File.Copy(file, Path.Combine(oldDirectory, Path.GetFileName(file)));

            var context = new ClickHouseContext(new ClickHouseOptions { ConnectionString = connection });
            var migrator = new ClickHouseMigrator(context);
            var before = await migrator.MigrateAsync(oldDirectory, token);
            Assert.Contains("0010_topology_projection_batches", before.Applied);
            Assert.DoesNotContain(await migrator.GetAppliedAsync(token), row => row.Version == ConflictContext);
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.columns WHERE database = currentDatabase() " +
                "AND table = 'topology_span_conflicts' AND name = 'candidate_context_json'", token));

            var anchor = new string('a', 64);
            var first = new string('b', 64);
            var conflict = new string('c', 64);
            await stack.QueryScalarAsync(connection, $"""
                INSERT INTO topology_span_conflicts
                    (semantic_anchor, first_fingerprint, conflicting_fingerprint, publication_seq)
                VALUES ('{anchor}', '{first}', '{conflict}', 1)
                """, token);
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM topology_span_conflicts", token));

            var upgraded = await migrator.MigrateAsync(fullDirectory, token);
            Assert.Contains(ConflictContext, upgraded.Applied);
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.columns WHERE database = currentDatabase() " +
                "AND table = 'topology_span_conflicts' AND name = 'candidate_context_json'", token));
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM topology_span_conflicts WHERE candidate_context_json = ''", token));
            var repeated = await migrator.MigrateAsync(fullDirectory, token);
            Assert.Empty(repeated.Applied);
            Assert.Contains(ConflictContext, repeated.AlreadyApplied);
            Assert.Single((await migrator.GetAppliedAsync(token)).Where(row => row.Version == ConflictContext));
            Assert.Empty(await new TopologyObservedSnapshotReader(context).ReadAsync(0, token));

            var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
            var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(factory,
                new TopologyObservedSnapshotReader(context),
                new TopologyPublicationFence(new TopologyPublicationRevisionSource(factory,
                    new TopologyPublicationWatermarkReader(context)))));
            var query = new TopologyEdgeQuery(TopologyIdentity.Nano(DateTimeOffset.UtcNow));
            var scope = AccessScope.ForGroups("d08-observed-reader", ["A"]);
            Assert.Empty((await graph.SearchEdgesAsync(query, scope, token)).Items);

            // Simulate a stale/downgraded physical schema while the migration
            // ledger still claims success. A checksum-only repeat cannot repair
            // this; the production observed read must fail, never return Empty.
            await stack.QueryScalarAsync(connection,
                "ALTER TABLE topology_span_conflicts DROP COLUMN candidate_context_json", token);
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM topology_span_conflicts", token));
            Assert.Single((await migrator.GetAppliedAsync(token)).Where(row => row.Version == ConflictContext));
            await Assert.ThrowsAsync<InvalidOperationException>(() => stack.QueryScalarAsync(connection,
                "SELECT candidate_context_json FROM topology_span_conflicts LIMIT 1", token));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new TopologyObservedSnapshotReader(context).ReadAsync(0, token));
            await Assert.ThrowsAnyAsync<Exception>(() => graph.SearchEdgesAsync(query, scope, token));
        }
        finally
        {
            await stack.QueryScalarAsync(stack.ClickHouseConnectionString,
                $"DROP DATABASE IF EXISTS {database} SYNC", CancellationToken.None);
            Directory.Delete(oldDirectory, recursive: true);
        }
    }

    private static async Task<string?> ScalarAsync(string connection, string sql, CancellationToken token)
    {
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, db);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : value.ToString();
    }
}
