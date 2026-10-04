using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;
using ClickHouse.Driver.ADO;

namespace Bizigo.IntegrationTests;

/// <summary>Real CH driver hydration for the immutable pre-0014 40-column edge layout.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyObservedRowDigestIntegrationTests(DevStackFixture stack)
{
    private static readonly string Trace = "00112233445566778899aabbccddeeff";
    private static readonly string ParentSpan = "0011223344556677";
    private static readonly string ChildSpan = "8899aabbccddeeff";
    private static readonly string ParentNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly string ChildNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("55555555-5555-5555-5555-555555555555"));

    [Fact]
    public async Task Projected_row_roundtrips_through_real_driver_with_exact_digest_and_detects_payload_mismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, ct);
        var parent = Span(Guid.NewGuid(), "parent", ParentSpan, string.Empty, ParentNode, fixture.Now);
        var child = Span(Guid.NewGuid(), "child", ChildSpan, ParentSpan, ChildNode, fixture.Now + 1000);
        var edge = Assert.Single(TopologyObservation.Reduce([parent, child]).Edges);
        var sequence = 1UL;
        async Task<ulong> Publish(string key, Func<ulong, CancellationToken, Task> write, CancellationToken token)
        {
            await write(sequence, token);
            return sequence;
        }

        var projector = new TopologyObservedProjector(fixture.Storage, Publish);
        await new TelemetryWriter(fixture.Storage, fixture.Owners, projector).WriteAsync([parent, child], ct);

        // Use the actual frozen producer, not a second hand-maintained row mapper.
        var producer = typeof(TopologyObservedProjector).GetMethod("EdgeRow",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(producer);
        var writtenValues = Assert.IsType<object[]>(producer.Invoke(null, [edge, sequence]));
        var expectedDigest = TopologyObservedRowDigest.Compute(writtenValues);

        using var connection = new ClickHouseConnection(fixture.Storage.Options.ConnectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + TopologyObservedRowHydration.SelectList
            + " FROM topology_edges_observed WHERE edge_id = '" + edge.EdgeId + "'"
            + " AND publication_seq = 1 LIMIT 2";
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(TopologyObservedRowDigest.FrozenColumnCount, reader.FieldCount);
        var hydrated = TopologyObservedRowHydration.ReadValues(reader);
        Assert.False(await reader.ReadAsync(ct));
        Assert.Equal(expectedDigest, TopologyObservedRowDigest.Compute(hydrated));

        var tampered = (object?[])hydrated.Clone();
        tampered[35] = (decimal)tampered[35]! + 1;
        Assert.NotEqual(expectedDigest, TopologyObservedRowDigest.Compute(tampered));
        Assert.Equal(edge.EdgeId, hydrated[9]);
        Assert.Equal(edge.ParentOccurrences, Assert.IsType<string[]>(hydrated[29]));
        Assert.Equal(edge.ChildOccurrences, Assert.IsType<string[]>(hydrated[30]));
    }

    private static TelemetryRecord Span(Guid envelopeId, string leaf, string spanId, string parentSpanId,
        string nodeId, ulong start)
    {
        var span = "{\"traceId\":\"" + Trace + "\",\"spanId\":\"" + spanId
            + "\",\"parentSpanId\":\"" + parentSpanId + "\",\"startTimeUnixNano\":\""
            + start.ToString(CultureInfo.InvariantCulture) + "\",\"endTimeUnixNano\":\""
            + (start + 1).ToString(CultureInfo.InvariantCulture) + "\",\"name\":\"op\",\"kind\":2}";
        var owner = new TelemetryOwnerBinding(leaf, "SA", "A", 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, "SA", "A", 7,
            nodeId, null, 5, null, 9, "display", "Resolved");
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        return new(1, envelopeId, envelopeId.ToString("N") + "/" + leaf, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), owner, start, "op", "svc", "Client", "", 0, false,
            Trace, spanId, 1, new string('c', 64), Json("{}"), Json("{}"), "", "", null, Json(span))
        {
            Topology = topology, TopologyBindingsSha256 = new string('d', 64),
        };
    }
}
