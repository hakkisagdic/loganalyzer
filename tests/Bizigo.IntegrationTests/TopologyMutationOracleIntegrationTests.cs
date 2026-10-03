using System.Net;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyMutationOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("node", "create")]
    [InlineData("node", "update")]
    [InlineData("node", "delete")]
    [InlineData("edge", "create")]
    [InlineData("edge", "update")]
    [InlineData("edge", "delete")]
    public async Task H02_six_HTTP_writes_preserve_scope_versions_state_and_audit_atomicity(string kind, string operation)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var registry = new TopologyRegistry(f.Factory);
        var seedScope = AccessScope.ForGroups("h02-seed", ["A", "B"]);
        var nodes = new List<string>();
        foreach (var owner in new[] { "A", "A", "B", "B" })
        {
            var result = await registry.CreateAsync(seedScope, true,
                new(TopologyNodeKind.Service, "H02-" + owner, owner, true, []), Ct);
            Assert.Equal(201, result.Status); nodes.Add(result.Node!.Id);
        }
        var edges = new TopologyEdgeRegistry(f.Factory);
        var aEdge = await edges.CreateAsync(seedScope, true, new(nodes[0], nodes[1], "depends_on"), Ct);
        var bEdge = await edges.CreateAsync(seedScope, true, new(nodes[2], nodes[3], "depends_on"), Ct);
        Assert.Equal(201, aEdge.Status); Assert.Equal(201, bEdge.Status);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var baseline = await State(f, api.Subject("A"));

        using (var forbidden = await Send(foreign: true))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(baseline, await State(f, api.Subject("A")));
        using (var malformed = await Send(invalid: true))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(baseline, await State(f, api.Subject("A")));
        if (operation != "create")
        {
            using var stale = await Send(version: "99");
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(baseline, await State(f, api.Subject("A")));
        }

        await using var admin = await f.Factory.CreateDbContextAsync(Ct);
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bizigo.reject_h02_http_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'H02-DRIVER-SECRET'; END $$;
            CREATE TRIGGER h02_http_audit BEFORE INSERT ON bizigo.audit_log
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_h02_http_audit();
            """, Ct);
        try
        {
            using var rejected = await Send();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            var body = await rejected.Content.ReadAsStringAsync(Ct);
            foreach (var marker in new[] { "H02-DRIVER-SECRET", "Npgsql", "Exception", nodes[2], nodes[3] })
                Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
            // Full committed state, history, aliases, owner snapshots, epoch
            // and subject audit are byte-equal: count-only rollback could miss changes.
            Assert.Equal(baseline, await State(f, api.Subject("A")));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await admin.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER h02_http_audit ON bizigo.audit_log; DROP FUNCTION bizigo.reject_h02_http_audit();", cleanup.Token);
        }

        using var success = await Send();
        Assert.Equal(operation == "create" ? HttpStatusCode.Created : operation == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK,
            success.StatusCode);
        if (operation != "delete")
        {
            using var body = JsonDocument.Parse(await success.Content.ReadAsStringAsync(Ct));
            Assert.Equal(operation == "create" ? "1" : "2", body.RootElement.GetProperty("version").GetString());
        }
        await using var check = await f.Factory.CreateDbContextAsync(Ct);
        var audit = Assert.Single(await check.AuditLog.AsNoTracking().Where(a => a.Subject == api.Subject("A")).ToArrayAsync(Ct));
        Assert.Equal("topology." + kind + "." + operation, audit.Action);
        Assert.Equal("A", audit.Scope);
        Assert.NotEqual(baseline, await State(f, api.Subject("A")));
        if (operation != "create")
        {
            using var repeat = await Send();
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
            Assert.Single(await check.AuditLog.AsNoTracking().Where(a => a.Subject == api.Subject("A")).ToArrayAsync(Ct));
        }
        TelemetryDbFixture.Evidence("h02-http-" + kind + "-" + operation, new
        { kind, operation, status = (int)success.StatusCode, audit, rollbackUnchanged = true });

        Task<HttpResponseMessage> Send(bool foreign = false, bool invalid = false, string version = "1")
        {
            var id = kind == "node" ? (foreign ? nodes[2] : nodes[0])
                : (foreign && operation == "delete" ? bEdge.Edge!.Id : aEdge.Edge!.Id).ToString("D");
            var route = "/v1/topology/" + (kind == "node" ? "nodes" : "edges");
            if (operation != "create") route += "/" + id;
            if (operation == "delete")
                return api.DeleteAsync(route + "?version=" + (invalid ? "01" : version), role: "admin");
            string body;
            if (kind == "node")
                body = JsonSerializer.Serialize(new TopologyNodeMutationDto(TopologyNodeKind.Service, "H02-new-name",
                    foreign ? "B" : "A", true, [], Version: invalid ? (operation == "create" ? "1" : null)
                        : operation == "create" ? null : version), Wire);
            else
                body = JsonSerializer.Serialize(new TopologyDeclaredEdgeInput(nodes[0], foreign ? nodes[2] : nodes[1],
                    invalid ? "not-a-relation" : "connects_to", operation == "create" ? null : version), Wire);
            return operation == "create" ? api.PostAsync(route, body, role: "admin") : api.PutAsync(route, body, role: "admin");
        }
    }

    private static async Task<string> State(TelemetryDbFixture f, string subject)
    {
        await using var db = await f.Factory.CreateDbContextAsync(Ct);
        return JsonSerializer.Serialize(new
        {
            nodes = await db.TopologyNodes.AsNoTracking().OrderBy(n => n.Id).ToArrayAsync(Ct),
            histories = await db.TopologyNodeHistory.AsNoTracking().OrderBy(h => h.Revision).ToArrayAsync(Ct),
            bindings = await db.TopologyBindings.AsNoTracking().OrderBy(h => h.Revision).ToArrayAsync(Ct),
            owners = await db.TopologyOwnerHistory.AsNoTracking().OrderBy(h => h.Revision).ToArrayAsync(Ct),
            edges = await db.TopologyDeclaredEdges.AsNoTracking().OrderBy(e => e.Id).ToArrayAsync(Ct),
            edgeHistories = await db.TopologyDeclaredEdgeHistory.AsNoTracking().OrderBy(h => h.Revision).ToArrayAsync(Ct),
            revision = await db.TopologyReadState.AsNoTracking().SingleAsync(Ct),
            audit = await db.AuditLog.AsNoTracking().Where(a => a.Subject == subject).OrderBy(a => a.At).ThenBy(a => a.Action).ToArrayAsync(Ct),
        }, Wire);
    }
}
