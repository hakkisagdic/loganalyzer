using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyProductionProviderTests
{
    private static readonly AccessScope Scope = AccessScope.ForGroups("topology-provider", ["A"]);
    private static readonly RcaWindow Window = new()
    {
        BaselineFrom = DateTimeOffset.UnixEpoch, BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(1),
        From = DateTimeOffset.UnixEpoch.AddMinutes(1), To = DateTimeOffset.UnixEpoch.AddMinutes(2),
    };
    private static readonly string SourceA = Node(1), SourceB = Node(2), Root = Node(3);

    public static IEnumerable<object[]> NumericCases()
    {
        foreach (var provider in new[] { "topology.graph-path", "topology.common-ancestor" })
        foreach (var dimension in new[] { "node", "edge", "page", "byte" })
        foreach (var delta in new[] { -1, 0, 1 }) yield return [provider, dimension, delta];
    }

    [Theory, MemberData(nameof(NumericCases))]
    public async Task Production_all_numeric_boundaries(string provider, string dimension, int delta)
    {
        var baseline = await Provider(provider, Ready(), TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, baseline.Status);
        var itemBytes = JsonSerializer.SerializeToUtf8Bytes(Assert.Single(baseline.Items), BundleSerializer.Options).Length;
        var limits = new Dictionary<string, int>
        {
            ["node"] = 3 + 100,
            ["edge"] = 2 + 100,
            ["page"] = 3 + 100,
            ["byte"] = itemBytes + 100,
        };
        limits[dimension] = dimension switch
        {
            "node" => 3 + delta,
            "edge" => 2 + delta,
            "page" => 3 + delta,
            _ => itemBytes + delta,
        };
        var budget = new TopologyProviderBudget(limits["node"], limits["edge"], limits["page"], limits["byte"]);
        var result = await Provider(provider, Ready(), budget).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        if (delta < 0)
        {
            Assert.Equal(EvidenceStatus.Unavailable, result.Status);
            Assert.True(result.Truncated);
            Assert.Empty(result.Items);
            Assert.Contains("BudgetExceeded", result.Detail, StringComparison.Ordinal);
            Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        }
        else
        {
            Assert.Equal(EvidenceStatus.Gathered, result.Status);
            Assert.False(result.Truncated);
            Assert.Single(result.Items);
        }
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var provider in new[] { "topology.graph-path", "topology.common-ancestor" })
        foreach (var outcome in new[] { "partial", "timeout", "exception", "caller-cancel" })
            yield return [provider, outcome];
    }

    [Theory, MemberData(nameof(FailureCases))]
    public async Task Production_failure_and_cancel_truth_table(string provider, string outcome)
    {
        var query = Ready();
        var gatherBudget = GatherBudget.Default;
        using var caller = new CancellationTokenSource();
        if (outcome == "partial")
        {
            query.TopologyPath = new(TopologyGraphResultStatus.NotVerified, [], [], null, 1, "HiddenBoundary");
            query.TopologyAncestor = new(TopologyGraphResultStatus.NotVerified, null, [], 1, "HiddenBoundary");
        }
        else if (outcome == "timeout")
        {
            query.TopologyPathResponse = async (_, _, token) =>
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
            query.TopologyAncestorResponse = async (_, _, token) =>
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); };
            gatherBudget = new(10, TimeSpan.FromMilliseconds(20));
        }
        else if (outcome == "exception")
        {
            query.TopologyPathResponse = (_, _, _) => throw new IOException("provider source failed");
            query.TopologyAncestorResponse = (_, _, _) => throw new IOException("provider source failed");
        }
        else caller.Cancel();

        var actual = Provider(provider, query, TopologyProviderBudget.Default);
        if (outcome == "caller-cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                actual.GatherAsync(Window, Scope, gatherBudget, caller.Token));
            return;
        }
        var result = await actual.GatherAsync(Window, Scope, gatherBudget, caller.Token);
        Assert.Equal(outcome == "partial" ? EvidenceStatus.Unavailable : EvidenceStatus.Failed, result.Status);
        Assert.Empty(result.Items);
        Assert.NotEqual(EvidenceStatus.Empty, result.Status);
        Assert.Equal(outcome == "partial" ? "NotComparable" : "Failed", result.Telemetry!.Evaluation);
    }

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Proof_references_preserve_edge_fields_and_bounded_drilldown(string provider)
    {
        var result = await Provider(provider, Ready(), TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var payload = Assert.Single(result.Items).Payload;
        using var document = JsonDocument.Parse(payload["proof_edges"]);
        var proofs = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, proofs.Length);
        Assert.All(proofs, proof =>
        {
            Assert.Equal("observed", proof.GetProperty("provenance").GetString());
            Assert.Equal("depends_on", proof.GetProperty("relation").GetString());
            Assert.Equal(0.75m, proof.GetProperty("confidence").GetDecimal());
            Assert.Equal(1000, proof.GetProperty("last_seen_unix_nano").GetDecimal());
            Assert.Equal(1, proof.GetProperty("evidence").GetArrayLength());
            Assert.Equal("more-proof", proof.GetProperty("evidence_cursor").GetString());
        });
        Assert.Contains(proofs, proof => proof.GetProperty("id").GetString() == "edge-2");
    }

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Mismatched_or_reverse_proof_never_becomes_a_finding(string provider)
    {
        var query = Ready();
        query.TopologyEdges["edge-2"] = Detail("edge-2", SourceB, Root);
        var result = await Provider(provider, query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Contains("proof", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_path_provider_joins_paged_hops_once()
    {
        var query = Ready();
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(request.Cursor is null
            ? new TopologyPathResult(TopologyGraphResultStatus.Found, [SourceA, Root], ["edge-1"], "next", 1)
            : new TopologyPathResult(TopologyGraphResultStatus.Found, [Root, SourceB], ["edge-2"], null, 1));
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var payload = Assert.Single(result.Items).Payload;
        Assert.Equal(JsonSerializer.Serialize(new[] { SourceA, Root, SourceB }, BundleSerializer.Options), payload["node_ids"]);
        Assert.Equal("[\"edge-1\",\"edge-2\"]", payload["edge_ids"]);
    }

    [Fact]
    public async Task Previous_evidence_semantics_cross_provider_golden()
    {
        var query = Ready();
        var path = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        var ancestor = await Provider("topology.common-ancestor", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        var prior = new[]
        {
            Prior("change.feed", EvidenceKind.Change, "change"),
            Prior("logs.first-seen", EvidenceKind.Log, "first-seen"),
            Prior("topology.shared-attribute", EvidenceKind.Topology, "shared"),
        };
        EvidenceSlice[] slices = [path, ancestor, .. prior];
        var ranked = EvidenceRanking.RankAll(slices).Select(static value => value.Item.Id).ToArray();
        Assert.Equal(["change", "first-seen", .. new[] { path.Items[0].Id, ancestor.Items[0].Id }
            .Order(StringComparer.Ordinal), "shared"], ranked);
        var bundle = new EvidenceBundle
        {
            Id = Guid.NewGuid(), GatheredAt = Window.To, Window = Window,
            Scope = new(["A"], false), Slices = slices, Trust = new(10, 0),
        };
        Assert.Null(bundle.OutOfScopeCount);
        Assert.Equal(bundle.ContentHash, BundleSerializer.Deserialize(BundleSerializer.Serialize(bundle)).ContentHash);
    }

    [Fact]
    public async Task Determinism_and_scope_do_not_reuse_topology_proof()
    {
        var query = Ready();
        query.TopologySourceNodesResponse = (sourceIds, scope, _) =>
            Task.FromResult<IReadOnlyList<TopologySourceNode>>(scope.Allows("A")
                ? [.. query.TopologySourceNodes.Where(node => sourceIds.Contains(node.SourceId, StringComparer.Ordinal))]
                : []);
        var provider = Provider("topology.graph-path", query, TopologyProviderBudget.Default);
        var first = await provider.GatherAsync(Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        var second = await provider.GatherAsync(Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        var scopeB = AccessScope.ForGroups("topology-provider-b", ["B"]);
        var hidden = await provider.GatherAsync(Window, scopeB, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, first.Status);
        Assert.Equal(JsonSerializer.Serialize(first.Items, BundleSerializer.Options),
            JsonSerializer.Serialize(second.Items, BundleSerializer.Options));
        Assert.Equal(EvidenceStatus.Unavailable, hidden.Status);
        Assert.Empty(hidden.Items);

        static EvidenceBundle Bundle(EvidenceSlice slice, AccessScope scope, Guid id, DateTimeOffset gatheredAt) => new()
        {
            Id = id, GatheredAt = gatheredAt, Window = Window,
            Scope = new(scope.IsUnrestricted ? [] : scope.OwnerGroups.Order(StringComparer.Ordinal).ToArray(), scope.IsUnrestricted),
            Slices = [slice], Trust = new(10, 0),
        };
        var bundleA1 = Bundle(first, Scope, Guid.NewGuid(), Window.To);
        var bundleA2 = Bundle(second, Scope, Guid.NewGuid(), Window.To.AddHours(1));
        var bundleB = Bundle(hidden, scopeB, Guid.NewGuid(), Window.To);
        Assert.Equal(bundleA1.ContentHash, bundleA2.ContentHash);
        Assert.NotEqual(bundleA1.ContentHash, bundleB.ContentHash);
        Assert.False(bundleA1.Scope.IsReadableBy(scopeB));
    }

    private static EvidenceSlice Prior(string provider, EvidenceKind kind, string id) => new()
    {
        ProviderId = provider, Kind = kind, Status = EvidenceStatus.Gathered,
        Items = [new(id, provider, kind, Window.To, 1, id, new Dictionary<string, string>())],
    };

    private static IEvidenceProvider Provider(string id, RecordingScopedQuery query, TopologyProviderBudget budget) =>
        id == "topology.graph-path" ? new TopologyGraphPathProvider(query, budget) :
        new TopologyCommonAncestorProvider(query, budget);

    private static RecordingScopedQuery Ready()
    {
        var query = new RecordingScopedQuery
        {
            TelemetryFeed = (_, _, _, _) => Task.FromResult(new TelemetryCount(TelemetryResultStatus.Data, 1, null)),
            TopologyPath = new(TopologyGraphResultStatus.Found, [SourceA, Root, SourceB], ["edge-1", "edge-2"], null, 1),
            TopologyAncestor = new(TopologyGraphResultStatus.Found, Root,
                [new(SourceA, [Root, SourceA], ["edge-3"]), new(SourceB, [Root, SourceB], ["edge-2"])], 1),
        };
        query.Onsets.Add(new("A", "source-1", Window.From, 1, 0));
        query.Onsets.Add(new("A", "source-2", Window.From.AddSeconds(1), 1, 0));
        query.TopologySourceNodes.Add(new("source-1", SourceA, "A"));
        query.TopologySourceNodes.Add(new("source-2", SourceB, "A"));
        query.TopologyEdges.Add("edge-1", Detail("edge-1", SourceA, Root));
        query.TopologyEdges.Add("edge-2", Detail("edge-2", Root, SourceB));
        query.TopologyEdges.Add("edge-3", Detail("edge-3", Root, SourceA));
        return query;
    }

    private static TopologyEdgeDetail Detail(string id, string from, string to) => new(
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.75m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 10, 1000, 2000, 1, 1, false),
        [new(id, "proof-1", "trace-logical-1", "span-logical-1", 1000)], "more-proof");

    private static string Node(int value) => TopologyIdentity.Node(TopologyNodeKind.Source,
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));
}
