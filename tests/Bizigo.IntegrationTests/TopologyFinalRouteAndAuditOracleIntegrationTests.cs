using System.Globalization;
using System.Net;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyFinalRouteAndAuditOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task B05_Ancestor_route_accepts_two_and_twenty_but_rejects_one_twentyone_duplicate_comma_and_hidden()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var scope = AccessScope.ForGroups("ancestor-seed", ["A", "B"]);
        var registry = new TopologyRegistry(fixture.Factory);
        var edges = new TopologyEdgeRegistry(fixture.Factory);
        var root = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "ancestor-root", "A", true, []), Ct)).Node!.Id;
        var targets = new List<string>();
        var proofByTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 21; index++)
        {
            var target = (await registry.CreateAsync(scope, true,
                new(TopologyNodeKind.Service, "ancestor-target-" + index, "A", true, []), Ct)).Node!.Id;
            targets.Add(target);
            var edge = await edges.CreateAsync(scope, true, new(root, target, "depends_on"), Ct);
            Assert.Equal(201, edge.Status);
            proofByTarget.Add(target, edge.Edge!.Id.ToString("D"));
        }
        var hidden = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "ancestor-hidden", "B", true, []), Ct)).Node!.Id;
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        static string Route(IEnumerable<string> ids) => "/v1/topology/ancestors?" +
            string.Join('&', ids.Select(id => "nodeId=" + Uri.EscapeDataString(id)));
        foreach (var count in new[] { 2, 20 })
        {
            using var response = await api.GetAsync(Route(targets.Take(count)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Found", json.RootElement.GetProperty("status").GetString());
            Assert.Equal(root, json.RootElement.GetProperty("node_id").GetString());
            Assert.Equal(count, json.RootElement.GetProperty("paths").GetArrayLength());
            var paths = json.RootElement.GetProperty("paths").EnumerateArray().ToArray();
            Assert.Equal(targets.Take(count).Order(StringComparer.Ordinal), paths.Select(path =>
                path.GetProperty("target_node_id").GetString()!).Order(StringComparer.Ordinal));
            foreach (var path in paths)
            {
                var target = path.GetProperty("target_node_id").GetString()!;
                Assert.Equal(new[] { root, target }, path.GetProperty("nodes").EnumerateArray()
                    .Select(static node => node.GetString()!).ToArray());
                Assert.Equal(new[] { proofByTarget[target] }, path.GetProperty("edge_ids").EnumerateArray()
                    .Select(static edgeId => edgeId.GetString()!).ToArray());
            }
        }
        foreach (var invalid in new[] { Route(targets.Take(1)), Route(targets),
                     Route([targets[0], targets[0]]), Route([targets[0] + "," + targets[1]]) })
        {
            using var response = await api.GetAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(hidden, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        using var forbidden = await api.GetAsync(Route([targets[0], hidden]));
        using var unknown = await api.GetAsync(Route([targets[0],
            TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid())]));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(forbidden.StatusCode, unknown.StatusCode);
        Assert.Equal(await forbidden.Content.ReadAsStringAsync(Ct), await unknown.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task B05_Publication_scope_and_expiry_cursor_revalidation_is_fail_closed()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var scope = AccessScope.ForGroups("cursor-seed", ["A"]);
        var registry = new TopologyRegistry(fixture.Factory);
        for (var index = 0; index < 3; index++)
            Assert.Equal(201, (await registry.CreateAsync(scope, true,
                new(TopologyNodeKind.Service, "cursor-" + index, "A", true, []), Ct)).Status);
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        using var first = await api.GetAsync("/v1/topology/nodes?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
        var cursor = page.RootElement.GetProperty("cursor").GetString();
        Assert.NotNull(cursor);
        var continuation = "/v1/topology/nodes?limit=1&cursor=" + Uri.EscapeDataString(cursor);
        using var wrongScope = await api.GetAsync(continuation, "B");
        Assert.Equal(HttpStatusCode.BadRequest, wrongScope.StatusCode);

        // Exercise the exact production signed-envelope TTL branch without a
        // day-long sleep; the inner stable key and all other bindings are kept.
        var codec = api.App.Services.GetRequiredService<TopologyReadCursorCodec>();
        var expired = codec.Encode(codec.Decode(cursor) with
        { ValidUntilUnixNano = TopologyIdentity.Nano(DateTimeOffset.UtcNow.AddSeconds(-1)) });
        using var afterExpiry = await api.GetAsync("/v1/topology/nodes?limit=1&cursor=" + Uri.EscapeDataString(expired));
        Assert.Equal(HttpStatusCode.Conflict, afterExpiry.StatusCode);

        // A PG topology mutation changes the epoch even when the list's first
        // page has the same rows; the old cursor must restart, not silently skip.
        Assert.Equal(201, (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "cursor-after-page", "A", true, []), Ct)).Status);
        using var afterMutation = await api.GetAsync(continuation);
        Assert.Equal(HttpStatusCode.Conflict, afterMutation.StatusCode);
    }

    [Fact]
    public async Task Q06_Real_PG_audit_insert_failure_never_returns_topology_data_200()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct);
        var marker = Guid.NewGuid().ToString("N");
        var function = "s05_audit_fail_" + marker;
        var trigger = "s05_audit_guard_" + marker;
        var subject = api.Subject("A");
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION bizigo." + function + "() RETURNS trigger "
            + "LANGUAGE plpgsql AS $$ BEGIN IF NEW.subject = '" + subject + "' "
            + "AND NEW.action LIKE 'topology.%' THEN RAISE EXCEPTION 'forced audit outage'; "
            + "END IF; RETURN NEW; END $$", Ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER " + trigger
                + " BEFORE INSERT ON bizigo.audit_log FOR EACH ROW EXECUTE FUNCTION bizigo."
                + function + "()", Ct);
            try
            {
                using var response = await api.GetAsync("/v1/topology/nodes");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
                Assert.Equal("QueryUnavailable", json.RootElement.GetProperty("reason").GetString());
                Assert.False(await db.AuditLog.AsNoTracking().AnyAsync(row =>
                    row.Subject == subject && row.Action == "topology.nodes.list", Ct));
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER " + trigger + " ON bizigo.audit_log", Ct);
            }
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP FUNCTION bizigo." + function + "()", Ct);
        }
        using var recovered = await api.GetAsync("/v1/topology/nodes");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.True(await db.AuditLog.AsNoTracking().AnyAsync(row =>
            row.Subject == subject && row.Action == "topology.nodes.list" && row.Succeeded, Ct));
    }
}
