using System.Data;
using System.Data.Common;
using LogAnalyzer.Contracts;
using LogAnalyzer.ControlPlane;
using LogAnalyzer.Query;
using LogAnalyzer.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LogAnalyzer.IntegrationTests;

/// <summary>Actual EF command emitted by the production mapping reader, not a fixture SQL copy.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyMappingSqlIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Mapped_source_keyset_uses_production_SQL_limit_and_index()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var owner = "map-plan-" + suffix;
        var sourceId = "map-source-" + suffix;
        var scope = AccessScope.ForGroups("map-plan-actor", [owner]);
        await fixture.SourceAsync(sourceId, owner);
        string root;
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
            root = (await db.TopologyNodes.SingleAsync(node => node.SourceId == sourceId, Ct)).Id;
        var service = (await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "mapping-plan-service", owner, true, []), Ct)).Node;
        Assert.NotNull(service);
        var created = await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(root, service.Id, "contains"), Ct);
        Assert.Equal(201, created.Status);
        var secondService = (await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "mapping-plan-service-2", owner, true, []), Ct)).Node;
        Assert.NotNull(secondService);
        Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(root, secondService.Id, "contains"), Ct)).Status);
        var firstEdge = Assert.IsType<TopologyDeclaredEdgeVersion>(created.Edge);
        long firstRevision;
        await using (var db = await fixture.Factory.CreateDbContextAsync(Ct))
            firstRevision = (await db.TopologyDeclaredEdgeHistory.SingleAsync(
                history => history.EdgeId == firstEdge.Id, Ct)).Revision;

        var commands = new MappingCommandCapture();
        var readerFactory = new InterceptedFactory(stack.PostgresConnectionString, commands);
        var revisions = new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage));
        var reader = new TopologySourceTargetPageReader(readerFactory,
            new TopologyPublicationFence(revisions));
        var asOf = TopologyIdentity.Nano(DateTimeOffset.UtcNow.AddMinutes(1));
        var page = await reader.ReadPageAsync([sourceId], asOf, scope, 1, null, Ct);
        Assert.Equal(service.Id, Assert.Single(page.Items).Target?.NodeId);
        Assert.NotNull(page.Cursor); // exact full page: terminal needs another charged read
        Assert.Single(commands.Commands);
        var second = await reader.ReadPageAsync([sourceId], asOf, scope, 1, page.Cursor, Ct);
        Assert.Equal(secondService.Id, Assert.Single(second.Items).Target?.NodeId);
        Assert.NotNull(second.Cursor);
        Assert.Equal(3, commands.Commands.Count); // at most pageSize+1 raw seeks this page
        var terminal = await reader.ReadPageAsync([sourceId], asOf, scope, 1, second.Cursor, Ct);
        Assert.Equal(TopologySourceTargetStatus.Complete, Assert.Single(terminal.Items).FinalStatus);
        Assert.Null(terminal.Cursor);
        Assert.Equal(5, commands.Commands.Count);
        // service-1's empty child seek precedes this root continuation; the
        // keyset must advance past the first real history revision.
        var captured = commands.Commands[2];
        Assert.Contains(captured.Parameters, parameter =>
            parameter.Name.Contains("afterRevision", StringComparison.Ordinal)
            && Convert.ToInt64(parameter.Value, System.Globalization.CultureInfo.InvariantCulture)
                == firstRevision);
        Assert.Contains("LIMIT 1", captured.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("from_node_id", captured.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relation", captured.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("revision", captured.Sql, StringComparison.OrdinalIgnoreCase);

        // The shared Postgres keeps whatever planner statistics earlier tests
        // left. When autovacuum analysed the history table while only a few
        // rows were live, every index became a full scan and the optimizer
        // costed an unrelated owner/clock index lower, so this EXPLAIN never
        // named the keyset index (CI run 39493440254, after the class order
        // changed). Give it the production shape instead: one tombstoned
        // parent with many children under its own node, then ANALYZE, as the
        // declared-plan test in TopologyQueryIntegrationTests does.
        var distractor = (await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "mapping-plan-distractor", owner, true, []), Ct)).Node;
        Assert.NotNull(distractor);
        await SeedMappingPlanDistractorsAsync(fixture.Factory, distractor.Id, service.Id, asOf, Ct);

        await using var dbForPlan = await fixture.Factory.CreateDbContextAsync(Ct);
        await dbForPlan.Database.OpenConnectionAsync(Ct);
        var connection = dbForPlan.Database.GetDbConnection();
        try
        {
            await using (var disableSeqScan = connection.CreateCommand())
            {
                disableSeqScan.CommandText = "SET enable_seqscan=off";
                await disableSeqScan.ExecuteNonQueryAsync(Ct);
            }
            var indexed = false;
            var lines = new List<string>();
            foreach (var cmd in commands.Commands)
            {
                await using var explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN (FORMAT TEXT) "
                    + cmd.Sql.Replace("-- topology.mapping.next-edge", string.Empty,
                        StringComparison.Ordinal);
                foreach (var parameter in cmd.Parameters)
                {
                    var bound = explain.CreateParameter();
                    bound.ParameterName = parameter.Name;
                    bound.DbType = parameter.Type;
                    bound.Value = parameter.Value ?? DBNull.Value;
                    explain.Parameters.Add(bound);
                }
                var cmdLines = new List<string>();
                await using var plan = await explain.ExecuteReaderAsync(Ct);
                while (await plan.ReadAsync(Ct)) cmdLines.Add(plan.GetString(0));
                lines.AddRange(cmdLines);
                if (cmdLines.Any(line => line.Contains("ix_topology_edge_hist_from_relation_revision", StringComparison.Ordinal)))
                {
                    indexed = true;
                    break;
                }
            }
            // Never persist SQL parameter values or plan lines: either may
            // include hidden node/owner identifiers.
            TelemetryDbFixture.Evidence("topology-mapping-keyset-plan", new
            {
                Route = "topology.mapping.next-edge",
                LimitOne = true,
                UsesMappingIndex = indexed,
                ParameterNames = captured.Parameters.Select(static parameter => parameter.Name),
            });
            // Scan-node lines carry index names only; predicates (and their
            // bound values) are on the Cond/Filter lines and stay out.
            Assert.True(indexed, "Production mapping keyset index was not used in forced-index EXPLAIN: "
                + string.Join("; ", lines.Where(static line => line.Contains(" Scan", StringComparison.Ordinal))
                    .Select(static line => line.Split("  (cost=", 2)[0].Trim()).Distinct()));
        }
        finally
        {
            await using var reset = connection.CreateCommand();
            reset.CommandText = "RESET enable_seqscan";
            await reset.ExecuteNonQueryAsync(Ct);
            await dbForPlan.Database.CloseConnectionAsync();
        }
    }

    private static async Task SeedMappingPlanDistractorsAsync(IDbContextFactory<ControlPlaneDbContext> factory,
        string fromNodeId, string toNodeId, decimal asOf, CancellationToken cancellationToken)
    {
        // Perf-only, tombstoned rows of another owner: no reader can see them,
        // and the next fixture reset removes them with the rest of the history.
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO loganalyzer.topology_edges_declared
                (id, from_node_id, to_node_id, relation, from_owner_group, to_owner_group,
                 directed, provenance, confidence, version, deleted_at, created_at,
                 updated_at, created_by, updated_by)
            SELECT md5('map-plan-' || series.n::text)::uuid, {fromNodeId}, {toNodeId},
                'contains', 'map-plan-other-owner', 'map-plan-other-owner',
                true, 'declared', 1.00, 1, now(), now(), now(), 'map-plan-fixture', 'map-plan-fixture'
            FROM generate_series(1, 2000) AS series(n)
            """, cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO loganalyzer.topology_edge_declared_history
                (edge_id, from_node_id, to_node_id, relation, from_owner_group,
                 to_owner_group, edge_version, directed, provenance, confidence,
                 deleted_at, from_nano, to_nano)
            SELECT md5('map-plan-' || series.n::text)::uuid, {fromNodeId}, {toNodeId},
                'contains', 'map-plan-other-owner', 'map-plan-other-owner',
                1, true, 'declared', 1.00, now(), {asOf - 1000m}, NULL
            FROM generate_series(1, 2000) AS series(n)
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("ANALYZE loganalyzer.topology_edge_declared_history", cancellationToken);
    }

    private sealed record BoundParameter(string Name, DbType Type, object? Value);
    private sealed record CapturedCommand(string Sql, IReadOnlyList<BoundParameter> Parameters);

    private sealed class MappingCommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("topology.mapping.next-edge", StringComparison.Ordinal))
                Commands.Add(new(command.CommandText,
                    command.Parameters.Cast<DbParameter>()
                        .Select(static parameter => new BoundParameter(parameter.ParameterName,
                            parameter.DbType, parameter.Value)).ToArray()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InterceptedFactory(string connectionString, MappingCommandCapture capture)
        : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly DbContextOptions<ControlPlaneDbContext> _options = Configure(connectionString, capture);

        private static DbContextOptions<ControlPlaneDbContext> Configure(string connectionString,
            MappingCommandCapture capture)
        {
            var builder = new DbContextOptionsBuilder<ControlPlaneDbContext>();
            ControlPlaneServiceCollectionExtensions.Configure(builder, connectionString);
            builder.AddInterceptors(capture);
            return builder.Options;
        }

        public ControlPlaneDbContext CreateDbContext() => new(_options);
    }
}
