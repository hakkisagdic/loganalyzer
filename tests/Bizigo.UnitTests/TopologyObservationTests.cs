using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

public sealed class TopologyObservationTests
{
    private const string Trace = "00112233445566778899aabbccddeeff";
    private const string ParentSpan = "0011223344556677";
    private const string ChildSpan = "8899aabbccddeeff";
    private const ulong Day = 86_400_000_000_000;
    private static readonly string ParentNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly string ChildNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void Parent_direction_not_links_and_child_first_pairing()
    {
        var parent = Span(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "p", ParentSpan,
            string.Empty, ParentNode, 1000);
        var child = Span(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "c", ChildSpan,
            ParentSpan, ChildNode, 2000, extraSpan: "\"links\":[{\"spanId\":\"deadbeefdeadbeef\"}]");
        var first = TopologyObservation.Reduce([child, parent]);
        var replay = TopologyObservation.Reduce([parent, child, child]);
        var edge = Assert.Single(first.Edges);
        Assert.Equal(ParentNode, edge.Parent.NodeId);
        Assert.Equal(ChildNode, edge.Child.NodeId);
        Assert.Equal(edge.EdgeId, Assert.Single(replay.Edges).EdgeId);
        Assert.Equal(first.PublicationKey, replay.PublicationKey);
        Assert.Empty(first.Conflicts);
        Assert.Empty(TopologyObservation.Reduce([child]).Edges);
    }

    [Fact]
    public void Separate_http_occurrences_keep_one_topology_event_and_new_publication_key()
    {
        var parent = Span(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "p", ParentSpan,
            string.Empty, ParentNode, 1000);
        var child = Span(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "c", ChildSpan,
            ParentSpan, ChildNode, 2000);
        var againParent = parent with
        {
            EnvelopeId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            LogicalId = "cccccccccccccccccccccccccccccccc/p",
        };
        var againChild = child with
        {
            EnvelopeId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            LogicalId = "dddddddddddddddddddddddddddddddd/c",
        };
        var one = TopologyObservation.Reduce([parent, child]);
        var otherHttpOnly = TopologyObservation.Reduce([againParent, againChild]);
        var two = TopologyObservation.Reduce([parent, child, againParent, againChild]);
        Assert.Single(two.Edges);
        Assert.Equal(Assert.Single(one.Edges).EdgeId, Assert.Single(otherHttpOnly.Edges).EdgeId);
        Assert.Equal(Assert.Single(one.Edges).EdgeId, Assert.Single(two.Edges).EdgeId);
        Assert.Equal(2, Assert.Single(two.Edges).ParentOccurrences.Count);
        Assert.Equal(2, Assert.Single(two.Edges).ChildOccurrences.Count);
        Assert.NotEqual(one.PublicationKey, two.PublicationKey);
        Assert.NotEqual(parent.LogicalId, againParent.LogicalId);
        Assert.NotEqual(child.LogicalId, againChild.LogicalId);
    }

