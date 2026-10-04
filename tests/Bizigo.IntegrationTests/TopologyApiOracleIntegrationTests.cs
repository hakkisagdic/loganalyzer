using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyApiOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed record Seed(string Root, string Left, string Right, string Hidden, string Edge, string RightEdge, string AsOf)
    {
        public string Route(string name) => name switch
        {
            "nodes" => "/v1/topology/nodes",
            "node" => "/v1/topology/nodes/" + Root,
            "edges" => "/v1/topology/edges",
            "edge" => "/v1/topology/edges/" + Edge,
            "neighbors" => "/v1/topology/nodes/" + Root + "/neighbors",
            "path" => "/v1/topology/path?fromNode=" + Root + "&toNode=" + Left,
            "ancestors" => "/v1/topology/ancestors?nodeId=" + Left + "&nodeId=" + Right,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
        public string At(string name) => Route(name) + (Route(name).Contains('?', StringComparison.Ordinal) ? "&" : "?") + "asOf=" + AsOf;
    }
    private static readonly string[] Reads = ["nodes", "node", "edges", "edge", "neighbors", "path", "ancestors"];

    [Fact]
    public async Task Route_auth_matrix_all_thirteen_methods_and_OpenAPI_paths()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var routes = Reads.Select(r => (HttpMethod.Get, seed.At(r))).Concat(new[]
        {
            (HttpMethod.Post, "/v1/topology/nodes"), (HttpMethod.Put, "/v1/topology/nodes/" + seed.Root),
            (HttpMethod.Delete, "/v1/topology/nodes/" + seed.Root + "?version=1"),
            (HttpMethod.Post, "/v1/topology/edges"), (HttpMethod.Put, "/v1/topology/edges/" + seed.Edge),
            (HttpMethod.Delete, "/v1/topology/edges/" + seed.Edge + "?version=1"),
        }).ToArray();
        Assert.Equal(13, routes.Length);
        foreach (var (method, route) in routes)
        {
            using var anonymous = await Send(api, method, route, null, "reader");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var ingest = await Send(api, method, route, "A", "ingest");
            Assert.Equal(HttpStatusCode.Forbidden, ingest.StatusCode);
            if (method != HttpMethod.Get)
            {
                using var reader = await Send(api, method, route, "A", "reader");
                Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
            }
            else
            {
                using var wrongAudience = await api.GetAsync(route, audience: "not-bizigo");
                Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
                using var allowed = await api.GetAsync(route);
                Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            }
        }
        using var document = await api.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        using var json = JsonDocument.Parse(await document.Content.ReadAsStringAsync(Ct));
        var paths = json.RootElement.GetProperty("paths");
        var actual = paths.EnumerateObject().Where(p => p.Name.StartsWith("/v1/topology/", StringComparison.Ordinal))
            .SelectMany(p => p.Value.EnumerateObject().Where(m => m.Name is "get" or "post" or "put" or "delete")
                .Select(m => m.Name + " " + p.Name)).Order(StringComparer.Ordinal).ToArray();
        var expected = new[] { "get /v1/topology/nodes", "get /v1/topology/nodes/{nodeId}",
            "get /v1/topology/edges", "get /v1/topology/edges/{edgeId}", "get /v1/topology/nodes/{nodeId}/neighbors",
            "get /v1/topology/path", "get /v1/topology/ancestors", "post /v1/topology/nodes",
            "put /v1/topology/nodes/{nodeId}", "delete /v1/topology/nodes/{nodeId}", "post /v1/topology/edges",
            "put /v1/topology/edges/{edgeId}", "delete /v1/topology/edges/{edgeId}" };
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        TelemetryDbFixture.Evidence("h01-route-auth", new { routes = routes.Select(r => new { method = r.Item1.Method, path = r.Item2 }), openApi = actual });
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("node")]
    [InlineData("edges")]
    [InlineData("edge")]
    [InlineData("neighbors")]
    [InlineData("path")]
    [InlineData("ancestors")]
    public async Task Read_route_matrix_rejects_invalid_filters_limits_and_preserves_scope(string name)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var route = seed.At(name);
        using var success = await api.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var body = await success.Content.ReadAsStringAsync(Ct);
        AssertTypedRead(name, body, seed);
        Assert.DoesNotContain(seed.Hidden, body, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN-B-MARKER", body, StringComparison.Ordinal);
        var before = await f.Db.AuditLog.CountAsync(a => a.Subject == api.Subject("A"), Ct);
        var limit = name == "edge" ? "evidencePageSize" : "limit";
        foreach (var extra in new[] { "unknown=x", limit + "=0", limit + "=201", limit + "=-1", limit + "=1e2",
                     "kind=unknown", "relation=unknown", "provenance=unknown", "from=2020-01-01T00:00:00Z" })
        {
            using var invalid = await api.GetAsync(route + "&" + extra);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Sweep(await invalid.Content.ReadAsStringAsync(Ct), seed);
        }
        Assert.Equal(before, await f.Db.AuditLog.CountAsync(a => a.Subject == api.Subject("A"), Ct));
        TelemetryDbFixture.Evidence("h03-read-" + name, new { name, success = body, invalidCount = 9 });
    }

    [Fact]
    public async Task Hidden_unknown_targets_and_cross_scope_cursor_have_identical_nonleaking_behavior()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var absent = "service:" + Guid.NewGuid().ToString("D");
        foreach (var format in new[] { "/v1/topology/nodes/{0}", "/v1/topology/nodes/{0}/neighbors",
            "/v1/topology/path?fromNode=" + seed.Root + "&toNode={0}",
            "/v1/topology/ancestors?nodeId=" + seed.Left + "&nodeId={0}" })
        {
            using var hidden = await api.GetAsync(string.Format(CultureInfo.InvariantCulture, format, seed.Hidden));
            using var missing = await api.GetAsync(string.Format(CultureInfo.InvariantCulture, format, absent));
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); Assert.Equal(missing.StatusCode, hidden.StatusCode);
            Assert.Equal(await missing.Content.ReadAsStringAsync(Ct), await hidden.Content.ReadAsStringAsync(Ct));
        }
        using var first = await api.GetAsync(seed.At("nodes") + "&limit=1");
        using var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
        Assert.True(page.RootElement.GetProperty("partial").GetBoolean());
        var cursor = page.RootElement.GetProperty("cursor").GetString(); Assert.NotNull(cursor);
        using var wrong = await api.GetAsync(seed.At("nodes") + "&limit=1&cursor=" + Uri.EscapeDataString(cursor), "B");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Sweep(await wrong.Content.ReadAsStringAsync(Ct), seed);
    }

    [Theory]
    [InlineData("nodes", "nodes", 3)]
    [InlineData("edges", "edges", 2)]
    [InlineData("neighbors", "neighbors", 2)]
    public async Task Read_route_partial_pages_are_bounded_complete_and_without_hidden_bytes(string route, string collection, int expected)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var ids = new HashSet<string>(StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null; var pages = 0;
        do
        {
            using var response = await api.GetAsync(seed.At(route) + "&limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
            Assert.True(bytes.Length <= Bizigo.Api.TopologyResponseBudget.MaximumBytes);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain(seed.Hidden, text, StringComparison.Ordinal);
            Assert.DoesNotContain("FOREIGN-B-MARKER", text, StringComparison.Ordinal);
            using var page = JsonDocument.Parse(bytes);
            var row = Assert.Single(page.RootElement.GetProperty(collection).EnumerateArray());
            Assert.True(ids.Add(row.GetProperty(route == "neighbors" ? "node_id" : "id").GetString()!));
            cursor = page.RootElement.GetProperty("cursor").GetString();
            Assert.Equal(cursor is not null, page.RootElement.GetProperty("partial").GetBoolean());
            if (cursor is not null) Assert.True(seen.Add(cursor));
            pages++; Assert.True(pages <= expected);
        } while (cursor is not null);
        Assert.Equal(expected, ids.Count);
        TelemetryDbFixture.Evidence("h03-pagination-" + route, new { route, pages, ids });
    }

    [Fact]
    public async Task Failure_body_real_database_error_all_seven_routes_is_503_not_empty()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        await f.SqlAsync("RENAME TABLE topology_edges_observed TO topology_fault_driver_marker");
        try
        {
            foreach (var name in Reads)
            {
                using var response = await api.GetAsync(seed.At(name));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(Ct); Sweep(body, seed);
                using var json = JsonDocument.Parse(body);
                Assert.Equal("QueryUnavailable", json.RootElement.GetProperty("reason").GetString());
            }
            var audits = await f.Db.AuditLog.AsNoTracking().Where(a => a.Subject == api.Subject("A")).ToArrayAsync(Ct);
            Assert.Equal(7, audits.Length); Assert.All(audits, a => Assert.False(a.Succeeded));
            TelemetryDbFixture.Evidence("h06-real-db-errors", new { audits, count = 7 });
        }
        finally { await f.SqlAsync("RENAME TABLE topology_fault_driver_marker TO topology_edges_observed"); }
        using var restored = await api.GetAsync(seed.At("nodes")); Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_body_and_cancellation_real_inflight_PG_wait_is_bounded(bool clientCancel)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(f);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        await using var locker = await f.Factory.CreateDbContextAsync(Ct);
        await using var transaction = await locker.Database.BeginTransactionAsync(Ct);
        await locker.Database.ExecuteSqlRawAsync("LOCK TABLE bizigo.topology_node_history IN ACCESS EXCLUSIVE MODE", Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = Stopwatch.StartNew();
        var pending = api.GetCancellableAsync(seed.At("nodes"), cancel.Token);
        using (var witness = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            witness.CancelAfter(TimeSpan.FromSeconds(5));
            while (await LockedQueryCount(f, api.ApplicationName, witness.Token) == 0) await Task.Delay(20, witness.Token);
        }
        Assert.False(pending.IsCompleted); // actual production SQL is waiting in PG
        long cancelMs = -1;
        if (clientCancel)
        {
            var watch = Stopwatch.StartNew(); cancel.Cancel();
            await api.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), Ct));
            while (await LockedQueryCount(f, api.ApplicationName, Ct) != 0 && watch.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(10, Ct);
            Assert.Equal(0, await LockedQueryCount(f, api.ApplicationName, Ct));
            cancelMs = watch.ElapsedMilliseconds; Assert.True(cancelMs <= 1000);
        }
        else
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(13), Ct);
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Sweep(await response.Content.ReadAsStringAsync(Ct), seed);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(13));
        }
        using (var auditWait = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            auditWait.CancelAfter(TimeSpan.FromSeconds(2));
            while (!await f.Db.AuditLog.AnyAsync(a => a.Subject == api.Subject("A"), auditWait.Token)) await Task.Delay(10, auditWait.Token);
        }
        var audit = Assert.Single(await f.Db.AuditLog.AsNoTracking().Where(a => a.Subject == api.Subject("A")).ToArrayAsync(Ct));
        Assert.False(audit.Succeeded); Assert.Contains("outcome=Cancelled", audit.Details, StringComparison.Ordinal);
        TelemetryDbFixture.Evidence("h06-pg-wait-" + clientCancel, new { clientCancel, cancelMs, elapsedMs = started.ElapsedMilliseconds, audit });
        await transaction.RollbackAsync(Ct);
    }

    private static async Task<long> LockedQueryCount(TelemetryDbFixture f, string application, CancellationToken ct)
    {
        await using var db = await f.Factory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE application_name=@app AND wait_event_type='Lock' AND query LIKE '%topology_node_history%'";
        var parameter = command.CreateParameter(); parameter.ParameterName = "app"; parameter.Value = application; command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static void AssertTypedRead(string route, string body, Seed seed)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        static void Shape(JsonElement value, params string[] keys) =>
            Assert.Equal(keys.Order(StringComparer.Ordinal),
                value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        static string Text(JsonElement value, string key)
        {
            var field = value.GetProperty(key);
            Assert.Equal(JsonValueKind.String, field.ValueKind);
            return field.GetString()!;
        }
        static void DecimalText(JsonElement value, string key)
        {
            var text = Text(value, key);
            Assert.NotEmpty(text);
            Assert.All(text, c => Assert.InRange(c, '0', '9'));
            Assert.True(decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        }
        static void Null(JsonElement value, string key) => Assert.Equal(JsonValueKind.Null, value.GetProperty(key).ValueKind);
        static void Complete(JsonElement value, bool cursor)
        {
            Assert.False(value.GetProperty("partial").GetBoolean());
            Null(value, "reason");
            DecimalText(value, "published_sequence");
            if (cursor) Null(value, "cursor");
        }
        void Node(JsonElement node, string id, string display)
        {
            Shape(node, "id", "kind", "display_name", "owner_group", "enabled", "version",
                "valid_from_unix_nano", "valid_to_unix_nano");
            Assert.Equal(id, Text(node, "id")); Assert.Equal("service", Text(node, "kind"));
            Assert.Equal(display, Text(node, "display_name")); Assert.Equal("A", Text(node, "owner_group"));
            Assert.True(node.GetProperty("enabled").GetBoolean()); Assert.Equal("1", Text(node, "version"));
            DecimalText(node, "valid_from_unix_nano"); Null(node, "valid_to_unix_nano");
        }
        void Edge(JsonElement edge, string id, string target)
        {
            Shape(edge, "id", "from_node_id", "to_node_id", "relation", "provenance", "directed", "confidence",
                "from_owner_group", "to_owner_group", "visibility", "first_seen_unix_nano", "last_seen_unix_nano",
                "effective_expiry_unix_nano", "publication_sequence", "version");
            Assert.Equal(id, Text(edge, "id")); Assert.Equal(seed.Root, Text(edge, "from_node_id"));
            Assert.Equal(target, Text(edge, "to_node_id")); Assert.Equal("depends_on", Text(edge, "relation"));
            Assert.Equal("declared", Text(edge, "provenance")); Assert.True(edge.GetProperty("directed").GetBoolean());
            Assert.Equal(1d, edge.GetProperty("confidence").GetDouble());
            Assert.Equal("A", Text(edge, "from_owner_group")); Assert.Equal("A", Text(edge, "to_owner_group"));
            Assert.Equal("same_owner", Text(edge, "visibility")); Assert.Equal("1", Text(edge, "version"));
            DecimalText(edge, "first_seen_unix_nano"); DecimalText(edge, "last_seen_unix_nano");
            DecimalText(edge, "publication_sequence"); Null(edge, "effective_expiry_unix_nano");
        }
        switch (route)
        {
            case "nodes":
                Shape(root, "nodes", "cursor", "partial", "reason", "published_sequence"); Complete(root, true);
                var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
                Assert.Equal(new[] { seed.Root, seed.Left, seed.Right }.Order(StringComparer.Ordinal),
                    nodes.Select(n => Text(n, "id")).Order(StringComparer.Ordinal));
                Node(Assert.Single(nodes, n => Text(n, "id") == seed.Root), seed.Root, "root");
                Node(Assert.Single(nodes, n => Text(n, "id") == seed.Left), seed.Left, "left");
                Node(Assert.Single(nodes, n => Text(n, "id") == seed.Right), seed.Right, "right");
                break;
            case "node":
                Shape(root, "node"); Node(root.GetProperty("node"), seed.Root, "root"); break;
            case "edges":
                Shape(root, "edges", "cursor", "partial", "reason", "published_sequence"); Complete(root, true);
                var edges = root.GetProperty("edges").EnumerateArray().ToArray();
                Assert.Equal(new[] { seed.Edge, seed.RightEdge }.Order(StringComparer.Ordinal),
                    edges.Select(e => Text(e, "id")).Order(StringComparer.Ordinal));
                Edge(Assert.Single(edges, e => Text(e, "id") == seed.Edge), seed.Edge, seed.Left);
                Edge(Assert.Single(edges, e => Text(e, "id") == seed.RightEdge), seed.RightEdge, seed.Right);
                break;
            case "edge":
                Shape(root, "edge", "evidence", "evidence_cursor");
                Edge(root.GetProperty("edge"), seed.Edge, seed.Left);
                Assert.Empty(root.GetProperty("evidence").EnumerateArray()); Null(root, "evidence_cursor"); break;
            case "neighbors":
                Shape(root, "neighbors", "outside_neighbor_count", "outside_neighbor_reason", "cursor", "partial", "reason", "published_sequence");
                Complete(root, true); Assert.Equal("0", Text(root, "outside_neighbor_count")); Null(root, "outside_neighbor_reason");
                var neighbors = root.GetProperty("neighbors").EnumerateArray().ToArray();
                Assert.Equal(new[] { seed.Left, seed.Right }.Order(StringComparer.Ordinal),
                    neighbors.Select(v => Text(v, "node_id")).Order(StringComparer.Ordinal));
                foreach (var row in neighbors)
                {
                    Shape(row, "node_id", "edge_id", "relation", "provenance", "direction");
                    Assert.Equal(Text(row, "node_id") == seed.Left ? seed.Edge : seed.RightEdge, Text(row, "edge_id"));
                    Assert.Equal("depends_on", Text(row, "relation")); Assert.Equal("declared", Text(row, "provenance"));
                    Assert.Equal("outgoing", Text(row, "direction"));
                }
                break;
            case "path":
                Shape(root, "status", "nodes", "edge_ids", "cursor", "partial", "reason", "published_sequence"); Complete(root, true);
                Assert.Equal("Found", Text(root, "status"));
                Assert.Equal(new[] { seed.Root, seed.Left }, root.GetProperty("nodes").EnumerateArray().Select(x => x.GetString()));
                Assert.Equal(new[] { seed.Edge }, root.GetProperty("edge_ids").EnumerateArray().Select(x => x.GetString())); break;
            case "ancestors":
                Shape(root, "status", "node_id", "paths", "partial", "reason", "published_sequence"); Complete(root, false);
                Assert.Equal("Found", Text(root, "status")); Assert.Equal(seed.Root, Text(root, "node_id"));
                var paths = root.GetProperty("paths").EnumerateArray().ToArray();
                Assert.Equal(new[] { seed.Left, seed.Right }.Order(StringComparer.Ordinal),
                    paths.Select(v => Text(v, "target_node_id")).Order(StringComparer.Ordinal));
                foreach (var path in paths)
                {
                    Shape(path, "target_node_id", "nodes", "edge_ids"); var target = Text(path, "target_node_id");
                    Assert.Equal(new[] { seed.Root, target }, path.GetProperty("nodes").EnumerateArray().Select(x => x.GetString()));
                    Assert.Equal(new[] { target == seed.Left ? seed.Edge : seed.RightEdge }, path.GetProperty("edge_ids").EnumerateArray().Select(x => x.GetString()));
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static void Sweep(string body, Seed seed)
    {
        foreach (var marker in new[] { "FOREIGN-B-MARKER", seed.Hidden, "topology_fault_driver_marker", "ClickHouse", "Npgsql", "SELECT ", "Exception", "secret" })
            Assert.DoesNotContain(marker, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"nodes\":[]", body, StringComparison.Ordinal);
    }
    private static Task<HttpResponseMessage> Send(TopologyHttpOracleHost api, HttpMethod method, string path, string? owner, string role) =>
        method == HttpMethod.Get ? api.GetAsync(path, owner, role) : method == HttpMethod.Post ? api.PostAsync(path, "{}", owner, role)
            : method == HttpMethod.Put ? api.PutAsync(path, "{}", owner, role) : api.DeleteAsync(path, owner, role);

    private static async Task<Seed> SeedAsync(TelemetryDbFixture f)
    {
        var registry = new TopologyRegistry(f.Factory); var scope = AccessScope.ForGroups("seed", ["A", "B"]);
        var ids = new List<string>();
        foreach (var name in new[] { "root", "left", "right", "FOREIGN-B-MARKER" })
        {
            var result = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, name,
                name == "FOREIGN-B-MARKER" ? "B" : "A", true, []), Ct);
            Assert.Equal(201, result.Status); ids.Add(result.Node!.Id);
        }
        var edgeRegistry = new TopologyEdgeRegistry(f.Factory);
        var first = await edgeRegistry.CreateAsync(scope, true, new(ids[0], ids[1], "depends_on"), Ct);
        var second = await edgeRegistry.CreateAsync(scope, true, new(ids[0], ids[2], "depends_on"), Ct);
        Assert.Equal(201, first.Status); Assert.Equal(201, second.Status);
        return new(ids[0], ids[1], ids[2], ids[3], first.Edge!.Id.ToString("D"), second.Edge!.Id.ToString("D"),
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
    }
}
