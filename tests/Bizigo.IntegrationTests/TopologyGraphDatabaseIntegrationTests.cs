using System.Net;
using System.Reflection;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Runs the production providers across real PG node/declared-edge history and
/// CH telemetry/publication reads. The reflected constructor bridge lets this
/// isolated branch compile before the parallel storage/source commits merge;
/// missing production types fail the test rather than skipping it.
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyGraphDatabaseIntegrationTests(DevStackFixture stack)
{
    public static IEnumerable<object[]> BudgetCases()
    {
        foreach (var provider in new[] { "topology.graph-path", "topology.common-ancestor" })
        foreach (var dimension in new[] { "node", "edge", "page", "byte" })
        foreach (var delta in new[] { -1, 0, 1 }) yield return [provider, dimension, delta];
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Directed_path_fixture_follows_opaque_node_order_with_two_edges()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, token);
        var (query, window, scope) = await SeedScenarioAsync(fixture, token);
        var slice = await Provider("topology.graph-path", query, TopologyProviderBudget.Default)
            .GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items);
        using var nodes = JsonDocument.Parse(item.Payload["node_ids"]);
        using var edges = JsonDocument.Parse(item.Payload["edge_ids"]);
        var path = nodes.RootElement.EnumerateArray().Select(static node => node.GetString()!).ToArray();
        Assert.Equal(3, path.Length);
        Assert.True(string.CompareOrdinal(path[0], path[^1]) < 0);
        Assert.Equal(2, edges.RootElement.GetArrayLength());
    }

    [Theory, MemberData(nameof(BudgetCases))]
    [Trait("Category", "Integration")]
    public async Task Real_provider_budgets(string providerId, string dimension, int delta)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, token);
        var (query, window, scope) = await SeedScenarioAsync(fixture, token);
        var baseline = await Provider(providerId, query, TopologyProviderBudget.Default).GatherAsync(
            window, scope, GatherBudget.Default, token);
        Assert.Equal(EvidenceStatus.Gathered, baseline.Status);
        var itemBytes = JsonSerializer.SerializeToUtf8Bytes(Assert.Single(baseline.Items), BundleSerializer.Options).Length;
        var limits = new Dictionary<string, int>
        {
            ["node"] = 1000, ["edge"] = 4000, ["page"] = 20, ["byte"] = 1024 * 1024,
        };
        limits[dimension] = (dimension switch
        {
            "node" => 3, "edge" => 2, "page" => 3, _ => itemBytes,
        }) + delta;
        var budget = new TopologyProviderBudget(limits["node"], limits["edge"], limits["page"], limits["byte"]);
        var result = await Provider(providerId, query, budget).GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.Equal(delta < 0 ? EvidenceStatus.Unavailable : EvidenceStatus.Gathered, result.Status);
        Assert.Equal(delta < 0, result.Truncated);
        Assert.Equal(delta < 0 ? 0 : 1, result.Items.Count);
        Assert.Equal(delta < 0 ? "NotComparable" : "Evaluated", result.Telemetry!.Evaluation);

        await using var auditDb = await fixture.Factory.CreateDbContextAsync(token);
        var actions = await auditDb.AuditLog.Where(row => row.Subject == scope.Subject)
            .Select(row => row.Action).ToArrayAsync(token);
        Assert.Contains("rca.propagation", actions);
        Assert.Contains(actions, action => action.Contains("feed", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Determinism_and_scope()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, token);
        var (query, window, scope) = await SeedScenarioAsync(fixture, token);
        var path = Provider("topology.graph-path", query, TopologyProviderBudget.Default);
        var first = await path.GatherAsync(window, scope, GatherBudget.Default, token);
        var second = await path.GatherAsync(window, scope, GatherBudget.Default, token);
        var foreign = AccessScope.ForGroups(scope.Subject + "-foreign", ["r08-other"]);
        var outside = await path.GatherAsync(window, foreign, GatherBudget.Default, token);
        Assert.Equal(EvidenceStatus.Gathered, first.Status);
        Assert.Equal(EvidenceStatus.Gathered, second.Status);
        Assert.Empty(outside.Items);

        static EvidenceBundle Bundle(EvidenceSlice slice, RcaWindow window, AccessScope scope,
            DateTimeOffset gatheredAt) => new()
        {
            Id = Guid.NewGuid(), GatheredAt = gatheredAt, Window = window,
            Scope = new(scope.OwnerGroups.Order(StringComparer.Ordinal).ToArray(), scope.IsUnrestricted),
            Slices = [slice], Trust = new(2, 0),
        };
        var a1 = Bundle(first, window, scope, window.To);
        var a2 = Bundle(second, window, scope, window.To.AddMinutes(1));
        var b = Bundle(outside, window, foreign, window.To);
        var store = new EvidenceBundleStore(fixture.Factory);
        await store.SaveAsync(a1, token);
        await store.SaveAsync(a2, token);
        await store.SaveAsync(b, token);
        var reopened = new EvidenceBundleStore(new ControlPlaneFactory(stack.PostgresConnectionString));
        var loadedA1 = await reopened.GetAsync(a1.Id, token);
        var loadedA2 = await reopened.GetAsync(a2.Id, token);
        var loadedB = await reopened.GetAsync(b.Id, token);
        Assert.NotNull(loadedA1); Assert.NotNull(loadedA2); Assert.NotNull(loadedB);
        Assert.Equal(a1.ContentHash, loadedA1.ContentHash);
        Assert.Equal(loadedA1.ContentHash, loadedA2.ContentHash);
        Assert.NotEqual(loadedA1.ContentHash, loadedB.ContentHash);
        Assert.False(loadedA1.Scope.IsReadableBy(foreign));
        Assert.Empty(loadedB.Items);
        Assert.Contains("proof_edges", Assert.Single(loadedA1.Items).Payload.Keys);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Proof_references_and_bounds()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, token);
        var (query, window, scope) = await SeedScenarioAsync(fixture, token);
        var slice = await Provider("topology.graph-path", query, TopologyProviderBudget.Default)
            .GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items);
        using var nodes = JsonDocument.Parse(item.Payload["node_ids"]);
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        var nodeIds = nodes.RootElement.EnumerateArray().Select(static node => node.GetString()).ToArray();
        var edges = proof.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, nodeIds.Length);
        Assert.Equal(2, edges.Length);
        for (var index = 0; index < edges.Length; index++)
        {
            var edge = edges[index];
            Assert.False(string.IsNullOrWhiteSpace(edge.GetProperty("id").GetString()));
            Assert.Equal(nodeIds[index], edge.GetProperty("from_node").GetString());
            Assert.Equal(nodeIds[index + 1], edge.GetProperty("to_node").GetString());
            Assert.Equal("declared", edge.GetProperty("provenance").GetString());
            Assert.Equal(1m, edge.GetProperty("confidence").GetDecimal());
            Assert.True(edge.GetProperty("last_seen_unix_nano").GetDecimal() > 0);
            Assert.InRange(edge.GetProperty("evidence").GetArrayLength(), 0, 200);
            Assert.Equal(JsonValueKind.Null, edge.GetProperty("evidence_cursor").ValueKind);
        }
    }

    private static async Task<(IScopedQuery Query, RcaWindow Window, AccessScope Scope)> SeedScenarioAsync(
        TelemetryDbFixture fixture, CancellationToken token)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var owner = "r08-" + suffix;
        var sourceA = "r08-a-" + suffix;
        var sourceB = "r08-b-" + suffix;
        var scope = AccessScope.ForGroups("r08-" + suffix, [owner]);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SourceAsync(sourceA, owner);
        await fixture.SourceAsync(sourceB, owner);

        await using (var db = await fixture.Factory.CreateDbContextAsync(token))
        {
            var sources = await db.TopologyNodes.Where(node => node.SourceId == sourceA || node.SourceId == sourceB)
                .ToDictionaryAsync(node => node.SourceId!, token);
            Assert.Equal(2, sources.Count);
            // The path provider orders affected nodes by opaque node ID. Keep
            // this fixture's A→root→B proof aligned with that order; inventory
            // creates source IDs with random UUIDs, independent of source name.
            if (string.CompareOrdinal(sources[sourceA].Id, sources[sourceB].Id) > 0)
                (sourceA, sourceB) = (sourceB, sourceA);
            Assert.True(string.CompareOrdinal(sources[sourceA].Id, sources[sourceB].Id) < 0);
            var root = (await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
                new(TopologyNodeKind.Service, "r08-root", owner, true, []), token)).Node;
            Assert.NotNull(root);
            await CreateDeclaredAsync(fixture.Factory, scope, sources[sourceA].Id, root.Id, token);
            await CreateDeclaredAsync(fixture.Factory, scope, root.Id, sources[sourceA].Id, token);
            await CreateDeclaredAsync(fixture.Factory, scope, root.Id, sources[sourceB].Id, token);
        }

        using (var ingest = fixture.Open())
        {
            await ingest.RecoverAsync(token);
            await fixture.EmitAsync(ingest, TelemetryDbFixture.Traces(sourceA, fixture.Now), TelemetrySignal.Traces);
        }
        await new EventWriter(fixture.Storage).WriteEventsAsync(
            [Degraded(owner, sourceA, now.AddMinutes(1)), Degraded(owner, sourceB, now.AddMinutes(2))], token);
        var graph = new TopologyGraphQueryService(RealSnapshotSource(fixture.Factory, fixture.Storage));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph);
        var window = new RcaWindow
        {
            BaselineFrom = now.AddDays(-7), BaselineTo = now.AddMinutes(-1),
            From = now, To = now.AddMinutes(15), OwnerGroups = [owner],
        };
        return (query, window, scope);
    }

    private static IEvidenceProvider Provider(string id, IScopedQuery query, TopologyProviderBudget budget) =>
        id == "topology.graph-path" ? new TopologyGraphPathProvider(query, budget) :
        new TopologyCommonAncestorProvider(query, budget);

    private static LogEvent Degraded(string owner, string source, DateTimeOffset timestamp) => new()
    {
        EventId = Guid.NewGuid(), Timestamp = timestamp, OwnerGroup = owner, SourceId = source, Host = source,
        Vendor = "r08", Product = "test", ParserId = "r08", ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok, SignatureHash = 1, TimeSource = TimeSources.Parsed,
        SeverityNum = 3, SrcIp = IPAddress.IPv6Any, DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal), Body = "r08 degraded",
    };

    private static async Task CreateDeclaredAsync(IDbContextFactory<ControlPlaneDbContext> factory,
        AccessScope scope, string from, string to, CancellationToken token)
    {
        var registry = Construct("Bizigo.ControlPlane.TopologyEdgeRegistry, Bizigo.ControlPlane", factory, null!);
        var inputType = Require("Bizigo.Contracts.TopologyDeclaredEdgeInput, Bizigo.Contracts");
        var input = Activator.CreateInstance(inputType, from, to, "depends_on", null, null)!;
        var method = registry.GetType().GetMethod("CreateAsync")!;
        var call = (Task)method.Invoke(registry, [scope, true, input, token])!;
        await call;
        var result = call.GetType().GetProperty("Result")!.GetValue(call)!;
        Assert.Equal(201, (int)result.GetType().GetProperty("Status")!.GetValue(result)!);
    }

    private static ITopologyGraphSnapshotSource RealSnapshotSource(
        IDbContextFactory<ControlPlaneDbContext> factory, ClickHouseContext clickHouse)
    {
        var observed = Construct("Bizigo.Storage.ClickHouse.TopologyObservedSnapshotReader, Bizigo.Storage.ClickHouse", clickHouse);
        var watermark = Construct("Bizigo.Storage.ClickHouse.TopologyPublicationWatermarkReader, Bizigo.Storage.ClickHouse", clickHouse);
        var revisions = Construct("Bizigo.Query.TopologyPublicationRevisionSource, Bizigo.Query", factory, watermark);
        var fence = Construct("Bizigo.Query.TopologyPublicationFence, Bizigo.Query", revisions);
        return (ITopologyGraphSnapshotSource)Construct("Bizigo.Query.TopologyGraphSnapshotSource, Bizigo.Query",
            factory, observed, fence);
    }

    private static Type Require(string assemblyQualifiedName) => Type.GetType(assemblyQualifiedName, throwOnError: true)!;
    private static object Construct(string type, params object[] args) => Activator.CreateInstance(Require(type), args)!;
}
