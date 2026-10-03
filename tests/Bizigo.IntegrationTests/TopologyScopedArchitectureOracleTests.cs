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
        var registrations = MapGetParameters(endpoints);
        Assert.Equal(7, registrations.Count);
        Assert.Equal(new[] { "/ancestors", "/edges", "/edges/{edgeId}", "/nodes",
                "/nodes/{nodeId}", "/nodes/{nodeId}/neighbors", "/path" },
            registrations.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(registrations.Count,
            Regex.Matches(endpoints, @"\bgroup\.MapGet\(", RegexOptions.CultureInvariant).Count);
        Assert.All(registrations.Values, parameters => Assert.Matches(
            @"\bIScopedQuery\s+query\b", parameters));
        Assert.All(registrations.Values, parameters => Assert.Matches(
            @"\bICurrentUser\s+user\b", parameters));
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

    private static IReadOnlyDictionary<string, string> MapGetParameters(string source)
    {
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        var registrations = Regex.Matches(source,
            @"group\.MapGet\(\s*""([^""]+)""\s*,\s*\(", RegexOptions.CultureInvariant);
        foreach (Match registration in registrations)
        {
            // The last char of the match opens the lambda parameter list.
            // Match its closing parenthesis structurally: [FromQuery(Name=...)]
            // may contain its own ')' and must not terminate the scan.
            var open = registration.Index + registration.Length - 1;
            var depth = 1;
            var brackets = 0;
            var inString = false;
            var escaped = false;
            var end = -1;
            for (var index = open + 1; index < source.Length; index++)
            {
                var value = source[index];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (value == '\\') escaped = true;
                    else if (value == '"') inString = false;
                    continue;
                }
                if (value == '"') { inString = true; continue; }
                if (value == '[') { brackets++; continue; }
                if (value == ']') { brackets--; continue; }
                if (brackets > 0) continue;
                if (value == '(') depth++;
                else if (value == ')' && --depth == 0) { end = index; break; }
            }
            Assert.True(end > open, "Topology MapGet lambda parameters are unbalanced.");
            Assert.True(routes.TryAdd(registration.Groups[1].Value,
                source[(open + 1)..end]), "Duplicate topology read route registration.");
        }
        return routes;
    }
}
