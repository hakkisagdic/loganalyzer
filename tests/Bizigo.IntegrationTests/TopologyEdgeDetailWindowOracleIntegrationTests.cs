using System.Globalization;
using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyEdgeDetailWindowOracleIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private sealed record Seed(string EdgeId, string AsOf, string From, string To,
        string WiderFrom, string ExcludedFrom, string ExcludedTo);

    [Fact]
    public async Task Edge_detail_explicit_window_is_applied_and_invalid_or_unpaired_bounds_are_400()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var seed = await SeedAsync(f, api);
        var endpoint = "/v1/topology/edges/" + Uri.EscapeDataString(seed.EdgeId) + "?asOf=" + seed.AsOf;
        using var included = await api.GetAsync(endpoint + "&from=" + seed.From + "&to=" + seed.To);
        Assert.Equal(HttpStatusCode.OK, included.StatusCode);
        using var body = JsonDocument.Parse(await included.Content.ReadAsStringAsync(Ct));
        Assert.Equal(seed.EdgeId, body.RootElement.GetProperty("edge").GetProperty("id").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("evidence").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("evidence_cursor").ValueKind);
        using var excluded = await api.GetAsync(endpoint + "&from=" + seed.ExcludedFrom + "&to=" + seed.ExcludedTo);
        Assert.Equal(HttpStatusCode.NotFound, excluded.StatusCode);
        foreach (var invalid in new[]
        {
            "&from=" + seed.From, "&to=" + seed.To,
            "&from=invalid&to=" + seed.To, "&from=" + seed.From + "&to=invalid",
            "&from=" + seed.To + "&to=" + seed.From,
            "&from=" + seed.To + "&to=" + seed.To,
            "&from=" + seed.From + "&to=9999-01-01T00:00:00Z",
        })
        {
            using var response = await api.GetAsync(endpoint + invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        TelemetryDbFixture.Evidence("h03-edge-detail-window", new
        { seed, included = body.RootElement, excluded = 404, invalidCases = 7 });
    }

    [Fact]
    public async Task Edge_detail_cursor_binds_window_and_same_window_continuation_preserves_exact_evidence()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TopologyHttpOracleHost.StartAsync(f, Ct);
        var seed = await SeedAsync(f, api);
        var endpoint = "/v1/topology/edges/" + Uri.EscapeDataString(seed.EdgeId) + "?asOf=" + seed.AsOf;
        var window = "&from=" + seed.From + "&to=" + seed.To;
        using var full = await api.GetAsync(endpoint + window);
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        using var fullJson = JsonDocument.Parse(await full.Content.ReadAsStringAsync(Ct));
        var expected = fullJson.RootElement.GetProperty("evidence").EnumerateArray()
            .Select(e => e.GetRawText()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, expected.Length);
        using var first = await api.GetAsync(endpoint + window + "&evidencePageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync(Ct));
        var firstItem = Assert.Single(firstJson.RootElement.GetProperty("evidence").EnumerateArray());
        var cursor = firstJson.RootElement.GetProperty("evidence_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var suffix = "&evidencePageSize=1&evidenceCursor=" + Uri.EscapeDataString(cursor!);
        // Both windows contain the edge. A 400 proves cursor-window binding,
        // rather than a missing-edge response concealing an unbound cursor.
        using var changed = await api.GetAsync(endpoint + "&from=" + seed.WiderFrom + "&to=" + seed.To + suffix);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        using var next = await api.GetAsync(endpoint + window + suffix);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var nextJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync(Ct));
        Assert.Equal(seed.EdgeId, nextJson.RootElement.GetProperty("edge").GetProperty("id").GetString());
        var nextItem = Assert.Single(nextJson.RootElement.GetProperty("evidence").EnumerateArray());
        Assert.NotEqual(firstItem.GetProperty("id").GetString(), nextItem.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, nextJson.RootElement.GetProperty("evidence_cursor").ValueKind);
        Assert.Equal(expected, new[] { firstItem.GetRawText(), nextItem.GetRawText() }.Order(StringComparer.Ordinal));
        TelemetryDbFixture.Evidence("h03-edge-detail-window-cursor", new
        { seed, changedWindow = 400, sameWindow = 200, first = firstJson.RootElement, next = nextJson.RootElement });
    }

    private static async Task<Seed> SeedAsync(TelemetryDbFixture f, TopologyHttpOracleHost api)
    {
        await f.SourceAsync("detail-parent", "A"); await f.SourceAsync("detail-child", "A");
        var registry = new TopologyRegistry(f.Factory);
        var scope = AccessScope.ForGroups("window-seed", ["A"]);
        var parent = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "parent", "A", true,
            [new("detail-parent", "", "parent")]), Ct);
        var child = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "child", "A", true,
            [new("detail-child", "", "child")]), Ct);
        Assert.Equal(201, parent.Status); Assert.Equal(201, child.Status);
        var time = checked((ulong)Math.Max(decimal.Parse(parent.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture),
            decimal.Parse(child.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture)) + 1000UL);
        var p = TelemetryDbFixture.Traces("detail-parent", time);
        var c = TelemetryDbFixture.Traces("detail-child", time + 1000);
        p.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "parent";
        c.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "child";
        var parentSpan = p.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = c.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        using var ingest = f.Open(sink: api.App.Services.GetRequiredService<ITelemetrySink>());
        await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, p, TelemetrySignal.Traces);
        await f.EmitAsync(ingest, c, TelemetrySignal.Traces);
        await using var read = api.App.Services.CreateAsyncScope();
        var edges = await read.ServiceProvider.GetRequiredService<IScopedQuery>().SearchTopologyEdgesAsync(
            new(time + 2000000000m, Provenance: TopologyProvenance.Observed), scope, Ct);
        var edge = Assert.Single(edges.Items);
        Assert.Equal(parent.Node.Id, edge.FromNode); Assert.Equal(child.Node.Id, edge.ToNode);
        static string At(decimal nano) => Uri.EscapeDataString(DateTimeOffset.UnixEpoch.AddTicks(checked((long)(nano / 100)))
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        return new(edge.Id, At(time + 2000000000m), At(time - 1000000000m), At(time + 1000000000m),
            At(time - 2000000000m), At(time - 4000000000m), At(time - 3000000000m));
    }
}
