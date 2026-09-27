using Bizigo.Contracts;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
public sealed class TelemetryScopeIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // kapsam: SearchTelemetryAsync
    // kapsam: CountTelemetryAsync
    // kapsam: CountOutOfScopeTelemetryAsync
    // kapsam: SummarizeTelemetryAsync
    // kapsam: GetMetricPointAsync
    // kapsam: GetTraceAsync
    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_resource_boundary_leaves_preserve_A_B_B_during_transfer_replay(bool traces)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("moving", "A", f.Clock.GetUtcNow().AddSeconds(-2));
        var boundary = checked((ulong)await f.SourceAsync("moving", "B", f.Clock.GetUtcNow().AddSeconds(-1)));
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        IMessage request;
        if (traces) request = TelemetryDbFixture.Traces("moving", boundary - 1, 3);
        else
        {
            var metrics = TelemetryDbFixture.Metrics("moving", boundary - 1, false);
            var points = metrics.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Gauge.DataPoints;
            var second = points[0].Clone(); second.TimeUnixNano = boundary;
            var third = points[0].Clone(); third.TimeUnixNano = boundary + 1;
            points.Add(second); points.Add(third); request = metrics;
        }
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var envelope = await f.EmitAsync(ingest, request, signal);
        Assert.Equal(new[] { "A", "B", "B" }, envelope.OwnerBindings!.Select(b => b.OwnerGroup));
        await ingest.ReplayArchiveAsync(Ct); await ingest.ReplayArchiveAsync(Ct);
        var query = f.Window(signal, "moving");
        var a = AccessScope.ForGroups("boundary-A", ["A"]);
        var b = AccessScope.ForGroups("boundary-B", ["B"]);
        var pageA = await f.Query.SearchTelemetryAsync(query, a, Ct);
        var pageB = await f.Query.SearchTelemetryAsync(query, b, Ct);
        Assert.Equal(boundary - 1, Assert.Single(pageA.Records).TimeUnixNano);
        Assert.Equal(new[] { boundary, boundary + 1 }, pageB.Records.Select(r => r.TimeUnixNano));
        Assert.All(pageA.Records, r => Assert.Equal("A", r.Owner.OwnerGroup));
        Assert.All(pageB.Records, r => Assert.Equal("B", r.Owner.OwnerGroup));
        Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, a, Ct)).Count);
        Assert.Equal(2, (await f.Query.CountOutOfScopeTelemetryAsync(query, a, Ct)).Count);
        Assert.Equal(1, Assert.Single((await f.Query.SummarizeTelemetryAsync(query, a, Ct)).Groups).Count);
        Assert.Equal(2, Assert.Single((await f.Query.SummarizeTelemetryAsync(query, b, Ct)).Groups).Count);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query with { OwnerGroups = ["B"] }, a, Ct)).Records);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.Denied, Ct)).Records);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("empty", []), Ct)).Records);
        Assert.Equal(3, (await f.Query.CountTelemetryAsync(query, AccessScope.System("explicit-admin"), Ct)).Count);
        if (traces)
        {
            var detail = await f.Query.GetTraceAsync(query with { TraceId = "00112233445566778899aabbccddeeff" }, a, Ct);
            Assert.Single(detail.Records); Assert.Equal(pageA.Records[0].LogicalId, detail.Records[0].LogicalId);
        }
        else
        {
            Assert.Single((await f.Query.GetMetricPointAsync(pageA.Records[0].LogicalId, a, Ct)).Records);
            Assert.Empty((await f.Query.GetMetricPointAsync(pageB.Records[0].LogicalId, a, Ct)).Records);
        }
        var audit = await f.Db.AuditLog.AsNoTracking().Where(r => r.Subject == "boundary-A").ToArrayAsync(Ct);
        Assert.NotEmpty(audit);
        Assert.All(audit, row =>
        {
            Assert.DoesNotContain("secret-for-moving", row.Details, StringComparison.Ordinal);
            Assert.DoesNotContain("forged-admin", row.Details, StringComparison.Ordinal);
            Assert.True(row.DurationMs >= 0);
        });
        TelemetryDbFixture.Evidence("boundary-" + signal, new { boundary, envelope.OwnerBindings, pageA, pageB, audit });
    }

    // kapsam: GetTelemetryFeedAsync
    [Fact, Trait("Category", "Integration")]
    public async Task Payload_owner_claim_and_unassigned_never_widen_authoritative_scope()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("known-A", "A"); await f.SourceAsync("known-B", "B");
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        foreach (var source in new[] { "known-A", "known-B", "unknown" })
            await f.EmitAsync(ingest, TelemetryDbFixture.Metrics(source, f.Now, false), TelemetrySignal.Metrics);
        var query = f.Window(TelemetrySignal.Metrics);
        foreach (var pair in new[] { ("A", "known-A"), ("B", "known-B"), ("_unassigned", "unknown") })
        {
            var scope = AccessScope.ForGroups("scope-" + pair.Item1, [pair.Item1]);
            var record = Assert.Single((await f.Query.SearchTelemetryAsync(query, scope, Ct)).Records);
            Assert.Equal(pair.Item2, record.Owner.SourceId); Assert.Equal(pair.Item1, record.Owner.OwnerGroup);
            Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        }
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("forged", ["forged-admin"]), Ct)).Records);
        var aOnly = AccessScope.ForGroups("feed-A", ["A"]);
        Assert.Equal(TelemetryResultStatus.Data, (await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Metrics, "known-A", aOnly, Ct)).Status);
        var hiddenFeed = await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Metrics, "known-B", aOnly, Ct);
        Assert.Equal(TelemetryResultStatus.NeverFed, hiddenFeed.Status); Assert.Equal(0, hiddenFeed.Count);
        Assert.Equal(TelemetryResultStatus.NeverFed, (await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Metrics, null, AccessScope.Denied, Ct)).Status);
        Assert.Equal(3, (await f.Query.CountTelemetryAsync(query, AccessScope.System("admin"), Ct)).Count);
    }
}
