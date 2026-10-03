using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only real PG production registry writes: alias history, source
/// scope, concurrent versions and transaction-wide audit failure rollback.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyRegistryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AccessScope A => AccessScope.ForGroups("admin-A", ["A"]);
    private static AccessScope AB => AccessScope.ForGroups("admin-AB", ["A", "B"]);
    private static TopologyNodeInput Service(string source = "SA", string owner = "A", string alias = "checkout") =>
        new(TopologyNodeKind.Service, "same-label", owner, true, [new(source, "n", alias)]);

    private async Task<IDbContextFactory<ControlPlaneDbContext>> Setup()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        await using var db = await factory.CreateDbContextAsync(Ct);
        // This collection shares one owned PG database. ControlPlaneAsync resets
        // inventory/topology but intentionally does not reset the audit table.
        // Each registry case measures its entire audit set, so clear only at the
        // case boundary, before production writes or audit-failure injection.
        await db.AuditLog.ExecuteDeleteAsync(Ct);
        Assert.Empty(await db.AuditLog.ToArrayAsync(Ct));
        db.Sources.AddRange(new SourceEntity { SourceId = "SA", OwnerGroup = "A" }, new SourceEntity { SourceId = "SB", OwnerGroup = "B" });
        await db.SaveChangesAsync(Ct);
        return factory;
    }

    [Fact]
    public async Task Registry_rename_alias_ambiguity()
    {
        var factory = await Setup();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        var registry = new TopologyRegistry(factory, clock);
        var serviceA = await registry.CreateAsync(AB, true, Service(), Ct);
        var serviceB = await registry.CreateAsync(AB, true, Service("SB", "B"), Ct);
        Assert.Equal(201, serviceA.Status); Assert.Equal(201, serviceB.Status);
        Assert.NotEqual(serviceA.Node!.Id, serviceB.Node!.Id);
        var instances = new List<TopologyNodeVersion>();
        foreach (var instance in new[] { "i1", "i2" })
        {
            var result = await registry.CreateAsync(A, true, new(TopologyNodeKind.ServiceInstance, "same-label", "A", true,
                [new("SA", "n", "checkout", serviceA.Node.Id, instance)]), Ct);
            Assert.Equal(201, result.Status); instances.Add(result.Node!);
        }
        Assert.NotEqual(instances[0].Id, instances[1].Id);
        await using var initial = await factory.CreateDbContextAsync(Ct);
        var binding = await initial.TopologyBindings.AsNoTracking().SingleAsync(b => b.TargetNodeId == serviceA.Node.Id, Ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        var renamed = await registry.UpdateAsync(A, true, serviceA.Node.Id, Service() with { DisplayName = "renamed", Version = 1 }, Ct);
        Assert.Equal(200, renamed.Status);
        await using var check = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(binding.BindingId, (await check.TopologyBindings.SingleAsync(b => b.TargetNodeId == serviceA.Node.Id, Ct)).BindingId);
        Assert.Equal(409, (await registry.CreateAsync(A, true, Service(), Ct)).Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        var aliasChange = await registry.UpdateAsync(A, true, serviceA.Node.Id, Service(alias: "new-name") with { DisplayName = "renamed", Version = 2 }, Ct);
        Assert.Equal(200, aliasChange.Status);
        var boundary = ulong.Parse(aliasChange.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture);
        var owners = new HistoricalTelemetryOwners(factory);
        var resolvedOwners = await owners.ResolveAsync([new("old", boundary - 1, ["SA"]), new("new", boundary, ["SA"])], Ct);
        var resolver = new HistoricalTopologyBindings(factory);
        var actual = await resolver.ResolveAsync([
            new(resolvedOwners[0], "n", "checkout", null), new(resolvedOwners[1], "n", "checkout", null),
            new(resolvedOwners[1], "n", "new-name", null)], Ct);
        Assert.Equal(serviceA.Node.Id, actual[0].NodeId);
        Assert.Equal("MissingBinding", actual[1].Reason); Assert.Equal(serviceA.Node.Id, actual[2].NodeId);
        Assert.Equal("renamed", actual[0].DisplayName);
        var instanceAfterAlias = await resolver.ResolveAsync([new(resolvedOwners[1], "n", "new-name", "i1")], Ct);
        Assert.Equal(instances[0].Id, Assert.Single(instanceAfterAlias).NodeId);
        Assert.Equal(409, (await registry.CreateAsync(A, true, new(TopologyNodeKind.ServiceInstance, "duplicate", "A", true,
            [new("SA", "n", "new-name", serviceA.Node.Id, "i1")]), Ct)).Status);
        Assert.Equal(6, await check.AuditLog.CountAsync(a => a.Action.StartsWith("topology.node."), Ct));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Audit_failure_rolls_back_node_alias_history_and_epoch(string operation)
    {
        var factory = await Setup(); var registry = new TopologyRegistry(factory);
        var existing = operation == "create" ? null : (await registry.CreateAsync(A, true, Service(), Ct)).Node;
        await using var admin = await factory.CreateDbContextAsync(Ct);
        var epoch = (await admin.TopologyReadState.SingleAsync(Ct)).Epoch;
        var nodeCount = await admin.TopologyNodes.CountAsync(Ct);
        var historyCount = await admin.TopologyNodeHistory.CountAsync(Ct);
        await admin.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION bizigo.reject_topology_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'topology audit fixture failure'; END $$;
            CREATE TRIGGER topology_audit_test BEFORE INSERT ON bizigo.audit_log
            FOR EACH ROW EXECUTE FUNCTION bizigo.reject_topology_audit_test();
            """, Ct);
        try
        {
            var result = operation switch
            {
                "create" => await registry.CreateAsync(A, true, Service(), Ct),
                "update" => await registry.UpdateAsync(A, true, existing!.Id, Service(alias: "changed") with { Version = 1 }, Ct),
                _ => await registry.DeleteAsync(A, true, existing!.Id, 1, Ct),
            };
            Assert.Equal(503, result.Status);
            await using var check = await factory.CreateDbContextAsync(Ct);
            Assert.Equal(epoch, (await check.TopologyReadState.SingleAsync(Ct)).Epoch);
            Assert.Equal(nodeCount, await check.TopologyNodes.CountAsync(Ct));
            Assert.Equal(historyCount, await check.TopologyNodeHistory.CountAsync(Ct));
            if (existing is not null)
            {
                Assert.Equal(1, (await check.TopologyNodes.SingleAsync(n => n.Id == existing.Id, Ct)).Version);
                Assert.Null((await check.TopologyBindings.SingleAsync(Ct)).ToNano);
                Assert.Null((await check.TopologyNodeHistory.SingleAsync(h => h.NodeId == existing.Id, Ct)).ToNano);
            }
            else Assert.Empty(await check.TopologyBindings.ToArrayAsync(Ct));
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TRIGGER topology_audit_test ON bizigo.audit_log; DROP FUNCTION bizigo.reject_topology_audit_test();", Ct);
        }
    }

    [Fact]
    public async Task Concurrent_alias_and_version_writes_have_one_winner()
    {
        var factory = await Setup(); var registry = new TopologyRegistry(factory);
        var created = await Task.WhenAll(registry.CreateAsync(A, true, Service(), Ct), registry.CreateAsync(A, true, Service(), Ct));
        Assert.Equal(new[] { 201, 409 }, created.Select(r => r.Status).Order());
        var node = created.Single(r => r.Status == 201).Node!;
        var changed = await Task.WhenAll(registry.UpdateAsync(A, true, node.Id, Service() with { Version = 1, DisplayName = "one" }, Ct),
            registry.UpdateAsync(A, true, node.Id, Service() with { Version = 1, DisplayName = "two" }, Ct));
        Assert.Equal(new[] { 200, 409 }, changed.Select(r => r.Status).Order());
        await using var check = await factory.CreateDbContextAsync(Ct);
        Assert.Single(await check.TopologyBindings.ToArrayAsync(Ct));
        Assert.Equal(2, await check.TopologyNodeHistory.CountAsync(h => h.NodeId == node.Id, Ct));
        Assert.Equal(2, await check.AuditLog.CountAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_owner_transfer_and_mutation_have_one_retryable_loser()
    {
        var factory = await Setup();
        var registry = new TopologyRegistry(factory);
        var input = new TopologyNodeInput(TopologyNodeKind.Network, "network", "A", true, []);
        var node = (await registry.CreateAsync(AB, true, input, Ct)).Node!;

        var results = await Task.WhenAll(
            registry.UpdateAsync(AB, true, node.Id, input with { OwnerGroup = "B", Version = 1 }, Ct),
            registry.UpdateAsync(A, true, node.Id, input with { DisplayName = "stale-name", Version = 1 }, Ct));

        Assert.Equal(new[] { 200, 409 }, results.Select(r => r.Status).Order());
        await using var check = await factory.CreateDbContextAsync(Ct);
        var current = await check.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct);
        Assert.Equal(2, current.Version);
        Assert.Equal(2, await check.TopologyNodeHistory.CountAsync(h => h.NodeId == node.Id, Ct));
        Assert.Equal(2, await check.AuditLog.CountAsync(a => a.Resource == node.Id, Ct));
        var transfers = await check.TopologyOwnerHistory.Where(h => h.NodeId == node.Id).ToArrayAsync(Ct);
        Assert.Equal(current.OwnerGroup == "B" ? 1 : 0, transfers.Length);
        if (transfers.Length == 1)
        {
            Assert.Equal("A", transfers[0].OldOwner);
            Assert.Equal("B", transfers[0].NewOwner);
            Assert.Equal("admin-AB", transfers[0].ChangedBy);
        }
    }

    [Fact]
    public async Task Scope_and_owner_transfer_recheck_authoritative_sources()
    {
        var factory = await Setup(); var registry = new TopologyRegistry(factory);
        Assert.Equal(403, (await registry.CreateAsync(A, false, Service(), Ct)).Status);
        Assert.Equal(403, (await registry.CreateAsync(A, true, Service("SB", "A"), Ct)).Status);
        var node = (await registry.CreateAsync(A, true, Service(), Ct)).Node!;
        Assert.Equal(409, (await registry.UpdateAsync(AB, true, node.Id, Service(owner: "B") with { Version = 1 }, Ct)).Status);
        await using (var db = await factory.CreateDbContextAsync(Ct))
        { (await db.Sources.SingleAsync(s => s.SourceId == "SA", Ct)).OwnerGroup = "B"; await db.SaveChangesAsync(Ct); }
        Assert.Equal(403, (await registry.UpdateAsync(A, true, node.Id, Service() with { Version = 1 }, Ct)).Status);
        var transferred = await registry.UpdateAsync(AB, true, node.Id, Service(owner: "B") with { Version = 1 }, Ct);
        Assert.Equal(200, transferred.Status);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var history = await check.TopologyNodeHistory.Where(h => h.NodeId == node.Id).OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(new[] { "A", "B" }, history.Select(h => h.OwnerGroup));
        Assert.Equal(history[0].ToNano, history[1].FromNano);
    }

    [Fact]
    public async Task Disable_and_tombstone_preserve_history_and_reject_stale_versions()
    {
        var factory = await Setup(); var registry = new TopologyRegistry(factory);
        var node = (await registry.CreateAsync(A, true, Service(), Ct)).Node!;
        Assert.Equal(200, (await registry.UpdateAsync(A, true, node.Id, Service() with { Version = 1, Enabled = false }, Ct)).Status);
        Assert.Equal(409, (await registry.DeleteAsync(A, true, node.Id, 1, Ct)).Status);
        Assert.Equal(204, (await registry.DeleteAsync(A, true, node.Id, 2, Ct)).Status);
        Assert.Equal(409, (await registry.UpdateAsync(A, true, node.Id, Service() with { Version = 3 }, Ct)).Status);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var history = await check.TopologyNodeHistory.Where(h => h.NodeId == node.Id).OrderBy(h => h.FromNano).ToArrayAsync(Ct);
        Assert.Equal(new[] { true, false, false }, history.Select(h => h.Enabled));
        Assert.True((await check.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct)).Deleted);
        Assert.NotNull((await check.TopologyBindings.SingleAsync(Ct)).ToNano);
    }

    [Fact]
    public async Task Source_identity_cannot_be_forged_or_changed()
    {
        var factory = await Setup(); var registry = new TopologyRegistry(factory);
        var source = new TopologyNodeInput(TopologyNodeKind.Source, "source", "A", true, [], "SA");
        Assert.Equal(409, (await registry.CreateAsync(A, true, source, Ct)).Status);
        Assert.Equal(409, (await registry.CreateAsync(A, true, source with { SourceId = "absent" }, Ct)).Status);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var node = await check.TopologyNodes.SingleAsync(n => n.SourceId == "SA", Ct);
        Assert.Equal(400, (await registry.UpdateAsync(AB, true, node.Id, source with { SourceId = "SB", Version = node.Version }, Ct)).Status);
        Assert.Equal(400, (await registry.UpdateAsync(A, true, node.Id, Service() with { Version = node.Version }, Ct)).Status);
        Assert.Empty(await check.AuditLog.ToArrayAsync(Ct));
        Assert.Equal(204, (await registry.DeleteAsync(A, true, node.Id, node.Version, Ct)).Status);
        await using (var inventory = await factory.CreateDbContextAsync(Ct))
        { (await inventory.Sources.SingleAsync(s => s.SourceId == "SA", Ct)).Hostname = "new-name"; await inventory.SaveChangesAsync(Ct); }
        await using var after = await factory.CreateDbContextAsync(Ct);
        Assert.True((await after.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct)).Deleted);
        Assert.False((await after.TopologyNodeHistory.SingleAsync(h => h.NodeId == node.Id && h.ToNano == null, Ct)).Enabled);
    }

    [Fact]
    public async Task Real_http_node_writes_require_admin_and_enforce_versions()
    {
        var factory = await Setup();
        await using var api = await TelemetryApiHost.StartTopologyAsync(factory, Ct);
        var input = Service(); var json = JsonSerializer.Serialize(input);
        using var anonymous = await api.PostAsync("/v1/topology/nodes", json, null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var reader = await api.PostAsync("/v1/topology/nodes", json);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        using var created = await api.PostAsync("/v1/topology/nodes", json, role: "admin");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var node = (await created.Content.ReadFromJsonAsync<TopologyNodeVersion>(Ct))!;
        using var updated = await api.PutAsync("/v1/topology/nodes/" + node.Id,
            JsonSerializer.Serialize(new TopologyNodeMutationDto(input.Kind, "HTTP rename", input.OwnerGroup,
                input.Enabled, input.Bindings, input.SourceId, node.Version.ToString(CultureInfo.InvariantCulture))), role: "admin");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var stale = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=1", role: "admin");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var deleted = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=2", role: "admin");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var check = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(3, await check.AuditLog.CountAsync(Ct));
        Assert.All(await check.AuditLog.ToArrayAsync(Ct), row => Assert.Equal(api.Subject("A"), row.Subject));
    }

    [Fact]
    public async Task Real_http_node_mutation_versions_are_string_only_and_int64_max_delete_is_lossless()
    {
        var factory = await Setup();
        await using var api = await TelemetryApiHost.StartTopologyAsync(factory, Ct);
        var input = Service();
        using var created = await api.PostAsync("/v1/topology/nodes", JsonSerializer.Serialize(input), role: "admin");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Assert.Equal(JsonValueKind.String, createdJson.RootElement.GetProperty("version").ValueKind);
        var node = (await created.Content.ReadFromJsonAsync<TopologyNodeVersion>(Ct))!;
        string Wire(string version) => JsonSerializer.Serialize(new TopologyNodeMutationDto(input.Kind, "changed",
            input.OwnerGroup, input.Enabled, input.Bindings, input.SourceId, version));

        using var numeric = await api.PutAsync("/v1/topology/nodes/" + node.Id,
            JsonSerializer.Serialize(input with { Version = long.MaxValue }), role: "admin");
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        using var outOfRange = await api.PutAsync("/v1/topology/nodes/" + node.Id,
            Wire("9223372036854775808"), role: "admin");
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        using var staleMax = await api.PutAsync("/v1/topology/nodes/" + node.Id,
            Wire("9223372036854775807"), role: "admin");
        Assert.Equal(HttpStatusCode.Conflict, staleMax.StatusCode);
        using var updated = await api.PutAsync("/v1/topology/nodes/" + node.Id, Wire("1"), role: "admin");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedJson = JsonDocument.Parse(await updated.Content.ReadAsStringAsync(Ct));
        Assert.Equal("2", updatedJson.RootElement.GetProperty("version").GetString());

        using var malformedDelete = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=01", role: "admin");
        Assert.Equal(HttpStatusCode.BadRequest, malformedDelete.StatusCode);
        using var outOfRangeDelete = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=9223372036854775808", role: "admin");
        Assert.Equal(HttpStatusCode.BadRequest, outOfRangeDelete.StatusCode);
        using var staleDelete = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=9223372036854775807", role: "admin");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            (await db.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct)).Version = long.MaxValue;
            (await db.TopologyNodeHistory.SingleAsync(h => h.NodeId == node.Id && h.ToNano == null, Ct))
                .NodeVersion = long.MaxValue;
            await db.SaveChangesAsync(Ct);
        }
        using var deleted = await api.DeleteAsync("/v1/topology/nodes/" + node.Id + "?version=9223372036854775807", role: "admin");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var stored = await check.TopologyNodes.SingleAsync(n => n.Id == node.Id, Ct);
        Assert.True(stored.Deleted);
        Assert.Equal(long.MaxValue, stored.Version);
        Assert.Equal(3, await check.AuditLog.CountAsync(Ct));
    }
}
