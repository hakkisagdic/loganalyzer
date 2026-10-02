using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only real CH storage assertions; no in-memory edge store.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TopologyProjectionIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Trace = "00112233445566778899aabbccddeeff";
    private const string ParentSpan = "0011223344556677";
    private const string ChildSpan = "8899aabbccddeeff";
    private static readonly string ParentNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly string ChildNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("55555555-5555-5555-5555-555555555555"));

    [Fact, Trait("Category", "Integration")]
    public async Task Parent_direction_not_links_and_out_of_order_durable_pairing()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var projector = new TopologyObservedProjector(f.Storage, published.PublishAsync);
        var writer = new TelemetryWriter(f.Storage, f.Owners, projector);
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, f.Now + 1000,
            "\"links\":[{\"traceId\":\"" + Trace + "\",\"spanId\":\"deadbeefdeadbeef\"}]");
        await writer.WriteAsync([child], Ct);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
        await writer.WriteAsync([parent], Ct);
        var edge = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(ParentNode + "\t" + ChildNode + "\t" + edge.EdgeId,
            (await f.SqlAsync("SELECT from_node_id,to_node_id,edge_id FROM topology_edges_observed FINAL FORMAT TSV")).Trim());
        Assert.Single(published.Keys);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Occurrence_retry_conflict_matrix()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync));
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await writer.WriteAsync([parent, child], Ct);
        var first = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        await writer.WriteAsync([parent, child], Ct); // same envelope: one raw logical occurrence
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        var againParent = Reenvelope(parent);
        var againChild = Reenvelope(child);
        await writer.WriteAsync([againParent, againChild], Ct);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(first.EdgeId,
            (await f.SqlAsync("SELECT edge_id FROM topology_edges_observed FINAL FORMAT TSV")).Trim());
        Assert.Equal(2, published.Keys.Distinct(StringComparer.Ordinal).Count());
        var conflict = Reenvelope(parent) with { Topology = parent.Topology! with { ServiceNodeId = ChildNode } };
        await writer.WriteAsync([conflict], Ct);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_span_conflicts FINAL")).Trim());
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        // Physical old edge remains for audit; the committed conflict marker
        // makes the public snapshot reject that anchor before any scope filter.
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Projector_crash_restart_reuses_publication_key_and_sequence()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var checkpoint = new FailOnceCheckpoint();
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync, checkpoint, published.ReadPendingKeyAsync));
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync([parent, child], Ct));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
        Assert.Empty(published.Keys); // insert occurred, but no publication ACK
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_projection_batches")).Trim());
        // New raw HTTP occurrence arrives while A is pending. Recovery must
        // finish the frozen A manifest before publishing cumulative A+B.
        var laterChild = Reenvelope(child);
        var recovered = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync,
                readPendingKey: published.ReadPendingKeyAsync));
        await recovered.WriteAsync([laterChild], Ct);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(2, published.Keys.Count);
        Assert.Equal(1UL, published.Sequences[0]);
        Assert.Equal(1UL, published.Sequences[1]); // callback retried under the same pending key
        Assert.Equal(2UL, published.Sequences[2]); // then the new cumulative batch
        Assert.Equal(published.AttemptedKeys[0], published.AttemptedKeys[1]);
        Assert.NotEqual(published.AttemptedKeys[1], published.AttemptedKeys[2]);
        Assert.Equal("2", (await f.SqlAsync("SELECT count(DISTINCT publication_key) FROM topology_projection_batches")).Trim());
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Pending_manifest_missing_fails_closed_before_current_batch()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var published = new Publication();
        var writer = new TelemetryWriter(f.Storage, f.Owners,
            new TopologyObservedProjector(f.Storage, published.PublishAsync,
                readPendingKey: _ => Task.FromResult<string?>(new string('e', 64))));
        var parent = Span(Guid.NewGuid(), "p", ParentSpan, string.Empty, ParentNode, f.Now);
        var child = Span(Guid.NewGuid(), "c", ChildSpan, ParentSpan, ChildNode, f.Now + 1000);
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync([parent, child], Ct));
        Assert.Empty(published.AttemptedKeys);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed")).Trim());
    }

    private static TelemetryRecord Reenvelope(TelemetryRecord record)
    {
        var id = Guid.NewGuid();
        return record with { EnvelopeId = id, LogicalId = id.ToString("N") + "/" + record.Owner.LeafKey };
    }

    private static TelemetryRecord Span(Guid envelopeId, string leaf, string spanId, string parentSpanId,
        string nodeId, ulong start, string? extraSpan = null)
    {
        var json = "{\"traceId\":\"" + Trace + "\",\"spanId\":\"" + spanId
            + "\",\"parentSpanId\":\"" + parentSpanId + "\",\"startTimeUnixNano\":\""
            + start.ToString(CultureInfo.InvariantCulture) + "\",\"endTimeUnixNano\":\""
            + (start + 1).ToString(CultureInfo.InvariantCulture) + "\",\"name\":\"op\",\"kind\":2"
            + (extraSpan is null ? string.Empty : "," + extraSpan) + "}";
        var owner = new TelemetryOwnerBinding(leaf, "SA", "A", 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, "SA", "A", 7,
            nodeId, null, 5, null, 9, "display", "Resolved");
        return new(1, envelopeId, envelopeId.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), owner, start, "op", "svc", "Client", "", 0, false,
            Trace, spanId, 1, new string('c', 64), Json("{}"), Json("{}"), "", "", null, Json(json))
        {
            Topology = topology, TopologyBindingsSha256 = new string('d', 64),
        };
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class FailOnceCheckpoint : ITopologyProjectionCheckpoints
    {
        private bool fail = true;
        public Task ReachAsync(string stage, CancellationToken cancellationToken)
        {
            Assert.Equal("after-observed-db-before-publish", stage);
            if (fail) { fail = false; throw new IOException("Simulated projector crash after CH insert."); }
            return Task.CompletedTask;
        }
    }

    private sealed class Publication
    {
        private string? pendingKey;
        private ulong pendingSequence;
        private ulong lastSequence;
        public List<string> AttemptedKeys { get; } = [];
        public List<ulong> Sequences { get; } = [];
        public List<string> Keys { get; } = [];
        public Task<string?> ReadPendingKeyAsync(CancellationToken token) => Task.FromResult(pendingKey);
        public async Task<ulong> PublishAsync(string key, Func<ulong, CancellationToken, Task> write,
            CancellationToken token)
        {
            if (pendingKey is null) { pendingKey = key; pendingSequence = lastSequence + 1; }
            else if (pendingKey != key) throw new InvalidOperationException("Different key cannot reuse pending sequence.");
            AttemptedKeys.Add(key); Sequences.Add(pendingSequence);
            await write(pendingSequence, token);
            lastSequence = pendingSequence; pendingKey = null; Keys.Add(key);
            return lastSequence;
        }
    }
}
