using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyDeclaredIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AccessScope A => AccessScope.ForGroups("topology-edge-admin-A", ["A"]);
    private static AccessScope AB => AccessScope.ForGroups("topology-edge-admin-AB", ["A", "B"]);

    private async Task<(IDbContextFactory<ControlPlaneDbContext> Factory, string From, string To)> Setup()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using var db = await factory.CreateDbContextAsync(Ct);
        await db.AuditLog.ExecuteDeleteAsync(Ct);
        db.Sources.AddRange(new SourceEntity { SourceId = "edge-SA", OwnerGroup = "A" },
            new SourceEntity { SourceId = "edge-SB", OwnerGroup = "A" });
        await db.SaveChangesAsync(Ct);
        var from = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "edge-SA", Ct)).Id;
        var to = (await db.TopologyNodes.SingleAsync(n => n.SourceId == "edge-SB", Ct)).Id;
        return (factory, from, to);
    }

    [Fact]
    public async Task Versioned_lifecycle()
    {
        var (factory, from, to) = await Setup();
        var registry = new TopologyEdgeRegistry(factory);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        var created = await Task.WhenAll(registry.CreateAsync(A, true, input, Ct), registry.CreateAsync(A, true, input, Ct));
        Assert.Equal([201, 409], created.Select(x => x.Status).Order());
        var id = created.Single(x => x.Status == 201).Edge!.Id;
        var updates = await Task.WhenAll(
            registry.UpdateAsync(A, true, id, input with { Version = "1", Relation = TopologyEdgeRelations.ConnectsTo }, Ct),
            registry.UpdateAsync(A, true, id, input with { Version = "1" }, Ct));
        Assert.Equal([200, 409], updates.Select(x => x.Status).Order());
        Assert.Equal(409, (await registry.DeleteAsync(A, true, id, 1, Ct)).Status);
        Assert.Equal(204, (await registry.DeleteAsync(A, true, id, 2, Ct)).Status);
        await using var db = await factory.CreateDbContextAsync(Ct);
        var rows = await db.TopologyDeclaredEdgeHistory.Where(h => h.EdgeId == id).OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(3, rows.Length);
        Assert.Equal(rows[0].ToNano, rows[1].FromNano);
        Assert.Equal(rows[1].ToNano, rows[2].FromNano);
        Assert.NotNull(rows[2].DeletedAt);
        Assert.Equal("depends_on", rows[0].Relation);
        Assert.Equal(rows[1].Relation, rows[2].Relation);
        Assert.Equal(3, await db.AuditLog.CountAsync(a => a.Action.StartsWith("topology.edge."), Ct));
    }

    [Fact]
    public async Task Endpoint_transfer_and_audit_rollback()
    {
        var (factory, from, to) = await Setup();
        var registry = new TopologyEdgeRegistry(factory);
        var input = new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn);
        var created = await registry.CreateAsync(A, true, input, Ct);
        Assert.Equal(201, created.Status);
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            (await db.Sources.SingleAsync(s => s.SourceId == "edge-SB", Ct)).OwnerGroup = "B";
            await db.SaveChangesAsync(Ct);
        }
        Assert.Equal(403, (await registry.UpdateAsync(A, true, created.Edge!.Id, input with { Version = "1" }, Ct)).Status);
        await using var admin = await factory.CreateDbContextAsync(Ct);
        var before = (await admin.TopologyReadState.SingleAsync(Ct)).Epoch;
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bizigo.reject_edge_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.action LIKE 'topology.edge.%' THEN RAISE EXCEPTION 'edge audit fixture failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER edge_audit_test BEFORE INSERT ON bizigo.audit_log
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_edge_audit_test();
            """, Ct);
        try
        {
            Assert.Equal(503, (await registry.UpdateAsync(AB, true, created.Edge.Id,
                input with { Version = "1", Relation = TopologyEdgeRelations.ConnectsTo }, Ct)).Status);
            await using var check = await factory.CreateDbContextAsync(Ct);
            Assert.Equal(before, (await check.TopologyReadState.SingleAsync(Ct)).Epoch);
            Assert.Equal(1, (await check.TopologyDeclaredEdges.SingleAsync(Ct)).Version);
            Assert.Single(await check.TopologyDeclaredEdgeHistory.ToArrayAsync(Ct));
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TRIGGER edge_audit_test ON bizigo.audit_log; DROP FUNCTION bizigo.reject_edge_audit_test();", Ct);
        }
        Assert.Equal(200, (await registry.UpdateAsync(AB, true, created.Edge.Id,
            input with { Version = "1", Relation = TopologyEdgeRelations.ConnectsTo }, Ct)).Status);
        await using var after = await factory.CreateDbContextAsync(Ct);
        Assert.Equal("B", (await after.TopologyDeclaredEdges.SingleAsync(Ct)).ToOwnerGroup);
    }

    [Fact]
    public async Task Provenance_roundtrip()
    {
        var (factory, from, to) = await Setup();
        var edge = (await new TopologyEdgeRegistry(factory).CreateAsync(A, true,
            new(from, to, TopologyEdgeRelations.DependsOn), Ct)).Edge!;
        await using var db = await factory.CreateDbContextAsync(Ct);
        var stored = await db.TopologyDeclaredEdges.SingleAsync(e => e.Id == edge.Id, Ct);
        Assert.True(stored.Directed);
        Assert.Equal("declared", stored.Provenance);
        Assert.Equal(1m, stored.Confidence);
        Assert.Equal("depends_on", stored.Relation);
    }

    [Fact]
    public async Task Real_http_declared_edge_requires_admin_both_owners_and_version()
    {
        var (factory, from, to) = await Setup();
        await using var api = await TelemetryApiHost.StartTopologyAsync(factory, Ct);
        var body = JsonSerializer.Serialize(new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.DependsOn));
        using var anonymous = await api.PostAsync("/v1/topology/edges", body, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var reader = await api.PostAsync("/v1/topology/edges", body);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        using var created = await api.PostAsync("/v1/topology/edges", body, role: "admin");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var edge = (await created.Content.ReadFromJsonAsync<TopologyDeclaredEdgeVersion>(Ct))!;
        Assert.Equal("1", edge.Version);
        using var duplicate = await api.PostAsync("/v1/topology/edges", body, role: "admin");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var stale = await api.PutAsync("/v1/topology/edges/" + edge.Id,
            JsonSerializer.Serialize(new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.ConnectsTo, "2")), role: "admin");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var updated = await api.PutAsync("/v1/topology/edges/" + edge.Id,
            JsonSerializer.Serialize(new TopologyDeclaredEdgeInput(from, to, TopologyEdgeRelations.ConnectsTo, "1")), role: "admin");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var deleted = await api.DeleteAsync("/v1/topology/edges/" + edge.Id + "?version=2", role: "admin");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }
}
