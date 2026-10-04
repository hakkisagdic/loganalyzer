using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.IntegrationTests;

/// <summary>Later B-owned display changes cannot rewrite A's admitted event-time identity.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyHistoricalDisplayOracleIntegrationTests(DevStackFixture stack)
{
    [Fact]
    public async Task D07_B_rename_keeps_old_A_event_binding_and_public_display_snapshot()
    {
        var token = TestContext.Current.CancellationToken;
        var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
        var clickHouse = await DevStackSetup.ClickHouseAsync(stack, token);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(1));
        var sourceId = "d07-source-" + Guid.NewGuid().ToString("N")[..12];
        var scopeA = AccessScope.ForGroups("d07-reader-A", ["A"]);
        var scopeB = AccessScope.ForGroups("d07-reader-B", ["B"]);
        var scopeAB = AccessScope.ForGroups("d07-admin-AB", ["A", "B"]);
        await using (var seed = await factory.CreateDbContextAsync(token))
        {
            seed.HistoryClock = clock;
            seed.Sources.Add(new SourceEntity { SourceId = sourceId, OwnerGroup = "A" });
            await seed.SaveChangesAsync(token);
        }

        var registry = new TopologyRegistry(factory, clock);
        var oldInput = new TopologyNodeInput(TopologyNodeKind.Service, "A-original", "A", true,
            [new TopologyAliasInput(sourceId, "ns", "checkout")]);
        var created = await registry.CreateAsync(scopeA, true, oldInput, token);
        Assert.Equal(201, created.Status);
        var nodeId = created.Node!.Id;
        var oldEventNano = checked(ulong.Parse(created.Node.ValidFromUnixNano, CultureInfo.InvariantCulture) + 1000);

        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var transferSource = await factory.CreateDbContextAsync(token))
        {
            transferSource.HistoryClock = clock;
            (await transferSource.Sources.SingleAsync(s => s.SourceId == sourceId, token)).OwnerGroup = "B";
            await transferSource.SaveChangesAsync(token);
        }
        var transferredInput = oldInput with { OwnerGroup = "B", Version = 1 };
        var transferred = await registry.UpdateAsync(scopeAB, true, nodeId, transferredInput, token);
        Assert.Equal(200, transferred.Status);

        clock.Advance(TimeSpan.FromSeconds(1));
        var renamed = await registry.UpdateAsync(scopeB, true, nodeId,
            transferredInput with { DisplayName = "B-renamed", Version = 2 }, token);
        Assert.Equal(200, renamed.Status);
        var currentNano = decimal.Parse(renamed.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture) + 1000;

        // The production admission resolvers must still return the old owner,
        // immutable node ID and A-era name for an event preceding the transfer.
        var owner = Assert.Single(await new HistoricalTelemetryOwners(factory).ResolveAsync(
            [new("old-A-event", oldEventNano, [sourceId])], token));
        Assert.Equal("A", owner.OwnerGroup);
        var binding = Assert.Single(await new HistoricalTopologyBindings(factory).ResolveAsync(
            [new(owner, "ns", "checkout", null)], token));
        Assert.Equal("Resolved", binding.Reason);
        Assert.Equal(nodeId, binding.NodeId);
        Assert.Equal("A-original", binding.DisplayName);

        // This is the same production PG-history + CH-watermark source used by
        // public graph reads, not a current-row or hand-built projection.
        var watermark = new TopologyPublicationWatermarkReader(clickHouse);
        var revisions = new TopologyPublicationRevisionSource(factory, watermark,
            new TopologyObservedRepairReadiness(factory, clickHouse));
        var source = new TopologyGraphSnapshotSource(factory,
            new TopologyObservedSnapshotReader(clickHouse), new TopologyPublicationFence(revisions));
        var graph = new TopologyGraphQueryService(source);
        var historicalA = await graph.GetNodeAsync(nodeId, oldEventNano, scopeA, token);
        Assert.NotNull(historicalA);
        Assert.Equal("A", historicalA.OwnerGroup);
        Assert.Equal("A-original", historicalA.DisplayName);
        Assert.Null(await graph.GetNodeAsync(nodeId, oldEventNano, scopeB, token));
        Assert.Null(await graph.GetNodeAsync(nodeId, currentNano, scopeA, token));
        var currentB = await graph.GetNodeAsync(nodeId, currentNano, scopeB, token);
        Assert.NotNull(currentB);
        Assert.Equal("B", currentB.OwnerGroup);
        Assert.Equal("B-renamed", currentB.DisplayName);

        await using var check = await factory.CreateDbContextAsync(token);
        var history = await check.TopologyNodeHistory.AsNoTracking().Where(h => h.NodeId == nodeId)
            .OrderBy(h => h.FromNano).ToArrayAsync(token);
        Assert.Equal(new[] { "A-original", "A-original", "B-renamed" }, history.Select(h => h.DisplayName));
        Assert.Equal(new[] { "A", "B", "B" }, history.Select(h => h.OwnerGroup));
        Assert.Equal(history[0].ToNano, history[1].FromNano);
        Assert.Equal(history[1].ToNano, history[2].FromNano);
    }
}
