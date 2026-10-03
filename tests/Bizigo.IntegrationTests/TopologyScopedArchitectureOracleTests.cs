using System.Text.RegularExpressions;
using Bizigo.Evidence.Providers;
using Bizigo.Query;

namespace Bizigo.IntegrationTests;

/// <summary>Q01: keep public read consumers on the audited, scope-required query boundary.</summary>
public sealed class TopologyScopedArchitectureOracleTests
{
    [Fact]
    public void Q01_All_seven_REST_reads_and_both_RCA_providers_use_scoped_query()
    {
        var endpoints = File.ReadAllText(DevStackSetup.RepoPath("src/Bizigo.Api/TopologyReadEndpoints.cs"));
        var providers = File.ReadAllText(DevStackSetup.RepoPath("src/Bizigo.Evidence/Providers/TopologyGraphProviders.cs"));
        var registrations = Regex.Matches(endpoints,
            @"group\.MapGet\(\s*""[^""]+""\s*,\s*\([^)]*\bIScopedQuery\s+query\b",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.Equal(7, registrations.Count);
        Assert.Equal(7, Regex.Matches(endpoints, @"\bgroup\.MapGet\(", RegexOptions.CultureInvariant).Count);
        Assert.Contains("RequireAuthorization(BizigoAuthPolicies.Read)", endpoints, StringComparison.Ordinal);
        foreach (var method in new[] { "SearchTopologyNodesAsync", "GetTopologyNodeAsync", "SearchTopologyEdgesAsync",
                     "GetTopologyEdgeAsync", "GetTopologyNeighborhoodAsync", "GetTopologyPathAsync",
                     "GetTopologyCommonAncestorAsync" })
            Assert.Contains("query." + method, endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("Bizigo.Storage.ClickHouse", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlPlaneDbContext", endpoints, StringComparison.Ordinal);
        Assert.Contains("private readonly IScopedQuery _query", providers, StringComparison.Ordinal);
        Assert.Contains("Task<EvidenceSlice> GatherAsync(IScopedQuery query", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("Bizigo.Storage.ClickHouse", providers, StringComparison.Ordinal);
        Assert.DoesNotContain("ControlPlaneDbContext", providers, StringComparison.Ordinal);
        Assert.Contains(typeof(TopologyGraphPathProvider).GetConstructors(), ctor =>
            ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IScopedQuery)));
        Assert.Contains(typeof(TopologyCommonAncestorProvider).GetConstructors(), ctor =>
            ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IScopedQuery)));
    }
}
