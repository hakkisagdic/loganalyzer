using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

public sealed class TopologyEvidenceTests
{
    private static readonly AccessScope Scope = AccessScope.ForGroups("evidence", ["A"]);
    private static readonly RcaWindow Window = new()
    {
        BaselineFrom = DateTimeOffset.UnixEpoch, BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(1),
        From = DateTimeOffset.UnixEpoch.AddMinutes(1), To = DateTimeOffset.UnixEpoch.AddMinutes(2),
    };

    [Fact]
    public void Production_registration_and_ranking()
    {
        var services = new ServiceCollection(); services.AddBizigoEvidence();
        var implementations = services.Where(service => service.ServiceType == typeof(IEvidenceProvider))
            .Select(service => service.ImplementationType).ToArray();
        Assert.Contains(typeof(TopologyProvider), implementations);
        Assert.Contains(typeof(TopologyGraphPathProvider), implementations);
        Assert.Contains(typeof(TopologyCommonAncestorProvider), implementations);
        Assert.Equal(4, EvidenceRanking.ClassRank("topology.graph-path"));
        Assert.Equal(4, EvidenceRanking.ClassRank("topology.common-ancestor"));
        Assert.Contains("topology.graph-path", EvidenceRanking.RankedProviders);
        Assert.Contains("topology.common-ancestor", EvidenceRanking.RankedProviders);
    }

    [Theory]
    [InlineData(TelemetryResultStatus.NeverFed, EvidenceStatus.NeverFed)]
    [InlineData(TelemetryResultStatus.Failed, EvidenceStatus.Failed)]
    [InlineData(TelemetryResultStatus.Empty, EvidenceStatus.Gathered)]
    [InlineData(TelemetryResultStatus.Data, EvidenceStatus.Gathered)]
    public async Task Feed_and_evaluation_states(TelemetryResultStatus feedStatus, EvidenceStatus expected)
    {
        var query = ReadyQuery(feedStatus);
        var result = await new TopologyGraphPathProvider(query).GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == EvidenceStatus.Gathered ? "Evaluated" : expected == EvidenceStatus.NeverFed ? "NotRun" : "Failed",
            result.Telemetry!.Evaluation);
    }

    [Fact]
    public async Task Provider_unavailable_or_failure()
    {
        var query = ReadyQuery(TelemetryResultStatus.Data);
        query.TopologySourceNodes.RemoveAt(1);
        var result = await new TopologyCommonAncestorProvider(query).GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status); Assert.True(result.IsEvidence is false);
        Assert.Contains("not guessed", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_provider_reports_budget_exhaustion_as_partial_not_success()
    {
        var query = ReadyQuery(TelemetryResultStatus.Data);
        var provider = new TopologyGraphPathProvider(query, new(2, 0 + 1, 1, 1));
        var result = await provider.GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Contains("BudgetExceeded", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Previous_evidence_semantics_and_legacy_bundle_hash_remain_stable()
    {
        var slice = new EvidenceSlice { ProviderId = "topology.graph-path", Kind = EvidenceKind.Topology,
            Status = EvidenceStatus.Empty, Detail = "none" };
        var bundle = new EvidenceBundle { Id = Guid.NewGuid(), GatheredAt = DateTimeOffset.UtcNow,
            Window = Window, Scope = new(["A"], false), Slices = [slice], Trust = new(0, 0) };
        var reopened = BundleSerializer.Deserialize(BundleSerializer.Serialize(bundle));
        Assert.Equal(bundle.ContentHash, reopened.ContentHash);
        var legacy = reopened with { SchemaVersion = 1 };
        var legacyReopened = BundleSerializer.Deserialize(BundleSerializer.Serialize(legacy));
        Assert.Equal(1, legacyReopened.SchemaVersion); Assert.Equal("LegacySemantics", legacyReopened.ExcludedInputRecords.Reason);
        Assert.Equal(EvidenceStatus.Empty, Assert.Single(legacyReopened.Slices).Status);
    }

    private static RecordingScopedQuery ReadyQuery(TelemetryResultStatus feedStatus)
    {
        var one = TopologyIdentity.Node(TopologyNodeKind.Source, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var two = TopologyIdentity.Node(TopologyNodeKind.Source, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var root = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.Parse("00000000-0000-0000-0000-000000000003"));
        var query = new RecordingScopedQuery
        {
            TelemetryFeed = (_, _, _, _) => Task.FromResult(new TelemetryCount(feedStatus,
                feedStatus == TelemetryResultStatus.Failed ? null : 1, feedStatus == TelemetryResultStatus.Failed ? "QueryUnavailable" : null)),
            TopologyPath = new(TopologyGraphResultStatus.Found, [one, two], ["edge-1"], null, 1),
            TopologyAncestor = new(TopologyGraphResultStatus.Found, root,
                [new(one, [root, one], ["ancestor-1"]), new(two, [root, two], ["ancestor-2"])], 1),
        };
        query.Onsets.Add(new("A", "source-1", Window.From, 1, 0));
        query.Onsets.Add(new("A", "source-2", Window.From.AddSeconds(1), 1, 0));
        query.TopologySourceNodes.Add(new("source-1", one, "A"));
        query.TopologySourceNodes.Add(new("source-2", two, "A"));
        query.TopologyEdges["edge-1"] = Detail("edge-1", one, two);
        query.TopologyEdges["ancestor-1"] = Detail("ancestor-1", root, one);
        query.TopologyEdges["ancestor-2"] = Detail("ancestor-2", root, two);
        return query;
    }

    private static TopologyEdgeDetail Detail(string id, string from, string to) => new(
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.75m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 0, 1, null, 1, 1, false), [], null);
}
