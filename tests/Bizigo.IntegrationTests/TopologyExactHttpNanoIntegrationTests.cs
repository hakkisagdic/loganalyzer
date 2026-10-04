using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Proto.Common.V1;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Actual PG/CH, Collector projection, production HTTP/auth/scope registrations,
/// and a test-owned *server* clock. The request never supplies an expiry clock.
/// These cases supplement, rather than relabel, the older B04 27-case matrix.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyExactHttpNanoIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const decimal Day = TopologyExpiry.NanosecondsPerDay;

    private sealed record Seed(string Root, string Parent, string Child, string Third,
        string EdgeId, string CompetingEdgeId, decimal Expiry, decimal CompetingExpiry,
        decimal NarrowFrom, decimal NarrowTo, decimal BroadFrom, decimal BroadTo);

    [Fact]
    public async Task B04_Http_all_graph_read_surfaces_use_server_exact_E_minus_1_E_E_plus_1_ns()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        Assert.Equal(1m, seed.Expiry % 100m); // DateTimeOffset cannot fake this boundary.
        var asOf = seed.Expiry - 1;
        var clock = new MutableTopologyExpiryNanoClock(asOf);
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct, clock);
        var window = Window(asOf, seed.NarrowFrom, seed.NarrowTo);
        var list = "/v1/topology/edges?provenance=observed&" + window;
        var detail = "/v1/topology/edges/" + seed.EdgeId + "?" + window;
        var neighbors = "/v1/topology/nodes/" + seed.Parent + "/neighbors?" + window;
        var path = "/v1/topology/path?fromNode=" + seed.Parent + "&toNode=" + seed.Child + "&" + window;
        var ancestors = "/v1/topology/ancestors?nodeId=" + seed.Parent + "&nodeId=" + seed.Child
            + "&" + window;
        foreach (var now in new[] { seed.Expiry - 1, seed.Expiry, seed.Expiry + 1 })
        {
            clock.Set(now);
            var eligible = now < seed.Expiry;
            using (var response = await GetAsBothAsync(api, list))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = await BodyAsync(response);
                Assert.Equal(eligible, body.RootElement.GetProperty("edges").EnumerateArray()
                    .Any(row => row.GetProperty("id").GetString() == seed.EdgeId));
            }
            using (var response = await GetAsBothAsync(api, detail))
            {
                Assert.Equal(eligible ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
                if (eligible)
                {
                    using var body = await BodyAsync(response);
                    Assert.Equal(seed.EdgeId, body.RootElement.GetProperty("edge").GetProperty("id").GetString());
                    Assert.Equal("observed", body.RootElement.GetProperty("edge")
                        .GetProperty("provenance").GetString());
                }
            }
            using (var response = await GetAsBothAsync(api, neighbors))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = await BodyAsync(response);
                Assert.Equal(eligible, body.RootElement.GetProperty("neighbors").EnumerateArray()
                    .Any(row => row.GetProperty("edge_id").GetString() == seed.EdgeId));
            }
            using (var response = await GetAsBothAsync(api, path))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = await BodyAsync(response);
                Assert.Equal(eligible ? "Found" : "Unreachable",
                    body.RootElement.GetProperty("status").GetString());
                Assert.Equal(eligible ? new[] { seed.EdgeId } : Array.Empty<string>(), body.RootElement
                    .GetProperty("edge_ids").EnumerateArray().Select(value => value.GetString()!).ToArray());
            }
            using (var response = await GetAsBothAsync(api, ancestors))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = await BodyAsync(response);
                Assert.Equal(eligible ? "Found" : "Unreachable",
                    body.RootElement.GetProperty("status").GetString());
                Assert.Equal(eligible ? seed.Root : null,
                    body.RootElement.GetProperty("node_id").GetString());
                Assert.Equal(eligible, body.RootElement.GetProperty("paths").EnumerateArray()
                    .Any(proof => proof.GetProperty("edge_ids").EnumerateArray()
                        .Any(id => id.GetString() == seed.EdgeId)));
            }
            using (var response = await api.GetAsync(neighbors, "A"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = await BodyAsync(response);
                Assert.Equal(eligible ? "1" : "0",
                    body.RootElement.GetProperty("outside_neighbor_count").GetString());
                Assert.Equal(JsonValueKind.Null,
                    body.RootElement.GetProperty("outside_neighbor_reason").ValueKind);
            }
        }
        // Same historical asOf after server time E+1 cannot resurrect proof.
        clock.Set(seed.Expiry + 1);
        using var historical = await GetAsBothAsync(api, detail);
        Assert.Equal(HttpStatusCode.NotFound, historical.StatusCode);
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + seed.EdgeId + "'")).Trim());
    }

    [Fact]
    public async Task B05_Exact_expiry_invalidates_list_neighbor_path_and_evidence_continuations()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var asOf = seed.Expiry - 1;
        var clock = new MutableTopologyExpiryNanoClock(asOf);
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct, clock);
        var window = Window(asOf, seed.NarrowFrom, seed.NarrowTo);
        var routes = new[]
        {
            (Path: "/v1/topology/edges?limit=1&" + window, Cursor: "cursor"),
            (Path: "/v1/topology/nodes/" + seed.Parent + "/neighbors?limit=1&" + window, Cursor: "cursor"),
            (Path: "/v1/topology/path?fromNode=" + seed.Root + "&toNode=" + seed.Child
                + "&limit=1&" + window, Cursor: "cursor"),
            (Path: "/v1/topology/edges/" + seed.EdgeId + "?evidencePageSize=1&" + window,
                Cursor: "evidence_cursor"),
        };
        var continuations = new List<string>();
        foreach (var (route, cursorField) in routes)
        {
            using var response = await GetAsBothAsync(api, route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = await BodyAsync(response);
            var cursor = body.RootElement.GetProperty(cursorField).GetString();
            Assert.False(string.IsNullOrWhiteSpace(cursor));
            continuations.Add(route + (cursorField == "evidence_cursor" ? "&evidenceCursor=" : "&cursor=")
                + Uri.EscapeDataString(cursor));
        }
        clock.Set(seed.Expiry);
        foreach (var continuation in continuations)
        {
            using var stale = await GetAsBothAsync(api, continuation);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var problem = await BodyAsync(stale);
            Assert.Equal("SnapshotChanged", problem.RootElement.GetProperty("reason").GetString());
        }
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + seed.EdgeId + "'")).Trim());
    }

    [Fact]
    public async Task B05_Earlier_eligible_competing_expiry_invalidates_ancestor_midread_and_path_cursor()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        Assert.True(seed.CompetingExpiry < seed.Expiry);
        var asOf = seed.CompetingExpiry - 1;
        var clock = new MutableTopologyExpiryNanoClock(asOf);
        var armed = 0;
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct, clock,
            afterAudit: record =>
            {
                if (record.Action == "topology.ancestors" &&
                    Interlocked.CompareExchange(ref armed, 2, 1) == 1)
                    clock.Set(seed.CompetingExpiry);
            });
        var window = Window(asOf, seed.BroadFrom, seed.BroadTo);
        var ancestors = "/v1/topology/ancestors?nodeId=" + seed.Parent + "&nodeId=" + seed.Child
            + "&" + window;
        using (var before = await GetAsBothAsync(api, ancestors))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var body = await BodyAsync(before);
            Assert.Equal("Found", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(seed.Root, body.RootElement.GetProperty("node_id").GetString());
            var witnessEdges = body.RootElement.GetProperty("paths").EnumerateArray()
                .SelectMany(proof => proof.GetProperty("edge_ids").EnumerateArray())
                .Select(id => id.GetString()!).ToArray();
            Assert.Contains(seed.EdgeId, witnessEdges);
            Assert.DoesNotContain(seed.CompetingEdgeId, witnessEdges);
        }
        var path = "/v1/topology/path?fromNode=" + seed.Root + "&toNode=" + seed.Child
            + "&limit=1&" + window;
        using var firstPath = await GetAsBothAsync(api, path);
        Assert.Equal(HttpStatusCode.OK, firstPath.StatusCode);
        using var firstBody = await BodyAsync(firstPath);
        var cursor = firstBody.RootElement.GetProperty("cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        // The selected witness expires at E1, but the unselected visible
        // depends_on edge expires at earlier E0 and must cap this cursor.
        clock.Set(seed.CompetingExpiry);
        using (var next = await GetAsBothAsync(api, path + "&cursor=" + Uri.EscapeDataString(cursor)))
            Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
        clock.Set(asOf);
        Volatile.Write(ref armed, 1);
        using (var inflight = await GetAsBothAsync(api, ancestors))
        {
            Assert.Equal(HttpStatusCode.Conflict, inflight.StatusCode);
            Assert.Equal(2, Volatile.Read(ref armed));
        }
        // A new request at E0 can still find the later selected witness;
        // only the earlier in-flight snapshot/cursor is invalid.
        using (var fresh = await GetAsBothAsync(api, ancestors))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            using var body = await BodyAsync(fresh);
            Assert.Equal("Found", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(seed.Root, body.RootElement.GetProperty("node_id").GetString());
        }
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + seed.CompetingEdgeId + "'")).Trim());
    }

    [Fact]
    public async Task B04_Hidden_counted_neighbor_expiry_invalidates_uncursored_midread_without_leaking_identity()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var asOf = seed.Expiry - 1;
        var clock = new MutableTopologyExpiryNanoClock(asOf);
        var armed = 0;
        await using var api = await TopologyHttpOracleHost.StartAsync(fixture, Ct, clock,
            afterAudit: record =>
            {
                if (record.Action == "topology.neighbors" &&
                    Interlocked.CompareExchange(ref armed, 2, 1) == 1)
                    clock.Set(seed.Expiry);
            });
        var route = "/v1/topology/nodes/" + seed.Parent + "/neighbors?"
            + Window(asOf, seed.NarrowFrom, seed.NarrowTo);
        using (var before = await api.GetAsync(route, "A"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var body = await BodyAsync(before);
            Assert.Equal("1", body.RootElement.GetProperty("outside_neighbor_count").GetString());
            Assert.Null(body.RootElement.GetProperty("cursor").GetString());
            var json = body.RootElement.GetRawText();
            Assert.DoesNotContain(seed.Child, json, StringComparison.Ordinal);
            Assert.DoesNotContain(seed.EdgeId, json, StringComparison.Ordinal);
        }
        Volatile.Write(ref armed, 1);
        using (var inflight = await api.GetAsync(route, "A"))
        {
            Assert.Equal(HttpStatusCode.Conflict, inflight.StatusCode);
            Assert.Equal(2, Volatile.Read(ref armed));
            using var problem = await BodyAsync(inflight);
            Assert.Equal("SnapshotChanged", problem.RootElement.GetProperty("reason").GetString());
        }
        // The original physical row persists, but a fresh E read no longer
        // reports a hidden outside neighbor, even with historical asOf fixed.
        using (var fresh = await api.GetAsync(route, "A"))
        {
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            using var body = await BodyAsync(fresh);
            Assert.Equal("0", body.RootElement.GetProperty("outside_neighbor_count").GetString());
            Assert.Null(body.RootElement.GetProperty("cursor").GetString());
            Assert.DoesNotContain(seed.EdgeId, body.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed WHERE edge_id = '"
            + seed.EdgeId + "'")).Trim());
    }

    private static async Task<Seed> SeedAsync(TelemetryDbFixture fixture)
    {
        var at = DateTimeOffset.UtcNow.AddHours(2).AddDays(-10);
        var parentStart = TopologyIdentity.Nano(at);
        var childStart = parentStart + 1001;
        var competingParentStart = parentStart + 5 * Day - 3_600_000_000_000m;
        var competingChildStart = competingParentStart + 1001;
        var expiry = childStart + 10 * Day;
        var competingExpiry = competingChildStart + 5 * Day;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var a = "nano-a-" + suffix;
        var b = "nano-b-" + suffix;
        var r = "nano-r-" + suffix;
        var t = "nano-t-" + suffix;
        var scope = AccessScope.ForGroups("nano-ab-" + suffix, ["A", "B"]);
        foreach (var (source, owner) in new[] { (a, "A"), (b, "B"), (r, "A"), (t, "A") })
            await fixture.SourceAsync(source, owner, at.AddDays(-2));
        var historicalClock = new FakeTimeProvider(at.AddDays(-1));
        var registry = new TopologyRegistry(fixture.Factory, historicalClock);
        var root = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "nano-root", "A", true, [new(r, "n", "root")]), Ct)).Node!.Id;
        var parent = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "nano-parent", "A", true, [new(a, "n", "parent")]), Ct)).Node!.Id;
        var child = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "nano-child", "B", true, [new(b, "n", "child")]), Ct)).Node!.Id;
        var third = (await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "nano-third", "A", true, [new(t, "n", "third")]), Ct)).Node!.Id;
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var sourceRoots = await db.TopologyNodes.AsNoTracking().Where(node =>
            node.SourceId == a || node.SourceId == b || node.SourceId == r || node.SourceId == t)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        var declared = new TopologyEdgeRegistry(fixture.Factory, historicalClock);
        foreach (var (source, target) in new[] { (a, parent), (b, child), (r, root), (t, third) })
            Assert.Equal(201, (await declared.CreateAsync(scope, true,
                new(sourceRoots[source].Id, target, "contains"), Ct)).Status);
        Assert.Equal(201, (await declared.CreateAsync(scope, true,
            new(root, parent, "depends_on"), Ct)).Status);

        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        var publisher = new TopologyPublicationCoordinator(fixture.Factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        var parentTrace = Trace(a, parentStart, "parent");
        var childTrace = Trace(b, childStart, "child");
        var parentSpan = parentTrace.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = childTrace.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        await AdmitAsync(fixture, writer, parentTrace, "nano-main-" + suffix, 90, 90);
        await AdmitAsync(fixture, writer, childTrace, "nano-main-" + suffix, 90, 10);

        var competingParent = Trace(r, competingParentStart, "root");
        var competingChild = Trace(t, competingChildStart, "third");
        var traceId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
        var rootSpan = competingParent.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var thirdSpan = competingChild.ResourceSpans[0].ScopeSpans[0].Spans[0];
        rootSpan.TraceId = traceId;
        thirdSpan.TraceId = traceId;
        rootSpan.ParentSpanId = ByteString.Empty;
        thirdSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        thirdSpan.ParentSpanId = rootSpan.SpanId;
        await AdmitAsync(fixture, writer, competingParent, "nano-competing-" + suffix, 90, 90);
        await AdmitAsync(fixture, writer, competingChild, "nano-competing-" + suffix, 90, 5);

        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), new TopologyPublicationFence(
                new TopologyPublicationRevisionSource(fixture.Factory, watermark,
                    new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)))));
        var narrowFrom = parentStart - 1;
        var narrowTo = childStart + 1;
        var broadFrom = parentStart - 1;
        var broadTo = competingChildStart + 1;
        var first = await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry - 1,
            Provenance: TopologyProvenance.Observed, FromUnixNano: narrowFrom, ToUnixNano: narrowTo)
            { ExpiryReadClockUnixNano = expiry - 1 }, scope, Ct);
        var selected = Assert.Single(first.Items.Where(edge => edge.FromNode == parent && edge.ToNode == child));
        Assert.Equal(expiry, selected.EffectiveExpiry!.Value);
        var earlier = await graph.SearchEdgesAsync(new TopologyEdgeQuery(competingExpiry - 1,
            Provenance: TopologyProvenance.Observed, FromUnixNano: broadFrom, ToUnixNano: broadTo)
            { ExpiryReadClockUnixNano = competingExpiry - 1 }, scope, Ct);
        var competing = Assert.Single(earlier.Items.Where(edge => edge.FromNode == root && edge.ToNode == third));
        Assert.Equal(competingExpiry, competing.EffectiveExpiry!.Value);
        return new(root, parent, child, third, selected.Id, competing.Id, expiry, competingExpiry,
            narrowFrom, narrowTo, broadFrom, broadTo);
    }

    private static OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest Trace(
        string source, decimal at, string service)
    {
        var request = TelemetryDbFixture.Traces(source, checked((ulong)at));
        var attributes = request.ResourceSpans[0].Resource.Attributes;
        attributes.Single(attribute => attribute.Key == "service.name").Value.StringValue = service;
        attributes.Add(new KeyValue { Key = "service.namespace", Value = new AnyValue { StringValue = "n" } });
        return request;
    }

    private static async Task AdmitAsync(TelemetryDbFixture fixture, TelemetryWriter writer,
        OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest request, string root,
        int traceDays, int observedDays)
    {
        var path = Path.Combine(fixture.Root, root);
        using var ingest = new SignalIngest(new(), new(fixture.Factory), fixture.Objects,
            Options.Create(new SignalOptions { Directory = path, ObservedRetentionDays = observedDays }),
            Options.Create(new WalOptions { Directory = path }), Options.Create(fixture.RawOptions),
            NullLogger<WriteAheadLog>.Instance, owners: fixture.Owners, bindings: fixture.Owners,
            sink: writer, retention: new TelemetryRetentionPolicy(traceDays));
        await ingest.RecoverAsync(Ct);
        await fixture.EmitAsync(ingest, request, TelemetrySignal.Traces);
    }

    private static string Window(decimal asOf, decimal from, decimal to) =>
        "asOf=" + Uri.EscapeDataString(Utc(asOf)) + "&from=" + Uri.EscapeDataString(Utc(from))
        + "&to=" + Uri.EscapeDataString(Utc(to));

    private static string Utc(decimal nano)
    {
        var seconds = decimal.Truncate(nano / 1_000_000_000m);
        var fraction = (long)(nano - seconds * 1_000_000_000m);
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "."
            + fraction.ToString("D9", CultureInfo.InvariantCulture) + "Z";
    }

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static async Task<HttpResponseMessage> GetAsBothAsync(TopologyHttpOracleHost api, string route)
    {
        var key = (SymmetricSecurityKey)typeof(TopologyHttpOracleHost)
            .GetField("key", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(api)!;
        var token = new JwtSecurityToken("https://telemetry-fixture.invalid/issuer", "bizigo-api",
            [new Claim("sub", api.Subject("AB")), new Claim("roles", "reader"),
                new Claim("groups", "/" + api.Subject("A")),
                new Claim("groups", "/" + api.Subject("B"))],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return await api.Http.SendAsync(request, Ct);
    }
}
