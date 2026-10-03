using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

public sealed partial class TopologyGraphDatabaseIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task Real_provider_DI_audits_scoped_path_ancestor_and_proof_reads()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(Stack, token);
        var (query, window, scope) = await SeedScenarioAsync(fixture, token);
        var fence = Fence(fixture);
        using var services = Services(query, fence).BuildServiceProvider();
        var path = await services.GetRequiredService<TopologyGraphPathProvider>()
            .GatherAsync(window, scope, GatherBudget.Default, token);
        var ancestor = await services.GetRequiredService<TopologyCommonAncestorProvider>()
            .GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.Equal(EvidenceStatus.Gathered, path.Status);
        Assert.Equal(EvidenceStatus.Gathered, ancestor.Status);
        Assert.Single(path.Items);
        Assert.Single(ancestor.Items);

        await using var db = await fixture.Factory.CreateDbContextAsync(token);
        var audits = await db.AuditLog.Where(row => row.Subject == scope.Subject &&
            (row.Action == "topology.path" || row.Action == "topology.ancestors" ||
             row.Action == "topology.edges.detail" || row.Action == "topology.source-targets"))
            .ToArrayAsync(token);
        Assert.Equal(2, audits.Count(row => row.Action == "topology.path"));
        Assert.Equal(1, audits.Count(row => row.Action == "topology.ancestors"));
        Assert.Equal(4, audits.Count(row => row.Action == "topology.edges.detail"));
        Assert.Equal(2, audits.Count(row => row.Action == "topology.source-targets"));
        Assert.Equal([0L, 2L], audits.Where(row => row.Action == "topology.path")
            .Select(row => row.RowCount).Order().ToArray());
        Assert.All(audits, row =>
        {
            Assert.True(row.Succeeded);
            Assert.Contains("outcome=Success", row.Details, StringComparison.Ordinal);
            Assert.Contains(scope.OwnerGroups.Single(), row.Scope, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("ch")]
    [Trait("Category", "Integration")]
    public async Task Real_provider_fence_rejects_epoch_or_watermark_change_between_directions(string changedStore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(Stack, token);
        var (_, window, scope) = await SeedScenarioAsync(fixture, token);
        var revisions = Revisions(fixture);
        var before = await revisions.ReadAsync(token);
        var audit = new RevisionChangingAuditSink(new ControlPlaneAuditSink(fixture.Factory),
            mutationToken => ChangeRevisionAsync(changedStore, fixture, scope, mutationToken), "topology.path");
        var graph = new TopologyGraphQueryService(RealSnapshotSource(fixture.Factory, fixture.Storage));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, audit, fixture.Reader, graph,
            topologyClock: TopologyClock(window),
            sourceTargetPages: new TopologySourceTargetPageReader(fixture.Factory,
                new TopologyPublicationFence(revisions)));
        using var services = Services(query, new TopologyPublicationFence(revisions)).BuildServiceProvider();
        var result = await services.GetRequiredService<TopologyGraphPathProvider>()
            .GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.True(audit.Mutated);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        var after = await revisions.ReadAsync(token);
        Assert.True(after.PostgresEpoch > before.PostgresEpoch);
        if (changedStore == "pg") Assert.Equal(before.ClickHouseWatermark, after.ClickHouseWatermark);
        else Assert.True(after.ClickHouseWatermark > before.ClickHouseWatermark);

        await using var db = await fixture.Factory.CreateDbContextAsync(token);
        var pathAudits = await db.AuditLog.Where(row => row.Subject == scope.Subject && row.Action == "topology.path")
            .ToArrayAsync(token);
        Assert.Equal(2, pathAudits.Length);
        Assert.All(pathAudits, row => Assert.True(row.Succeeded));
    }

    [Theory]
    [InlineData("pg")]
    [InlineData("ch")]
    [Trait("Category", "Integration")]
    public async Task Real_ancestor_proof_revision_change_is_not_evaluated(string changedStore)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await TelemetryDbFixture.CreateAsync(Stack, token);
        var (_, window, scope) = await SeedScenarioAsync(fixture, token);
        var revisions = Revisions(fixture);
        var before = await revisions.ReadAsync(token);
        var audit = new RevisionChangingAuditSink(new ControlPlaneAuditSink(fixture.Factory),
            mutationToken => ChangeRevisionAsync(changedStore, fixture, scope, mutationToken), "topology.ancestors");
        var graph = new TopologyGraphQueryService(RealSnapshotSource(fixture.Factory, fixture.Storage));
        var query = new ScopedQuery(new(fixture.Storage), new(fixture.Storage), new(fixture.Storage),
            new(fixture.Storage), fixture.Db, audit, fixture.Reader, graph,
            topologyClock: TopologyClock(window),
            sourceTargetPages: new TopologySourceTargetPageReader(fixture.Factory,
                new TopologyPublicationFence(revisions)));
        using var services = Services(query, new TopologyPublicationFence(revisions)).BuildServiceProvider();
        var result = await services.GetRequiredService<TopologyCommonAncestorProvider>()
            .GatherAsync(window, scope, GatherBudget.Default, token);
        Assert.True(audit.Mutated);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        var after = await revisions.ReadAsync(token);
        Assert.True(after.PostgresEpoch > before.PostgresEpoch);
        if (changedStore == "pg") Assert.Equal(before.ClickHouseWatermark, after.ClickHouseWatermark);
        else Assert.True(after.ClickHouseWatermark > before.ClickHouseWatermark);
    }

    private static async Task ChangeRevisionAsync(string changedStore, TelemetryDbFixture fixture,
        AccessScope scope, CancellationToken mutationToken)
    {
        if (changedStore == "pg")
        {
            var written = await new TopologyRegistry(fixture.Factory).CreateAsync(scope, true,
                new(TopologyNodeKind.Service, "revision-bump", scope.OwnerGroups.Single(), true, []), mutationToken);
            Assert.Equal(201, written.Status);
        }
        else
        {
            var watermark = new TopologyPublicationWatermarkReader(fixture.Storage);
            var publisher = new TopologyPublicationCoordinator(fixture.Factory,
                watermark, new TopologyPublicationWatermarkWriter(watermark, fixture.Storage));
            var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await publisher.PublishAsync(key, (_, _) => Task.CompletedTask, mutationToken);
        }
    }

    private static ServiceCollection Services(IScopedQuery query, TopologyPublicationFence fence)
    {
        var services = new ServiceCollection();
        services.AddSingleton(query);
        services.AddSingleton(fence);
        services.AddSingleton(TopologyProviderBudget.Default);
        services.AddTransient<TopologyGraphPathProvider>();
        services.AddTransient<TopologyCommonAncestorProvider>();
        return services;
    }

    private static TopologyPublicationRevisionSource Revisions(TelemetryDbFixture fixture) =>
        new(fixture.Factory, new TopologyPublicationWatermarkReader(fixture.Storage));

    private static TopologyPublicationFence Fence(TelemetryDbFixture fixture) => new(Revisions(fixture));

    private sealed class RevisionChangingAuditSink(IAuditSink inner, Func<CancellationToken, Task> mutate,
        string afterAction) : IAuditSink
    {
        private int changed;
        public bool Mutated => Volatile.Read(ref changed) != 0;

        public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
        {
            await inner.RecordAsync(record, cancellationToken);
            if (record.Action == afterAction && Interlocked.Exchange(ref changed, 1) == 0)
                await mutate(TestContext.Current.CancellationToken);
        }
    }
}
