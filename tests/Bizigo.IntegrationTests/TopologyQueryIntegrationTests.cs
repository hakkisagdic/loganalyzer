using System.Data;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>V07: plans are captured from the production reader, never reimplemented in the fixture.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyQueryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Production_sql_explain()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var plans = new List<TopologySqlPlan>();
        var observed = new TopologyObservedSnapshotReader(fixture.Storage) { ObserveQuery = plans.Add };
        var revisions = new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage));
        var source = new TopologyGraphSnapshotSource(fixture.Factory, observed,
            new TopologyPublicationFence(revisions)) { ObserveQuery = plans.Add };
        var scope = AccessScope.ForGroups("topology-plan-reader", ["topology-plan-owner"]);
        var clock = (decimal)fixture.Now + 1_000_000_000m;
        _ = await new TopologyGraphQueryService(source).SearchEdgesAsync(new(clock,
            Provenance: TopologyProvenance.Observed, FromUnixNano: clock - 1_000_000_000m,
            ToUnixNano: clock), scope, Ct);

        var revision = await revisions.ReadAsync(Ct);
        var clickhouse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in plans.Where(static plan => plan.Route != "declared-edges"))
            clickhouse.Add(plan.Route, await fixture.SqlAsync("EXPLAIN indexes=1 " + plan.Sql,
                new Dictionary<string, object> { ["watermark"] = revision.ClickHouseWatermark }));
        var declared = Assert.Single(plans, static plan => plan.Route == "declared-edges");
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        await db.Database.OpenConnectionAsync(Ct);
        var postgres = new List<string>();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN (FORMAT TEXT) " + declared.Sql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) postgres.Add(reader.GetString(0));
        }
        finally { await db.Database.CloseConnectionAsync(); }

        // No bound values or hidden row identities enter the diagnostic file.
        TelemetryDbFixture.Evidence("topology-production-explain", new
        {
            Routes = plans.Select(plan => new { plan.Route, plan.BoundParameterNames }),
            ClickHousePlans = clickhouse,
            PostgreSqlPlan = postgres,
        });
        var edges = Assert.Single(plans, static plan => plan.Route == "observed-edges");
        Assert.Contains("watermark", edges.BoundParameterNames);
        // Known RED until the scoped/as-of source pushes these predicates to
        // production SQL. A watermark-only global scan is not V07 evidence.
        Assert.Contains("scope_groups", edges.BoundParameterNames);
        Assert.Contains("window_from", edges.BoundParameterNames);
        Assert.Contains("window_to", edges.BoundParameterNames);
        Assert.Contains("scope_groups", declared.BoundParameterNames);
    }
}
