using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bizigo.UnitTests;

public sealed class TopologyProviderRemovalTests
{
    [Theory]
    [InlineData(typeof(TopologyGraphPathProvider), "topology.graph-path", "topology.common-ancestor")]
    [InlineData(typeof(TopologyCommonAncestorProvider), "topology.common-ancestor", "topology.graph-path")]
    public async Task Production_DI_missing_graph_provider_is_named_not_registered_and_partial(
        Type removedType, string missingId, string remainingId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IScopedQuery, RecordingScopedQuery>();
        services.AddBizigoEvidence();
        var registration = Assert.Single(services, service => service.ServiceType == typeof(IEvidenceProvider)
            && service.ImplementationType == removedType);
        Assert.True(services.Remove(registration));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var topology = scope.ServiceProvider.GetServices<IEvidenceProvider>()
            .Where(provider => provider.Kind == EvidenceKind.Topology).ToArray();
        Assert.DoesNotContain(topology, provider => provider.Id == missingId);
        Assert.Contains(topology, provider => provider.Id == remainingId);

        // Run the real collector/requirements with benign stand-ins for the
        // surviving DI identities, isolating the missing-registration verdict
        // from unrelated telemetry source availability.
        var providers = topology.Select(provider => (IEvidenceProvider)new StubProvider(provider.Id,
            provider.Kind, EvidenceStatus.Empty)).ToArray();
        var collector = new EvidenceCollector(providers, NullLogger<EvidenceCollector>.Instance,
            root.GetRequiredService<EvidenceProviderRequirements>());
        var report = await collector.GatherAsync(new RcaWindow
        {
            BaselineFrom = DateTimeOffset.UnixEpoch,
            BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(1),
            From = DateTimeOffset.UnixEpoch.AddMinutes(1),
            To = DateTimeOffset.UnixEpoch.AddMinutes(2),
        }, AccessScope.ForGroups("topology-removal", ["A"]), GatherBudget.Default,
            TestContext.Current.CancellationToken);
        var absent = Assert.Single(report.Slices, slice => slice.ProviderId == missingId);
        Assert.Equal(EvidenceKind.Topology, absent.Kind);
        Assert.Equal(EvidenceStatus.NotRegistered, absent.Status);
        var coverage = Assert.Single(report.Coverage, item => item.Kind == EvidenceKind.Topology);
        Assert.True(coverage.Partial);
        Assert.Equal(EvidenceStatus.NotRegistered, coverage.Status);
        Assert.Contains(missingId, coverage.NotConsulted);
        Assert.True(report.IsPartial);
    }
}
