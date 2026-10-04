using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Q08/B04: the one-day cases place the admission event within the default
/// 24-hour observed window at E-1ns. This isolates logical expiry from event
/// window exclusion, while ClickHouse TTL still has two hours before cleanup.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyFinalExpiryOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(2, 2, 1)]
    public async Task Q08_B04_Physical_row_remains_but_minimum_captured_expiry_hides_all_graph_reads(
        int parentDays, int childDays, int observedDays)
    {
        await using var fixture = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var sourceA = "expiry-a-" + suffix;
        var sourceB = "expiry-b-" + suffix;
        var scopeA = AccessScope.ForGroups("expiry-A-" + suffix, ["A"]);
        var scopeAB = AccessScope.ForGroups("expiry-AB-" + suffix, ["A", "B"]);
        await fixture.SourceAsync(sourceA, "A");
        await fixture.SourceAsync(sourceB, "B");
        var registry = new TopologyRegistry(fixture.Factory);
        var root = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "expiry-root", "A", true, []), Ct)).Node!.Id;
        var parent = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "expiry-parent", "A", true,
                [new(sourceA, "n", "parent")]), Ct)).Node!.Id;
        var child = (await registry.CreateAsync(scopeAB, true,
            new(TopologyNodeKind.Service, "expiry-child", "B", true,
                [new(sourceB, "n", "child")]), Ct)).Node!.Id;
        Assert.Equal(201, (await new TopologyEdgeRegistry(fixture.Factory).CreateAsync(scopeAB, true,
            new(root, parent, "depends_on"), Ct)).Status);
        var start = fixture.Now - (ulong)TopologyExpiry.NanosecondsPerDay + 7_200_000_000_000UL;
        var trace = Guid.NewGuid().ToString("N");
        var p = Span(Guid.NewGuid(), trace, "1111111111111111", "", "p", sourceA, "A",
            parent, start, parentDays, 2);
        var c = Span(Guid.NewGuid(), trace, "2222222222222222", "1111111111111111", "c",
            sourceB, "B", child, start + 1000, childDays, observedDays);
        var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
        var publisher = new TopologyPublicationCoordinator(fixture.Factory, watermark,
            new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
        var writer = new TelemetryWriter(fixture.Storage, fixture.Owners,
            new TopologyObservedProjector(fixture.Storage, publisher.PublishAsync,
                readPendingKey: publisher.ReadPendingKeyAsync));
        await writer.WriteAsync([p, c], Ct);

        var parentExpiry = (decimal)start + (decimal)parentDays * TopologyExpiry.NanosecondsPerDay;
        var childExpiry = (decimal)(start + 1000) + (decimal)childDays * TopologyExpiry.NanosecondsPerDay;
        var observedExpiry = (decimal)(start + 1000) + (decimal)observedDays * TopologyExpiry.NanosecondsPerDay;
        var expiry = Math.Min(parentExpiry, Math.Min(childExpiry, observedExpiry));
        var fence = new TopologyPublicationFence(new TopologyPublicationRevisionSource(fixture.Factory,
            new TopologyPublicationWatermarkReader(fixture.Storage),
            new TopologyObservedRepairReadiness(fixture.Factory, fixture.Storage)));
        var graph = new TopologyGraphQueryService(new TopologyGraphSnapshotSource(fixture.Factory,
            new TopologyObservedSnapshotReader(fixture.Storage), fence));
        var pre = await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry - 1,
            Provenance: TopologyProvenance.Observed)
            { ExpiryReadClockUnixNano = expiry - 1 }, scopeAB, Ct);
        var edge = Assert.Single(pre.Items);
        Assert.Equal(expiry, edge.EffectiveExpiry!.Value);
        var physical = await fixture.SqlAsync("SELECT toString(parent_trace_expiry), "
            + "toString(child_trace_expiry), toString(observed_expiry), toString(expires_nano) "
            + "FROM topology_edges_observed WHERE edge_id = '" + edge.Id + "' ORDER BY publication_seq DESC LIMIT 1");
        var stored = physical.Trim().Split('\t').Select(value => decimal.Parse(value,
            CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(new[] { parentExpiry, childExpiry, observedExpiry, expiry }, stored);

        foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
        {
            var before = clock < expiry;
            var page = await graph.SearchEdgesAsync(new TopologyEdgeQuery(clock,
                Provenance: TopologyProvenance.Observed)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(before, page.Items.Any(item => item.Id == edge.Id));
            Assert.Equal(before, await graph.GetEdgeAtExpiryAsync(edge.Id, clock,
                null, null, null, clock, scopeAB, null, 200, Ct) is not null);
            var neighborhood = await graph.NeighborhoodAsync(new TopologyNeighborhoodQuery(parent, clock)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(before, neighborhood.Neighbors.Any(item => item.EdgeId == edge.Id));
            var path = await graph.PathAsync(new TopologyPathQuery(parent, child, clock)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(before ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                path.Status);
            var ancestor = await graph.CommonAncestorAsync(new TopologyCommonAncestorQuery([parent, child], clock)
                { ExpiryReadClockUnixNano = clock }, scopeAB, Ct);
            Assert.Equal(before ? TopologyGraphResultStatus.Found : TopologyGraphResultStatus.Unreachable,
                ancestor.Status);
            if (before) Assert.Equal(root, ancestor.NodeId);
            var outside = await graph.CountExternalNeighborsAsync(new TopologyNeighborhoodQuery(parent, clock)
                { ExpiryReadClockUnixNano = clock }, scopeA, Ct);
            Assert.Equal(before ? 1 : 0, outside.Count);
            Assert.Null(outside.Reason);
        }
        // Scoped production path uses its server-authoritative decimal nano
        // clock, never caller asOf, even between DateTimeOffset ticks.
        foreach (var clock in new[] { expiry - 1, expiry, expiry + 1 })
        {
            var scoped = ScopedAt(fixture, graph, clock);
            Assert.Equal(clock < expiry, (await scoped.SearchTopologyEdgesAsync(new(clock,
                Provenance: TopologyProvenance.Observed), scopeAB, Ct)).Items.Any(item => item.Id == edge.Id));
        }
        Assert.Empty((await ScopedAt(fixture, graph, expiry + 1).SearchTopologyEdgesAsync(new(expiry - 1,
            Provenance: TopologyProvenance.Observed), scopeAB, Ct)).Items);
        var historicalFrom = (decimal)start - 1;
        var historicalTo = (decimal)(start + 1000) + 1;
        Assert.Empty((await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry + 1,
            Provenance: TopologyProvenance.Observed,
            FromUnixNano: historicalFrom, ToUnixNano: historicalTo)
            { ExpiryReadClockUnixNano = expiry + 1 }, scopeAB, Ct)).Items);
        Assert.Equal(TopologyGraphResultStatus.Unreachable, (await graph.PathAsync(
            new TopologyPathQuery(parent, child, expiry + 1,
                FromUnixNano: historicalFrom, ToUnixNano: historicalTo)
                { ExpiryReadClockUnixNano = expiry + 1 }, scopeAB, Ct)).Status);
        Assert.Equal("1", (await fixture.SqlAsync("SELECT count() FROM topology_edges_observed "
            + "WHERE edge_id = '" + edge.Id + "'")).Trim());

        // A new HTTP envelope under a 365-day config remains the same semantic
        // edge/proof, but cannot lengthen the original admission-captured TTL.
        await writer.WriteAsync([Reenvelope(p, 365), Reenvelope(c, 365)], Ct);
        Assert.Empty((await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry + 1,
            Provenance: TopologyProvenance.Observed)
            { ExpiryReadClockUnixNano = expiry + 1 }, scopeAB, Ct)).Items);
        Assert.Equal(edge.Id, Assert.Single((await graph.SearchEdgesAsync(new TopologyEdgeQuery(expiry - 1,
            Provenance: TopologyProvenance.Observed)
            { ExpiryReadClockUnixNano = expiry - 1 }, scopeAB, Ct)).Items).Id);
    }

    private static IScopedQuery ScopedAt(TelemetryDbFixture fixture, TopologyGraphQueryService graph,
        decimal expiryClock) => new ScopedQuery(new(fixture.Storage), new(fixture.Storage),
        new(fixture.Storage), new(fixture.Storage), fixture.Db, new ControlPlaneAuditSink(fixture.Factory),
        fixture.Reader, graph, topologyClock: new FakeTimeProvider(new DateTimeOffset(
            DateTime.UnixEpoch.AddTicks(checked((long)(expiryClock / 100m))), TimeSpan.Zero)),
        expiryNanoClock: new FixedTopologyExpiryNanoClock(expiryClock));

    private static TelemetryRecord Reenvelope(TelemetryRecord record, int retentionDays)
    {
        var envelope = Guid.NewGuid();
        return record with
        {
            EnvelopeId = envelope,
            LogicalId = envelope.ToString("N") + "/" + record.Owner.LeafKey,
            RetentionDays = retentionDays,
            ObservedRetentionDays = retentionDays,
        };
    }

    private static TelemetryRecord Span(Guid envelope, string trace, string span, string parentSpan, string leaf,
        string source, string owner, string node, ulong start, int retentionDays, int observedDays)
    {
        var raw = JsonDocument.Parse("{\"traceId\":\"" + trace + "\",\"spanId\":\"" + span
            + "\",\"parentSpanId\":\"" + parentSpan + "\",\"startTimeUnixNano\":\"" + start
            + "\",\"endTimeUnixNano\":\"" + (start + 1) + "\",\"name\":\"op\",\"kind\":2}").RootElement.Clone();
        var binding = new TelemetryOwnerBinding(leaf, source, owner, 7, start, "known");
        var topology = new TopologyLeafBinding(leaf, start, source, owner, 7,
            node, null, 5, null, 9, "display", "Resolved");
        return new TelemetryRecord(1, envelope, envelope.ToString("N") + "/" + leaf,
            TelemetrySignal.Traces, new string('a', 64), new string('b', 64), binding, start,
            "op", "svc", "Client", "", 0, false, trace, span, 1, new string('c', 64),
            JsonDocument.Parse("{}").RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone(),
            "", "", null, raw)
        {
            Topology = topology, TopologyBindingsSha256 = new string('d', 64),
            RetentionDays = retentionDays, ObservedRetentionDays = observedDays,
        };
    }
}
