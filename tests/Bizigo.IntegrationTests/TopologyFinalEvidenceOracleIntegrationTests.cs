using System.Net;
using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Common.V1;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Real PG registry/declared history, CH typed projection and production RCA providers.
/// In particular the Service IDs must never be replaced by their Source-node IDs to
/// make a provider proof pass: that would hide the explicit contains mapping contract.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyFinalEvidenceOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R05_Mixed_declared_and_observed_proofs_remain_distinct_and_noncausal()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture, parallelDeclared: true);
        var edges = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Relation: TopologyRelation.DependsOn), seed.Scope, Ct);
        var pair = edges.Items.Where(edge => edge.FromNode == seed.Parent && edge.ToNode == seed.Child).ToArray();
        Assert.Equal(2, pair.Length);
        Assert.Equal(2, pair.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(pair, edge => edge.Provenance == TopologyProvenance.Declared && edge.Confidence == 1m);
        var observed = Assert.Single(pair, edge => edge.Provenance == TopologyProvenance.Observed);
        Assert.Equal(0.5m, observed.Confidence);
        Assert.Equal(seed.TraceId, Assert.Single((await seed.Query.GetTopologyEdgeAsync(
            observed.Id, seed.ReadClock, seed.Scope, Ct))!.Evidence.Where(e => e.TraceLogicalId == seed.TraceId
                && e.SpanLogicalId == seed.ChildSpanId)).TraceLogicalId);

        var stale = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: seed.ReadClock - 10 * TopologyExpiry.NanosecondsPerDay,
            ToUnixNano: seed.ReadClock - 9 * TopologyExpiry.NanosecondsPerDay), seed.Scope, Ct);
        Assert.DoesNotContain(stale.Items, edge => edge.Id == observed.Id);
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        Assert.Contains("not causal proof", slice.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(slice.Items);
    }

    [Fact]
    public async Task R05_RCA_window_excludes_old_observed_but_keeps_declared_state_at_window_end()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var owner = seed.Scope.OwnerGroups.Single();
        var middle = (await new TopologyRegistry(fixture.Factory).CreateAsync(seed.Scope, true,
            new(TopologyNodeKind.Service, "declared-window-middle", owner, true, []), Ct)).Node!.Id;
        var edges = new TopologyEdgeRegistry(fixture.Factory);
        Assert.Equal(201, (await edges.CreateAsync(seed.Scope, true,
            new(seed.Parent, middle, "depends_on"), Ct)).Status);
        Assert.Equal(201, (await edges.CreateAsync(seed.Scope, true,
            new(middle, seed.Child, "depends_on"), Ct)).Status);
        // The trace is near the original From; both degraded Source onsets are
        // later. A narrow RCA window still has two affected sources, but the
        // observed one-hop edge is outside [From,To). The current declared
        // two-hop path remains. A missing window filter would choose the stale
        // one-hop edge regardless of randomized edge-ID lexical ordering.
        var narrow = seed.Window with
        {
            From = seed.Window.From.AddSeconds(30),
            To = seed.Window.From.AddMinutes(5),
        };
        var clock = TopologyIdentity.Nano(narrow.To);
        var graph = await seed.Query.SearchTopologyEdgesAsync(new(clock,
            Relation: TopologyRelation.DependsOn,
            FromUnixNano: TopologyIdentity.Nano(narrow.From), ToUnixNano: clock), seed.Scope, Ct);
        Assert.DoesNotContain(graph.Items, edge => edge.Provenance == TopologyProvenance.Observed);
        Assert.Equal(2, graph.Items.Count(edge => edge.Provenance == TopologyProvenance.Declared
            && edge.Relation == TopologyRelation.DependsOn));
        var direct = await seed.Query.GetTopologyPathAsync(new(seed.Parent, seed.Child, clock,
            FromUnixNano: TopologyIdentity.Nano(narrow.From), ToUnixNano: clock), seed.Scope, Ct);
        Assert.Equal(new[] { seed.Parent, middle, seed.Child }, direct.Nodes);
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(narrow,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items);
        using var nodeProof = JsonDocument.Parse(item.Payload["node_ids"]);
        Assert.Equal(new[] { seed.Parent, middle, seed.Child }, nodeProof.RootElement.EnumerateArray()
            .Select(static node => node.GetString()!).ToArray());
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        Assert.Equal(2, proof.RootElement.GetArrayLength());
        Assert.All(proof.RootElement.EnumerateArray().ToArray(), edge =>
            Assert.Equal("declared", edge.GetProperty("provenance").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D07_Declared_edge_start_and_end_exactly_at_Window_To_use_prior_state(
        bool deleteAtTo)
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "to-boundary-a-" + suffix;
        var sourceB = "to-boundary-b-" + suffix;
        var owner = "to-boundary-" + suffix;
        var scope = AccessScope.ForGroups("to-boundary-actor-" + suffix, [owner]);
        await fixture.SourceAsync(sourceA, owner);
        await fixture.SourceAsync(sourceB, owner);
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var roots = await db.TopologyNodes.Where(node => node.SourceId == sourceA || node.SourceId == sourceB)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        var from = fixture.Clock.GetUtcNow();
        // Registry history is rounded to microseconds; use an aligned To so
        // the write lands exactly on the half-open snapshot boundary.
        var to = DateTimeOffset.FromUnixTimeMilliseconds(from.ToUnixTimeMilliseconds() + 10 * 60 * 1000);
        using (var ingest = fixture.Open())
        {
            await ingest.RecoverAsync(Ct);
            await fixture.EmitAsync(ingest, TelemetryDbFixture.Traces(sourceA, fixture.Now), TelemetrySignal.Traces);
            await fixture.EmitAsync(ingest, TelemetryDbFixture.Traces(sourceB, fixture.Now + 1000), TelemetrySignal.Traces);
        }
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded(owner, sourceA, from.AddMinutes(1)),
            Degraded(owner, sourceB, from.AddMinutes(2)),
        ], Ct);
        var createdAt = deleteAtTo ? from.AddMinutes(5) : to;
        var edge = await new TopologyEdgeRegistry(fixture.Factory, new FakeTimeProvider(createdAt))
            .CreateAsync(scope, true, new(roots[sourceA].Id, roots[sourceB].Id, "depends_on"), Ct);
        Assert.Equal(201, edge.Status);
        var edgeId = edge.Edge!.Id.ToString("D");
        if (deleteAtTo)
            Assert.Equal(204, (await new TopologyEdgeRegistry(fixture.Factory, new FakeTimeProvider(to))
                .DeleteAsync(scope, true, edge.Edge.Id, long.Parse(edge.Edge.Version,
                    CultureInfo.InvariantCulture), Ct)).Status);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            topologyClock: new FakeTimeProvider(to.AddTicks(10)));
        var boundary = TopologyIdentity.Nano(to);
        if (!deleteAtTo) Assert.Equal(boundary,
            decimal.Parse(edge.Edge.ValidFromUnixNano, CultureInfo.InvariantCulture));
        var prior = await query.SearchTopologyEdgesAsync(new(boundary - 1,
            Relation: TopologyRelation.DependsOn), scope, Ct);
        var afterBoundary = await query.SearchTopologyEdgesAsync(new(boundary,
            Relation: TopologyRelation.DependsOn), scope, Ct);
        Assert.Equal(deleteAtTo, prior.Items.Any(row => row.Id == edgeId));
        Assert.Equal(!deleteAtTo, afterBoundary.Items.Any(row => row.Id == edgeId));
        var atTo = new RcaWindow
        {
            BaselineFrom = from.AddDays(-1), BaselineTo = from.AddMinutes(-1),
            From = from, To = to, OwnerGroups = [owner],
        };
        var next = atTo with { To = to.AddTicks(10) };
        var provider = new TopologyGraphPathProvider(query);
        var priorSlice = await provider.GatherAsync(atTo, scope, GatherBudget.Default, Ct);
        Assert.Equal(deleteAtTo ? EvidenceStatus.Gathered : EvidenceStatus.Empty, priorSlice.Status);
        if (deleteAtTo)
        {
            using var proof = JsonDocument.Parse(Assert.Single(priorSlice.Items).Payload["proof_edges"]);
            Assert.Equal(edgeId, Assert.Single(proof.RootElement.EnumerateArray().ToArray())
                .GetProperty("id").GetString());
        }
        var after = await provider.GatherAsync(next, scope, GatherBudget.Default, Ct);
        Assert.Equal(deleteAtTo ? EvidenceStatus.Empty : EvidenceStatus.Gathered, after.Status);
        if (!deleteAtTo)
        {
            using var proof = JsonDocument.Parse(Assert.Single(after.Items).Payload["proof_edges"]);
            Assert.Equal(edgeId, Assert.Single(proof.RootElement.EnumerateArray().ToArray())
                .GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task E04_Wide_RCA_window_preserves_observed_proof_beyond_default_24h_detail_window()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var wide = seed.Window with { To = seed.Window.From.AddDays(2) };
        var clock = TopologyIdentity.Nano(wide.To);
        var explicitWindow = await seed.Query.SearchTopologyEdgesAsync(new(clock,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: TopologyIdentity.Nano(wide.From), ToUnixNano: clock), seed.Scope, Ct);
        Assert.Single(explicitWindow.Items); // precondition: published proof exists in RCA window
        Assert.Empty((await seed.Query.SearchTopologyEdgesAsync(new(clock,
            Provenance: TopologyProvenance.Observed), seed.Scope, Ct)).Items);
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(wide,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        using var proof = JsonDocument.Parse(Assert.Single(slice.Items).Payload["proof_edges"]);
        Assert.Equal("observed", Assert.Single(proof.RootElement.EnumerateArray().ToArray())
            .GetProperty("provenance").GetString());
    }

    [Fact]
    public async Task E02_Explicit_source_contains_service_mapping_is_required_for_affected_sources()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var sourceNodes = await seed.Query.ResolveTopologySourceNodesAsync(
            [seed.ParentSource, seed.ChildSource], seed.Scope, Ct);
        Assert.Equal(2, sourceNodes.Count);
        Assert.DoesNotContain(sourceNodes, node => node.NodeId == seed.Parent || node.NodeId == seed.Child);
        var contains = await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Relation: TopologyRelation.Contains, Provenance: TopologyProvenance.Declared), seed.Scope, Ct);
        Assert.Contains(contains.Items, edge => edge.FromNode == sourceNodes.Single(n =>
            n.SourceId == seed.ParentSource).NodeId && edge.ToNode == seed.Parent);
        Assert.Contains(contains.Items, edge => edge.FromNode == sourceNodes.Single(n =>
            n.SourceId == seed.ChildSource).NodeId && edge.ToNode == seed.Child);

        // This is the contract counterexample: affected immutable Source IDs resolve
        // to two distinct Service UUIDs via explicit contains; the typed graph edge
        // joins the Service UUIDs, not the Source-node UUIDs. Current provider code
        // that searches directly from source nodes must fail this named oracle.
        var proof = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, proof.Status);
        var item = Assert.Single(proof.Items);
        using var nodes = JsonDocument.Parse(item.Payload["node_ids"]);
        Assert.Equal(new[] { seed.Parent, seed.Child }, nodes.RootElement.EnumerateArray()
            .Select(static node => node.GetString()!).ToArray());
    }

    [Fact]
    public async Task E04_Observed_edge_proof_preserves_typed_reference_and_bounded_detail_continuation()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var observed = Assert.Single((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.Scope, Ct)).Items);
        var first = await seed.Query.GetTopologyEdgeAsync(observed.Id, seed.ReadClock, seed.Scope,
            null, 1, Ct);
        Assert.NotNull(first);
        Assert.Single(first.Evidence);
        Assert.NotNull(first.EvidenceCursor);
        var second = await seed.Query.GetTopologyEdgeAsync(observed.Id, seed.ReadClock, seed.Scope,
            first.EvidenceCursor, 1, Ct);
        Assert.NotNull(second);
        Assert.Single(second.Evidence);
        Assert.NotEqual(first.Evidence[0].Id, second.Evidence[0].Id);
        Assert.Null(second.EvidenceCursor);
        Assert.Equal(seed.Parent, observed.FromNode);
        Assert.Equal(seed.Child, observed.ToNode);
        Assert.Equal(0.5m, observed.Confidence);
        Assert.All(first.Evidence.Concat(second.Evidence), reference =>
            Assert.Equal(seed.TraceId, reference.TraceLogicalId));

        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items);
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        var edge = Assert.Single(proof.RootElement.EnumerateArray().ToArray());
        Assert.Equal(observed.Id, edge.GetProperty("id").GetString());
        Assert.Equal(seed.Parent, edge.GetProperty("from_node").GetString());
        Assert.Equal(seed.Child, edge.GetProperty("to_node").GetString());
        Assert.Equal("observed", edge.GetProperty("provenance").GetString());
        Assert.Equal(0.5m, edge.GetProperty("confidence").GetDecimal());
        Assert.True(edge.GetProperty("last_seen_unix_nano").GetDecimal() > 0);
    }

    [Fact]
    public async Task E02_Missing_immutable_source_identity_is_not_guessed_from_name_or_trace()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var unknown = "unregistered-" + Guid.NewGuid().ToString("N");
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded(seed.Scope.OwnerGroups.Single(), unknown, seed.Window.From.AddMinutes(3)),
        ], Ct);
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Unavailable, slice.Status);
        Assert.Empty(slice.Items);
        Assert.Equal("NotComparable", slice.Telemetry!.Evaluation);
        Assert.DoesNotContain(unknown, JsonSerializer.Serialize(slice), StringComparison.Ordinal);
    }

    [Fact]
    public async Task E08_Observed_proof_bundle_hash_and_detail_cursor_bind_effective_scope()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var provider = new TopologyGraphPathProvider(seed.Query);
        var first = await provider.GatherAsync(seed.Window, seed.Scope, GatherBudget.Default, Ct);
        var second = await provider.GatherAsync(seed.Window, seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, first.Status);
        Assert.Equal(first.Items.Select(i => i.Id), second.Items.Select(i => i.Id));
        var foreign = AccessScope.ForGroups("e08-foreign-" + Guid.NewGuid().ToString("N"), ["B"]);
        var outside = await provider.GatherAsync(seed.Window, foreign, GatherBudget.Default, Ct);
        Assert.Empty(outside.Items);

        static EvidenceBundle Bundle(EvidenceSlice slice, RcaWindow window, AccessScope scope) => new()
        {
            Id = Guid.NewGuid(), GatheredAt = window.To, Window = window,
            Scope = new(scope.OwnerGroups.Order(StringComparer.Ordinal).ToArray(), scope.IsUnrestricted),
            Slices = [slice], Trust = new(2, 0),
        };
        var bundleA = Bundle(first, seed.Window, seed.Scope);
        var bundleAReplay = Bundle(second, seed.Window, seed.Scope);
        var bundleB = Bundle(outside, seed.Window, foreign);
        var store = new EvidenceBundleStore(fixture.Factory);
        await store.SaveAsync(bundleA, Ct);
        await store.SaveAsync(bundleAReplay, Ct);
        await store.SaveAsync(bundleB, Ct);
        var reopened = new EvidenceBundleStore(new ControlPlaneFactory(stack.PostgresConnectionString));
        var loadedA = await reopened.GetAsync(bundleA.Id, Ct);
        var loadedReplay = await reopened.GetAsync(bundleAReplay.Id, Ct);
        var loadedB = await reopened.GetAsync(bundleB.Id, Ct);
        Assert.NotNull(loadedA); Assert.NotNull(loadedReplay); Assert.NotNull(loadedB);
        Assert.Equal(loadedA.ContentHash, loadedReplay.ContentHash);
        Assert.NotEqual(loadedA.ContentHash, loadedB.ContentHash);
        Assert.False(loadedA.Scope.IsReadableBy(foreign));

        var observed = Assert.Single((await seed.Query.SearchTopologyEdgesAsync(new(seed.ReadClock,
            Provenance: TopologyProvenance.Observed), seed.Scope, Ct)).Items);
        var detail = await seed.Query.GetTopologyEdgeAsync(observed.Id, seed.ReadClock, seed.Scope, null, 1, Ct);
        Assert.NotNull(detail); Assert.NotNull(detail.EvidenceCursor);
        await Assert.ThrowsAsync<TopologyCursorException>(() => seed.Query.GetTopologyEdgeAsync(
            observed.Id, seed.ReadClock, foreign, detail.EvidenceCursor, 1, Ct));
    }

    private sealed record Seed(IScopedQuery Query, AccessScope Scope, RcaWindow Window, decimal ReadClock,
        string ParentSource, string ChildSource, string Parent, string Child, string TraceId, string ChildSpanId);

    private static async Task<Seed> SeedAsync(TelemetryDbFixture fixture, bool parallelDeclared = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var owner = "evidence-" + suffix;
        var sourceA = "evidence-a-" + suffix;
        var sourceB = "evidence-b-" + suffix;
        var scope = AccessScope.ForGroups("evidence-actor-" + suffix, [owner]);
        await fixture.SourceAsync(sourceA, owner);
        await fixture.SourceAsync(sourceB, owner);
        var registry = new TopologyRegistry(fixture.Factory);
        var a = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "evidence-service-A", owner, true, [new(sourceA, "n", "checkout-a")]), Ct)).Node!;
        var b = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "evidence-service-B", owner, true, [new(sourceB, "n", "checkout-b")]), Ct)).Node!;
        var (parent, child, parentSource, childSource) = string.CompareOrdinal(a.Id, b.Id) < 0
            ? (a.Id, b.Id, sourceA, sourceB) : (b.Id, a.Id, sourceB, sourceA);
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var sourceNodes = await db.TopologyNodes.Where(n => n.SourceId == sourceA || n.SourceId == sourceB)
            .ToDictionaryAsync(n => n.SourceId!, Ct);
        var edgeRegistry = new TopologyEdgeRegistry(fixture.Factory);
        Assert.Equal(201, (await edgeRegistry.CreateAsync(scope, true,
            new(sourceNodes[parentSource].Id, parent, "contains"), Ct)).Status);
        Assert.Equal(201, (await edgeRegistry.CreateAsync(scope, true,
            new(sourceNodes[childSource].Id, child, "contains"), Ct)).Status);
        if (parallelDeclared)
            Assert.Equal(201, (await edgeRegistry.CreateAsync(scope, true,
                new(parent, child, "depends_on"), Ct)).Status);

        var publisher = new TopologyPublicationCoordinator(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyPublicationWatermarkWriter(new TopologyPublicationWatermarkReader(fixture.Storage), fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        var start = checked((ulong)Math.Max(decimal.Parse(a.ValidFromUnixNano, CultureInfo.InvariantCulture),
            decimal.Parse(b.ValidFromUnixNano, CultureInfo.InvariantCulture)) + 1_000_000_000UL);
        var parentExport = TelemetryDbFixture.Traces(parentSource, start);
        var childExport = TelemetryDbFixture.Traces(childSource, start + 1000);
        BindService(parentExport.ResourceSpans[0].Resource.Attributes, parentSource == sourceA
            ? "checkout-a" : "checkout-b");
        BindService(childExport.ResourceSpans[0].Resource.Attributes, childSource == sourceA
            ? "checkout-a" : "checkout-b");
        var parentSpan = parentExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = childExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        var trace = Convert.ToHexStringLower(parentSpan.TraceId.Span);
        var childSpanId = Convert.ToHexStringLower(childSpan.SpanId.Span);
        using (var ingest = fixture.Open(sink: writer))
        {
            await ingest.RecoverAsync(Ct);
            await fixture.EmitAsync(ingest, parentExport, TelemetrySignal.Traces);
            await fixture.EmitAsync(ingest, childExport, TelemetrySignal.Traces);
        }

        var now = fixture.Clock.GetUtcNow();
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded(owner, parentSource, now.AddMinutes(1)),
            Degraded(owner, childSource, now.AddMinutes(2)),
        ], Ct);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            topologyClock: new FakeTimeProvider(now.AddDays(3)));
        var window = new RcaWindow
        {
            BaselineFrom = now.AddDays(-7), BaselineTo = now.AddMinutes(-1),
            From = now, To = now.AddMinutes(15), OwnerGroups = [owner],
        };
        return new(query, scope, window, TopologyIdentity.Nano(window.To), parentSource,
            childSource, parent, child, trace, childSpanId);
    }

    private static void BindService(Google.Protobuf.Collections.RepeatedField<KeyValue> attributes, string service)
    {
        attributes.Single(attribute => attribute.Key == "service.name").Value.StringValue = service;
        attributes.Add(new KeyValue { Key = "service.namespace", Value = new AnyValue { StringValue = "n" } });
    }

    private static LogEvent Degraded(string owner, string source, DateTimeOffset at) => new()
    {
        EventId = Guid.NewGuid(), Timestamp = at, OwnerGroup = owner, SourceId = source, Host = source,
        Vendor = "evidence", Product = "test", ParserId = "evidence", ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok, SignatureHash = 1, TimeSource = TimeSources.Parsed,
        SeverityNum = 3, SrcIp = IPAddress.IPv6Any, DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal), Body = "degraded",
    };
}
