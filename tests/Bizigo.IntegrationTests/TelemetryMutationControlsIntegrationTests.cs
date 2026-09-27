using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Positive controls deliberately independent of the injected fault:
/// valid gauge data and actual current inventory must continue to work.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TelemetryMutationControlsIntegrationTests(DevStackFixture stack)
{
    [Fact, Trait("Category", "Integration")]
    public async Task Known_inventory_without_payload_claim_remains_visible()
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, token);
        await f.SourceAsync("positive", "A");
        var request = TelemetryDbFixture.Metrics("positive", f.Now, false);
        var attributes = request.ResourceMetrics[0].Resource.Attributes;
        attributes.Remove(attributes.Single(a => a.Key == "owner_group"));
        using var ingest = f.Open(); await ingest.RecoverAsync(token);
        await f.EmitAsync(ingest, request, TelemetrySignal.Metrics);
        var scope = AccessScope.ForGroups("positive", ["A"]);
        var page = await f.Query.SearchTelemetryAsync(f.Window(TelemetrySignal.Metrics), scope, token);
        Assert.Equal(TelemetryResultStatus.Data, page.Status); Assert.Single(page.Records);
        Assert.Equal("A", page.Records[0].Owner.OwnerGroup);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Single_and_CSV_current_source_writes_remain_functional()
    {
        var token = TestContext.Current.CancellationToken;
        var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            Assert.Equal(201, SourceOwnershipHistoryIntegrationTests.Status(await SourceOwnershipHistoryIntegrationTests.Upsert(db,
                new() { SourceId = "positive-single", OwnerGroup = "A" })));
            Assert.Equal(200, SourceOwnershipHistoryIntegrationTests.Status(await SourceOwnershipHistoryIntegrationTests.Csv(db, "positive-csv,B,host")));
        }
        await using var read = await factory.CreateDbContextAsync(token);
        Assert.Equal("A", (await read.Sources.SingleAsync(s => s.SourceId == "positive-single", token)).OwnerGroup);
        Assert.Equal("B", (await read.Sources.SingleAsync(s => s.SourceId == "positive-csv", token)).OwnerGroup);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Replay_duplicates_are_reduced_before_merge()
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, token);
        await f.SourceAsync("duplicate", "A");
        await f.SqlAsync("SYSTEM STOP MERGES metric_points");
        using var ingest = f.Open(); await ingest.RecoverAsync(token);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("duplicate", f.Now, false), TelemetrySignal.Metrics);
        await ingest.ReplayArchiveAsync(token); await ingest.ReplayArchiveAsync(token);
        Assert.Equal("3", (await f.SqlAsync("SELECT count() FROM metric_points")).Trim());
        var query = f.Window(TelemetrySignal.Metrics); var scope = AccessScope.ForGroups("dedup", ["A"]);
        Assert.Single((await f.Query.SearchTelemetryAsync(query, scope, token)).Records);
        Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, scope, token)).Count);
        Assert.Equal(1, Assert.Single((await f.Query.SummarizeTelemetryAsync(query, scope, token)).Groups).Count);
        await f.SqlAsync("SYSTEM START MERGES metric_points");
    }
}
