using System.Globalization;
using System.Net;
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
/// Decision G: affected immutable Sources are groups of explicitly mapped
/// candidates, not guessed service labels and not a union of mandatory targets.
/// The Collector resolves Service/Instance aliases before WAL acknowledgement.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyMappedSourceGroupOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G_Path_evaluates_both_directions_all_cross_candidates_and_selects_one_shortest_proof()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var mid = await NodeAsync(fixture, seed.Scope, "longer-mid");
        await EdgeAsync(fixture, seed.Scope, seed.B2, seed.A1);
        await EdgeAsync(fixture, seed.Scope, seed.A2, mid);
        await EdgeAsync(fixture, seed.Scope, mid, seed.B1);
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items); // one finding per Source pair, not candidate pair
        using var nodes = JsonDocument.Parse(item.Payload["node_ids"]);
        Assert.Equal(new[] { seed.B2, seed.A1 }, nodes.RootElement.EnumerateArray()
            .Select(static node => node.GetString()!).ToArray());
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        var dependency = Assert.Single(proof.RootElement.EnumerateArray().ToArray());
        Assert.Equal(seed.B2, dependency.GetProperty("from_node").GetString());
        Assert.Equal(seed.A1, dependency.GetProperty("to_node").GetString());
        Assert.Equal("depends_on", dependency.GetProperty("relation").GetString());
        Assert.Equal("declared", dependency.GetProperty("provenance").GetString());
        AssertWitnesses(item.Payload["source_witnesses"],
            new(seed.SourceA, seed.A1, [seed.ContainsA1], [], []),
            new(seed.SourceB, seed.B2, [seed.ContainsB2], [], []));
        Assert.DoesNotContain("contains", item.Payload["proof_edges"], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G_Nonterminal_service_and_instance_are_both_eligible_mapped_candidates(bool instanceProof)
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture, admitInstance: instanceProof, projectDependency: true);
        var from = instanceProof ? seed.I1 : seed.A1;
        var slice = await new TopologyGraphPathProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        var item = Assert.Single(slice.Items);
        using var nodes = JsonDocument.Parse(item.Payload["node_ids"]);
        Assert.Equal(new[] { from, seed.B1 }, nodes.RootElement.EnumerateArray()
            .Select(static node => node.GetString()!).ToArray());
        Assert.Equal(instanceProof ? seed.I1 : seed.A1, seed.AdmittedA);
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        var dependency = Assert.Single(proof.RootElement.EnumerateArray().ToArray());
        Assert.Equal(from, dependency.GetProperty("from_node").GetString());
        Assert.Equal(seed.B1, dependency.GetProperty("to_node").GetString());
        Assert.Equal("observed", dependency.GetProperty("provenance").GetString());
        Assert.Equal("depends_on", dependency.GetProperty("relation").GetString());
        Assert.Equal(0.5m, dependency.GetProperty("confidence").GetDecimal());
        AssertWitnesses(item.Payload["source_witnesses"],
            new(seed.SourceA, from, instanceProof ? [seed.ContainsA1, seed.ContainsI1] : [seed.ContainsA1], [], []),
            new(seed.SourceB, seed.B1, [seed.ContainsB1], [], []));
    }

    [Fact]
    public async Task G_Ancestor_needs_one_positive_hop_witness_per_Source_not_all_candidate_union()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var seed = await SeedAsync(fixture);
        var x = await NodeAsync(fixture, seed.Scope, "grouped-x");
        var y = await NodeAsync(fixture, seed.Scope, "union-y");
        var aHub = await NodeAsync(fixture, seed.Scope, "union-a-hub");
        var bHub = await NodeAsync(fixture, seed.Scope, "union-b-hub");
        var xA1 = await EdgeAsync(fixture, seed.Scope, x, seed.A1);
        var xB1 = await EdgeAsync(fixture, seed.Scope, x, seed.B1);
        await EdgeAsync(fixture, seed.Scope, y, aHub);
        await EdgeAsync(fixture, seed.Scope, y, bHub);
        await EdgeAsync(fixture, seed.Scope, aHub, seed.A1);
        await EdgeAsync(fixture, seed.Scope, aHub, seed.A2);
        await EdgeAsync(fixture, seed.Scope, bHub, seed.B1);
        await EdgeAsync(fixture, seed.Scope, bHub, seed.B2);

        var grouped = await new TopologyCommonAncestorProvider(seed.Query).GatherAsync(seed.Window,
            seed.Scope, GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, grouped.Status);
        var item = Assert.Single(grouped.Items);
        Assert.Equal(x, item.Payload["ancestor_node_id"]);
        using var proof = JsonDocument.Parse(item.Payload["proof_edges"]);
        var edges = proof.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.All(edges, edge => Assert.Equal(x, edge.GetProperty("from_node").GetString()));
        Assert.Contains(edges, edge => edge.GetProperty("to_node").GetString() == seed.A1);
        Assert.Contains(edges, edge => edge.GetProperty("to_node").GetString() == seed.B1);
        AssertWitnesses(item.Payload["source_witnesses"],
            new(seed.SourceA, seed.A1, [seed.ContainsA1], [x, seed.A1], [xA1]),
            new(seed.SourceB, seed.B1, [seed.ContainsB1], [x, seed.B1], [xB1]));

        // Public REST/query ancestor still has explicit target-union semantics:
        // x misses a2/b2; y reaches all four through the separate hubs.
        var union = await seed.Query.GetTopologyCommonAncestorAsync(new(
            [seed.A1, seed.A2, seed.B1, seed.B2], TopologyIdentity.Nano(seed.Window.To)),
            seed.Scope, Ct);
        Assert.Equal(TopologyGraphResultStatus.Found, union.Status);
        Assert.Equal(y, union.NodeId);
    }

    [Fact]
    public async Task G_Root_only_complete_mapping_preserves_direct_Source_dependency_without_guessing_service()
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "root-a-" + suffix;
        var sourceB = "root-b-" + suffix;
        var owner = "root-" + suffix;
        var scope = AccessScope.ForGroups("root-actor-" + suffix, [owner]);
        await fixture.SourceAsync(sourceA, owner);
        await fixture.SourceAsync(sourceB, owner);
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var roots = await db.TopologyNodes.Where(node => node.SourceId == sourceA || node.SourceId == sourceB)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        var ordered = new[] { roots[sourceA].Id, roots[sourceB].Id }
            .Order(StringComparer.Ordinal).ToArray();
        await EdgeAsync(fixture, scope, ordered[0], ordered[1]);
        using (var ingest = fixture.Open())
        {
            await ingest.RecoverAsync(Ct);
            var raw = await fixture.EmitAsync(ingest,
                TelemetryDbFixture.Traces(sourceA, fixture.Now), TelemetrySignal.Traces);
            Assert.False(Assert.Single(raw.TopologyBindings!).Resolved);
        }
        var now = fixture.Clock.GetUtcNow();
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded(owner, sourceA, now.AddMinutes(1)),
            Degraded(owner, sourceB, now.AddMinutes(2)),
        ], Ct);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            topologyClock: new FakeTimeProvider(now.AddMinutes(20)));
        var window = new RcaWindow
        {
            BaselineFrom = now.AddDays(-7), BaselineTo = now.AddMinutes(-1),
            From = now, To = now.AddMinutes(15), OwnerGroups = [owner],
        };
        var slice = await new TopologyGraphPathProvider(query).GatherAsync(window, scope,
            GatherBudget.Default, Ct);
        Assert.Equal(EvidenceStatus.Gathered, slice.Status);
        using var nodes = JsonDocument.Parse(Assert.Single(slice.Items).Payload["node_ids"]);
        Assert.Equal(ordered, nodes.RootElement.EnumerateArray().Select(static n => n.GetString()!).ToArray());
    }

    private sealed record Seed(IScopedQuery Query, AccessScope Scope, RcaWindow Window,
        string SourceA, string SourceB, string A1, string A2, string B1, string B2,
        string I1, string AdmittedA, string ContainsA1, string ContainsB1,
        string ContainsB2, string ContainsI1);

    private static async Task<Seed> SeedAsync(TelemetryDbFixture fixture, bool admitInstance = false,
        bool projectDependency = false)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "group-a-" + suffix;
        var sourceB = "group-b-" + suffix;
        var owner = "group-" + suffix;
        var scope = AccessScope.ForGroups("group-actor-" + suffix, [owner]);
        await fixture.SourceAsync(sourceA, owner);
        await fixture.SourceAsync(sourceB, owner);
        var registry = new TopologyRegistry(fixture.Factory);
        var a1 = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "a1", owner, true, [new(sourceA, "n", "a1")]), Ct)).Node!;
        var a2 = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "a2", owner, true, [new(sourceA, "n", "a2")]), Ct)).Node!;
        var b1 = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "b1", owner, true, [new(sourceB, "n", "b1")]), Ct)).Node!;
        var b2 = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service,
            "b2", owner, true, [new(sourceB, "n", "b2")]), Ct)).Node!;
        var i1 = (await registry.CreateAsync(scope, true, new(TopologyNodeKind.ServiceInstance,
            "a1-i1", owner, true, [new(sourceA, "n", "a1", a1.Id, "i1")]), Ct)).Node!;
        await using var db = await fixture.Factory.CreateDbContextAsync(Ct);
        var roots = await db.TopologyNodes.Where(node => node.SourceId == sourceA || node.SourceId == sourceB)
            .ToDictionaryAsync(node => node.SourceId!, Ct);
        var containsA1 = await ContainsAsync(fixture, scope, roots[sourceA].Id, a1.Id);
        await ContainsAsync(fixture, scope, roots[sourceA].Id, a2.Id);
        var containsI1 = await ContainsAsync(fixture, scope, a1.Id, i1.Id);
        var containsB1 = await ContainsAsync(fixture, scope, roots[sourceB].Id, b1.Id);
        var containsB2 = await ContainsAsync(fixture, scope, roots[sourceB].Id, b2.Id);

        var latestBinding = new[] { a1, a2, b1, b2, i1 }
            .Max(version => decimal.Parse(version.ValidFromUnixNano, CultureInfo.InvariantCulture));
        var eventTime = checked((ulong)latestBinding + 1_000_000_000UL);
        var requestA = TelemetryDbFixture.Traces(sourceA, eventTime);
        var requestB = TelemetryDbFixture.Traces(sourceB, eventTime + 1000);
        SetService(requestA.ResourceSpans[0].Resource.Attributes, "a1", admitInstance ? "i1" : null);
        SetService(requestB.ResourceSpans[0].Resource.Attributes, "b1", null);
        var parentSpan = requestA.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = requestB.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        string admitted;
        var publisher = new TopologyPublicationCoordinator(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyPublicationWatermarkWriter(new TopologyPublicationWatermarkReader(fixture.Storage),
                fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        using (var ingest = fixture.Open(sink: projectDependency ? writer : null))
        {
            await ingest.RecoverAsync(Ct);
            var envelopeA = await fixture.EmitAsync(ingest, requestA, TelemetrySignal.Traces);
            var envelopeB = await fixture.EmitAsync(ingest, requestB, TelemetrySignal.Traces);
            admitted = Assert.Single(envelopeA.TopologyBindings!).NodeId!;
            Assert.Equal(b1.Id, Assert.Single(envelopeB.TopologyBindings!).NodeId);
        }
        var now = fixture.Clock.GetUtcNow();
        await new EventWriter(fixture.Storage).WriteEventsAsync([
            Degraded(owner, sourceA, now.AddMinutes(1)),
            Degraded(owner, sourceB, now.AddMinutes(2)),
        ], Ct);
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory), fixture.Reader, graph,
            topologyClock: new FakeTimeProvider(now.AddMinutes(20)));
        var window = new RcaWindow
        {
            BaselineFrom = now.AddDays(-7), BaselineTo = now.AddMinutes(-1),
            From = now, To = now.AddMinutes(15), OwnerGroups = [owner],
        };
        return new(query, scope, window, sourceA, sourceB, a1.Id, a2.Id, b1.Id, b2.Id,
            i1.Id, admitted, containsA1, containsB1, containsB2, containsI1);
    }

    private static void SetService(Google.Protobuf.Collections.RepeatedField<KeyValue> attributes,
        string name, string? instance)
    {
        attributes.Single(attribute => attribute.Key == "service.name").Value.StringValue = name;
        attributes.Add(new KeyValue { Key = "service.namespace", Value = new AnyValue { StringValue = "n" } });
        if (instance is not null)
            attributes.Add(new KeyValue { Key = "service.instance.id",
                Value = new AnyValue { StringValue = instance } });
    }

    private static async Task<string> NodeAsync(TelemetryDbFixture fixture, AccessScope scope, string name)
    {
        var result = await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
            new(TopologyNodeKind.Service, name, scope.OwnerGroups.Single(), true, []), Ct);
        Assert.Equal(201, result.Status);
        return result.Node!.Id;
    }

    private static async Task<string> ContainsAsync(TelemetryDbFixture fixture, AccessScope scope,
        string from, string to)
    {
        var result = await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(from, to, "contains"), Ct);
        Assert.Equal(201, result.Status);
        return result.Edge!.Id.ToString();
    }

    private static async Task<string> EdgeAsync(TelemetryDbFixture fixture, AccessScope scope,
        string from, string to)
    {
        var result = await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scope, true,
            new(from, to, "depends_on"), Ct);
        Assert.Equal(201, result.Status);
        return result.Edge!.Id.ToString();
    }

    private sealed record ExpectedWitness(string SourceId, string TargetNodeId,
        string[] MappingEdgeIds, string[] Nodes, string[] EdgeIds);

    private static void AssertWitnesses(string json, params ExpectedWitness[] expected)
    {
        using var document = JsonDocument.Parse(json);
        var actual = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        var ordered = expected.OrderBy(witness => witness.SourceId, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var witness = actual[index];
            Assert.Equal(ordered[index].SourceId, witness.GetProperty("source_id").GetString());
            Assert.Equal(ordered[index].TargetNodeId, witness.GetProperty("target_node_id").GetString());
            Assert.Equal(ordered[index].MappingEdgeIds, Strings(witness, "mapping_edge_ids"));
            Assert.Equal(ordered[index].Nodes, Strings(witness, "nodes"));
            Assert.Equal(ordered[index].EdgeIds, Strings(witness, "edge_ids"));
        }
    }

    private static string[] Strings(JsonElement element, string field) => element.GetProperty(field)
        .EnumerateArray().Select(static value => value.GetString()!).ToArray();

    private static LogEvent Degraded(string owner, string source, DateTimeOffset at) => new()
    {
        EventId = Guid.NewGuid(), Timestamp = at, OwnerGroup = owner, SourceId = source, Host = source,
        Vendor = "group", Product = "test", ParserId = "group", ParserVersion = "1.0.0",
        ParseStatus = ParseStatus.Ok, SignatureHash = 1, TimeSource = TimeSources.Parsed,
        SeverityNum = 3, SrcIp = IPAddress.IPv6Any, DstIp = IPAddress.IPv6Any,
        Attrs = new Dictionary<string, string>(StringComparer.Ordinal), Body = "degraded",
    };
}
