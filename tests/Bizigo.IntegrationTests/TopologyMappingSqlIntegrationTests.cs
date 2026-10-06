using System.Data;
using System.Data.Common;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bizigo.IntegrationTests;

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

        await using var dbForPlan = await fixture.Factory.CreateDbContextAsync(Ct);
        await dbForPlan.Database.OpenConnectionAsync(Ct);
        var connection = dbForPlan.Database.GetDbConnection();
        try
        {
            await using (var disableSeqScan = connection.CreateCommand())
            {
                disableSeqScan.CommandText = "ANALYZE topology_declared_edge_history; SET enable_seqscan=off";
                await disableSeqScan.ExecuteNonQueryAsync(Ct);
            }
            await using var explain = connection.CreateCommand();
            explain.CommandText = "EXPLAIN (FORMAT TEXT) "
                + captured.Sql.Replace("-- topology.mapping.next-edge", string.Empty,
                    StringComparison.Ordinal);
            foreach (var parameter in captured.Parameters)
            {
                var bound = explain.CreateParameter();
                bound.ParameterName = parameter.Name;
                bound.DbType = parameter.Type;
                bound.Value = parameter.Value ?? DBNull.Value;
                explain.Parameters.Add(bound);
            }
            var lines = new List<string>();
            await using var plan = await explain.ExecuteReaderAsync(Ct);
            while (await plan.ReadAsync(Ct)) lines.Add(plan.GetString(0));
            var indexed = lines.Any(line => line.Contains(
                "ix_topology_edge_hist_from_relation_revision",
                StringComparison.Ordinal));
            // Never persist SQL parameter values or plan lines: either may
            // include hidden node/owner identifiers.
            TelemetryDbFixture.Evidence("topology-mapping-keyset-plan", new
            {
                Route = "topology.mapping.next-edge",
                LimitOne = true,
                UsesMappingIndex = indexed,
                ParameterNames = captured.Parameters.Select(static parameter => parameter.Name),
            });
            Assert.True(indexed, "Production mapping keyset index was not used in forced-index EXPLAIN.");
        }
        finally
        {
            await using var reset = connection.CreateCommand();
            reset.CommandText = "RESET enable_seqscan";
            await reset.ExecuteNonQueryAsync(Ct);
            await dbForPlan.Database.CloseConnectionAsync();
        }
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