    [Fact]
    public void Conflicting_span_anchor_invalidates_prior_edge()
    {
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, 1000);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, 2000);
        Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        var conflict = parent with
        {
            EnvelopeId = Guid.NewGuid(),
            LogicalId = Guid.NewGuid().ToString("N") + "/p",
            Topology = parent.Topology! with { ServiceNodeId = ChildNode },
        };
        var reduced = TopologyObservation.Reduce([parent, child, conflict]);
        Assert.Empty(reduced.Edges);
        Assert.Equal(TopologySpanCandidate.FromRecord(parent)!.Anchor, Assert.Single(reduced.Conflicts).Anchor);
    }

    [Fact]
    public void Conflict_context_covers_every_captured_history_and_dependent_child()
    {
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, 1000);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, 2000);
        var alternate = parent with
        {
            EnvelopeId = Guid.NewGuid(), LogicalId = Guid.NewGuid().ToString("N") + "/p",
            Topology = parent.Topology! with { OwnerGroup = "B", SourceId = "source-B", ServiceNodeId = ChildNode },
            Owner = parent.Owner with { OwnerGroup = "B", SourceId = "source-B" },
        };
        var third = parent with
        {
            EnvelopeId = Guid.NewGuid(), LogicalId = Guid.NewGuid().ToString("N") + "/p",
            Topology = parent.Topology! with { ServiceNodeId = ChildNode },
        };
        var conflict = Assert.Single(TopologyObservation.Reduce([child, third, parent, alternate]).Conflicts);
        Assert.Equal(4, conflict.Candidates.Count);
        Assert.Contains(conflict.Candidates, item => item.OwnerGroup == "B" && item.IsConflictedAnchor);
        Assert.Contains(conflict.Candidates, item => item.NodeId == ChildNode && !item.IsConflictedAnchor);
        Assert.Equal(3, conflict.Candidates.Count(item => item.IsConflictedAnchor));
    }

    [Theory]
    [InlineData(30, 90, 90, 30)]
    [InlineData(90, 30, 90, 30)]
    [InlineData(90, 90, 10, 10)]
    public void Effective_expiry_uses_parent_child_and_observed_minimum(
        int parentDays, int childDays, int observedDays, int expectedDays)
    {
        const ulong start = 1_000_000_000_000;
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, start,
            retainedDays: parentDays);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, start,
            retainedDays: childDays, observedDays: observedDays);
        var edge = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        Assert.Equal((decimal)start + expectedDays * (decimal)Day, edge.EffectiveExpiry);
    }

    [Fact]
    public void Canonical_hash_frames_strings_and_sorts_object_keys()
    {
        Assert.Equal(TopologyCanonicalIdentity.Json(Parse("{\"b\":1,\"a\":[{\"y\":2,\"x\":1}]}")),
            TopologyCanonicalIdentity.Json(Parse("{\"a\":[{\"x\":1,\"y\":2}],\"b\":1}")));
        Assert.NotEqual(TopologyCanonicalIdentity.Hash("a", "bc"), TopologyCanonicalIdentity.Hash("ab", "c"));
        Assert.Equal("d0396a28be2928b6a808fdbeed2317ffa3651c193ed6f978fa6c256f755b6b32",
            TopologyCanonicalIdentity.Hash("trace-span-v1", Trace, ParentSpan));
    }

    [Fact]
    public void Durable_projection_manifest_round_trips_exact_edge_payload()
    {
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, 1000);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, 2000);
        var batch = TopologyObservation.Reduce([parent, child]);
        var payload = JsonSerializer.Serialize(batch, RawSignalCodec.Json);
        var replay = JsonSerializer.Deserialize<TopologyProjectionBatch>(payload, RawSignalCodec.Json);
        Assert.NotNull(replay);
        Assert.Equal(batch.PublicationKey, replay.PublicationKey);
        Assert.Equal(Assert.Single(batch.Edges).EdgeId, Assert.Single(replay.Edges).EdgeId);
        Assert.Equal(batch.Edges[0].EffectiveExpiry, replay.Edges[0].EffectiveExpiry);
        Assert.Equal(batch.Edges[0].Parent.NodeId, replay.Edges[0].Parent.NodeId);
        Assert.Equal(batch.Edges[0].Child.NodeId, replay.Edges[0].Child.NodeId);
        Assert.Equal(batch.Edges[0].ParentOccurrences, replay.Edges[0].ParentOccurrences);
        Assert.Equal(batch.Edges[0].ChildOccurrences, replay.Edges[0].ChildOccurrences);
    }

    private static TelemetryRecord Span(Guid envelopeId, string leaf, string spanId, string parentSpanId,
        string nodeId, ulong start, int retainedDays = 90, int observedDays = 90, string? extraSpan = null)
    {
        const string source = "source-A";
        const string owner = "A";
        var json = "{\"traceId\":\"" + Trace + "\",\"spanId\":\"" + spanId
            + "\",\"parentSpanId\":\"" + parentSpanId + "\",\"startTimeUnixNano\":\"" + start
            + "\",\"endTimeUnixNano\":\"" + (start + 1) + "\",\"name\":\"op\",\"kind\":2"
            + (extraSpan is null ? string.Empty : "," + extraSpan) + "}";
        var ownerBinding = new TelemetryOwnerBinding(leaf, source, owner, 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, source, owner, 7,
            nodeId, null, 5, null, 9, "display", "Resolved");
        return new(1, envelopeId, envelopeId.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), ownerBinding, start, "op", "svc", "Client", "",
            0, false, Trace, spanId, 1, new string('c', 64), Parse("{}"), Parse("{}"), "", "",
            null, Parse(json))
        {
            RetentionDays = retainedDays, ObservedRetentionDays = observedDays,
            Topology = topology, TopologyBindingsSha256 = new string('d', 64),
        };
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
