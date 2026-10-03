using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>Upgrade from actual predecessor schemas in private PG/CH databases.
/// The CH public-read control uses the serial collection's reset PG fixture.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyEdgeMigrationOracleIntegrationTests(DevStackFixture stack)
{
    private const string BeforeDeclared = "20261002224707_AddTopologyOwnerHistory";
    private const string Declared = "20261002231400_AddTopologyDeclaredEdges";
    private const string ConflictContext = "0011_topology_conflict_context";
    private const string ParentResolution = "0012_topology_parent_resolution";

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
            var parentMigration = Path.Combine(fullDirectory, ParentResolution + ".sql");
            Assert.True(File.Exists(parentMigration), "The additive parent-resolution migration must ship with the release.");
            foreach (var file in Directory.GetFiles(fullDirectory, "*.sql")
                .Where(file => string.CompareOrdinal(Path.GetFileName(file), "0011_") < 0))
                File.Copy(file, Path.Combine(oldDirectory, Path.GetFileName(file)));

            var context = new ClickHouseContext(new ClickHouseOptions { ConnectionString = connection });
            var migrator = new ClickHouseMigrator(context);
            var before = await migrator.MigrateAsync(oldDirectory, token);
            Assert.Contains("0010_topology_projection_batches", before.Applied);
            Assert.DoesNotContain(await migrator.GetAppliedAsync(token), row => row.Version == ConflictContext);
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                $"SELECT count() FROM schema_migrations WHERE version = '{ConflictContext}'", token));
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                $"SELECT count() FROM schema_migrations WHERE version = '{ParentResolution}'", token));
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.columns WHERE database = currentDatabase() " +
                "AND table = 'topology_span_conflicts' AND name = 'candidate_context_json'", token));
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.tables WHERE database = currentDatabase() " +
                "AND name = 'topology_parent_resolution'", token));

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
            await AssertMarkerAsync(connection, anchor, first, conflict, token);

            var upgraded = await migrator.MigrateAsync(fullDirectory, token);
            Assert.Contains(ConflictContext, upgraded.Applied);
            Assert.Contains(ParentResolution, upgraded.Applied);
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.columns WHERE database = currentDatabase() " +
                "AND table = 'topology_span_conflicts' AND name = 'candidate_context_json'", token));
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.tables WHERE database = currentDatabase() " +
                "AND name = 'topology_parent_resolution'", token));
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM topology_span_conflicts WHERE candidate_context_json = ''", token));
            await AssertMarkerAsync(connection, anchor, first, conflict, token);
            await AssertRawLedgerAsync(connection, ConflictContext, newMigration, token);
            await AssertRawLedgerAsync(connection, ParentResolution, parentMigration, token);
            var repeated = await migrator.MigrateAsync(fullDirectory, token);
            Assert.Empty(repeated.Applied);
            Assert.Contains(ConflictContext, repeated.AlreadyApplied);
            Assert.Contains(ParentResolution, repeated.AlreadyApplied);
            await AssertRawLedgerAsync(connection, ConflictContext, newMigration, token);
            await AssertRawLedgerAsync(connection, ParentResolution, parentMigration, token);
            await AssertMarkerAsync(connection, anchor, first, conflict, token);
            var observedReader = new TopologyObservedSnapshotReader(context);
            Assert.Empty(await observedReader.ReadAsync(0, token));

            var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
            var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(factory,
                observedReader,
                new TopologyPublicationFence(new TopologyPublicationRevisionSource(factory,
                    new TopologyPublicationWatermarkReader(context)))));
            var query = new TopologyEdgeQuery(TopologyIdentity.Nano(DateTimeOffset.UtcNow.AddSeconds(1)));
            var scope = AccessScope.ForGroups("d08-observed-reader", ["A"]);
            // Marker seq=1 is still uncommitted here. This is only a negative
            // control, not evidence that the migrated legacy marker is usable.
            Assert.Empty((await graph.SearchEdgesAsync(query with
                { Provenance = TopologyProvenance.Observed }, scope, token)).Items);

            await using (var seed = await factory.CreateDbContextAsync(token))
            {
                seed.Sources.AddRange(new SourceEntity { SourceId = "d08-declared-A", OwnerGroup = "A" },
                    new SourceEntity { SourceId = "d08-declared-B", OwnerGroup = "A" });
                await seed.SaveChangesAsync(token);
            }
            string from;
            string to;
            await using (var lookup = await factory.CreateDbContextAsync(token))
            {
                from = (await lookup.TopologyNodes.SingleAsync(n => n.SourceId == "d08-declared-A", token)).Id;
                to = (await lookup.TopologyNodes.SingleAsync(n => n.SourceId == "d08-declared-B", token)).Id;
            }
            var declared = await new TopologyEdgeRegistry(factory).CreateAsync(scope, true,
                new(from, to, TopologyEdgeRelations.DependsOn), token);
            Assert.Equal(201, declared.Status);
            query = new TopologyEdgeQuery(TopologyIdentity.Nano(DateTimeOffset.UtcNow.AddSeconds(2)));

            // Commit the exact marker sequence in both stores so production
            // reads cannot filter it away as an uncommitted row.
            await stack.QueryScalarAsync(connection,
                "INSERT INTO topology_publication_watermark (id, committed_sequence) VALUES (1, 1)", token);
            await using (var publish = await factory.CreateDbContextAsync(token))
            {
                (await publish.TopologyReadState.SingleAsync(token)).PublishedSequence = 1;
                await publish.SaveChangesAsync(token);
            }
            await using (var published = await factory.CreateDbContextAsync(token))
                Assert.Equal(1, (await published.TopologyReadState.SingleAsync(token)).PublishedSequence);
            Assert.Equal("1", await stack.QueryScalarAsync(connection,
                "SELECT max(committed_sequence) FROM topology_publication_watermark FINAL WHERE id = 1", token));
            var readiness = await ReadReadinessAsync(observedReader, 1, token);
            Assert.False(readiness.Usable);
            Assert.Equal(1, readiness.UnattributedMarkers);
            var migrationRequired = await Assert.ThrowsAnyAsync<IOException>(() =>
                graph.SearchEdgesAsync(query, scope, token));
            Assert.Equal("TopologyObservedMigrationRequiredException", migrationRequired.GetType().Name);
            var declaredOnly = await graph.SearchEdgesAsync(query with
                { Provenance = TopologyProvenance.Declared }, scope, token);
            Assert.Equal(declared.Edge!.Id.ToString("D"), Assert.Single(declaredOnly.Items).Id);
            await AssertMarkerAsync(connection, anchor, first, conflict, token);

            // Simulate a stale/downgraded physical schema while the migration
            // ledger still claims success. A checksum-only repeat cannot repair
            // this; the production observed read must fail, never return Empty.
            await stack.QueryScalarAsync(connection,
                "ALTER TABLE topology_span_conflicts DROP COLUMN candidate_context_json", token);
            await AssertMarkerAsync(connection, anchor, first, conflict, token);
            await AssertRawLedgerAsync(connection, ConflictContext, newMigration, token);
            await AssertRawLedgerAsync(connection, ParentResolution, parentMigration, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stack.QueryScalarAsync(connection,
                "SELECT candidate_context_json FROM topology_span_conflicts LIMIT 1", token));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                observedReader.ReadAsync(0, token));
            await Assert.ThrowsAnyAsync<Exception>(() => graph.SearchEdgesAsync(query, scope, token));

            // Restore 0011, then independently remove 0012. Both ledger rows
            // remain, but a public observed read must not invent MissingParent
            // or return an apparently complete empty graph.
            await stack.QueryScalarAsync(connection,
                "ALTER TABLE topology_span_conflicts ADD COLUMN candidate_context_json String DEFAULT ''", token);
            Assert.False((await ReadReadinessAsync(observedReader, 1, token)).Usable);
            await stack.QueryScalarAsync(connection, "DROP TABLE topology_parent_resolution SYNC", token);
            Assert.Equal("0", await stack.QueryScalarAsync(connection,
                "SELECT count() FROM system.tables WHERE database = currentDatabase() " +
                "AND name = 'topology_parent_resolution'", token));
            await AssertMarkerAsync(connection, anchor, first, conflict, token);
            await AssertRawLedgerAsync(connection, ConflictContext, newMigration, token);
            await AssertRawLedgerAsync(connection, ParentResolution, parentMigration, token);
            await Assert.ThrowsAnyAsync<Exception>(() => observedReader.ReadAsync(0, token));
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

    private async Task AssertMarkerAsync(string connection, string anchor, string first,
        string conflict, CancellationToken token) => Assert.Equal("1", await stack.QueryScalarAsync(connection, $"""
        SELECT count() FROM topology_span_conflicts
        WHERE semantic_anchor = '{anchor}' AND first_fingerprint = '{first}'
          AND conflicting_fingerprint = '{conflict}' AND publication_seq = 1
        """, token));

    private async Task AssertRawLedgerAsync(string connection, string version, string file,
        CancellationToken token)
    {
        var normalized = (await File.ReadAllTextAsync(file, token)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        Assert.Equal("1", await stack.QueryScalarAsync(connection,
            $"SELECT count() FROM schema_migrations WHERE version = '{version}'", token));
        Assert.Equal(checksum, await stack.QueryScalarAsync(connection,
            $"SELECT checksum FROM schema_migrations WHERE version = '{version}' LIMIT 1", token));
    }

    // This isolated branch predates Q's additive readiness API. Reflection
    // keeps the test-only commit cherry-pickable, but absence of the production
    // API fails the named case instead of silently skipping the assertion.
    private static async Task<(bool Usable, int UnattributedMarkers)> ReadReadinessAsync(
        TopologyObservedSnapshotReader reader, ulong watermark, CancellationToken token)
    {
        var method = reader.GetType().GetMethod("CheckReadinessAsync")
            ?? throw new InvalidOperationException("Production observed readiness API is missing.");
        var task = (Task)method.Invoke(reader, [watermark, token])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var type = result.GetType();
        return ((bool)type.GetProperty("Usable")!.GetValue(result)!,
            (int)type.GetProperty("UnattributedMarkers")!.GetValue(result)!);
    }
}
