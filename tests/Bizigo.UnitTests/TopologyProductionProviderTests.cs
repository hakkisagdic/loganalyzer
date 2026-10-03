using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.UnitTests;

public sealed class TopologyProductionProviderTests
{
    private static readonly AccessScope Scope = AccessScope.ForGroups("topology-provider", ["A"]);
    private static readonly RcaWindow Window = new()
    {
        BaselineFrom = DateTimeOffset.UnixEpoch, BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(1),
        From = DateTimeOffset.UnixEpoch.AddMinutes(1), To = DateTimeOffset.UnixEpoch.AddMinutes(2),
    };
    private static readonly string SourceA = Node(1), SourceB = Node(2), Root = Node(3), Other = Node(4);

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
            "page" => (provider == "topology.graph-path" ? 4 : 3) + delta,
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
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(request.FromNodeId != SourceA
            ? new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1)
            : request.Cursor is null
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
    public async Task Production_reverse_only_path_is_found_for_unordered_affected_pair()
    {
        var query = BidirectionalReady(forwardEnabled: false);
        var calls = new List<(string From, string To)>();
        var response = query.TopologyPathResponse!;
        query.TopologyPathResponse = async (request, scope, token) =>
        {
            calls.Add((request.FromNodeId, request.ToNodeId));
            return await response(request, scope, token);
        };
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        Assert.Equal(new[] { (SourceA, SourceB), (SourceB, SourceA) }, calls);
        var payload = Assert.Single(result.Items).Payload;
        Assert.Equal(JsonSerializer.Serialize(new[] { SourceB, Other, SourceA }, BundleSerializer.Options), payload["node_ids"]);
        Assert.Equal("[\"edge-4\",\"edge-5\"]", payload["edge_ids"]);
    }

    [Fact]
    public async Task Production_two_complete_unreachable_directions_are_empty()
    {
        var query = Ready();
        query.TopologyPathResponse = (_, _, _) => Task.FromResult(
            new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Empty, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("Evaluated", result.Telemetry!.Evaluation);
    }

    [Theory]
    [InlineData(true, "edge-4")]
    [InlineData(false, "edge-1")]
    public async Task Production_bidirectional_paths_choose_hop_then_ordinal_nodes(bool reverseShort, string expectedFirstEdge)
    {
        var result = await Provider("topology.graph-path", BidirectionalReady(reverseShort: reverseShort),
            TopologyProviderBudget.Default).GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var payload = Assert.Single(result.Items).Payload;
        using var edgeIds = JsonDocument.Parse(payload["edge_ids"]);
        Assert.Equal(expectedFirstEdge, edgeIds.RootElement[0].GetString());
        using var nodeIds = JsonDocument.Parse(payload["node_ids"]);
        Assert.Equal(reverseShort ? SourceB : SourceA, nodeIds.RootElement[0].GetString());
    }

    [Theory]
    [InlineData(false, "HiddenBoundary")]
    [InlineData(true, "QueryPartial")]
    public async Task Production_partial_opposite_direction_never_publishes_a_complete_finding(
        bool partialForward, string reason)
    {
        var query = BidirectionalReady();
        var response = query.TopologyPathResponse!;
        query.TopologyPathResponse = (request, scope, token) =>
            request.FromNodeId == (partialForward ? SourceA : SourceB)
                ? Task.FromResult(new TopologyPathResult(TopologyGraphResultStatus.NotVerified, [], [], null, 1, reason))
                : response(request, scope, token);
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Contains(reason, result.Detail, StringComparison.Ordinal);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
    }

    [Fact]
    public async Task Production_failed_opposite_direction_never_publishes_a_complete_finding()
    {
        var query = Ready();
        var response = query.TopologyPathResponse!;
        query.TopologyPathResponse = (request, scope, token) => request.FromNodeId == SourceB
            ? throw new IOException("reverse graph unavailable") : response(request, scope, token);
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Failed, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("Failed", result.Telemetry!.Evaluation);
    }

    [Fact]
    public async Task Production_different_direction_publications_are_not_mixed()
    {
        var query = Ready();
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(request.FromNodeId == SourceA
            ? query.TopologyPath!
            : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 2));
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_pg_epoch_change_during_reverse_or_proof_detail_restarts_pair(bool duringProof)
    {
        var query = Ready();
        var revisions = new MutableRevisionSource(new(7, 1));
        if (duringProof)
        {
            query.TopologyEdgeResponse = (edgeId, _, _, _) =>
            {
                revisions.Current = new(8, 1);
                return Task.FromResult<TopologyEdgeDetail?>(query.TopologyEdges[edgeId]);
            };
        }
        else
        {
            var response = query.TopologyPathResponse!;
            query.TopologyPathResponse = (request, scope, token) =>
            {
                if (request.FromNodeId == SourceB) revisions.Current = new(8, 1);
                return response(request, scope, token);
            };
        }
        var services = new ServiceCollection();
        services.AddSingleton<IScopedQuery>(query);
        services.AddSingleton(new TopologyPublicationFence(revisions));
        services.AddTransient<TopologyGraphPathProvider>();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<TopologyGraphPathProvider>().GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Equal(2, revisions.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_ancestor_proof_restarts_on_pg_epoch_or_ch_watermark_change(bool changeWatermark)
    {
        var query = Ready();
        var revisions = new MutableRevisionSource(new(7, 1));
        query.TopologyEdgeResponse = (edgeId, _, _, _) =>
        {
            revisions.Current = changeWatermark ? new(7, 2) : new(8, 1);
            return Task.FromResult<TopologyEdgeDetail?>(query.TopologyEdges[edgeId]);
        };
        var services = new ServiceCollection();
        services.AddSingleton<IScopedQuery>(query);
        services.AddSingleton(new TopologyPublicationFence(revisions));
        services.AddTransient<TopologyCommonAncestorProvider>();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<TopologyCommonAncestorProvider>().GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Equal(2, revisions.Reads);
    }

    [Fact]
    public async Task Production_ancestor_inflight_cancel_reaches_query_within_one_second_and_result_within_two()
    {
        var query = Ready();
        using var caller = new CancellationTokenSource();
        var inFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        query.TopologyEdgeResponse = async (_, _, _, token) =>
        {
            using var registration = token.Register(() => tokenObserved.TrySetResult(true));
            inFlight.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancellation must leave the proof read.");
        };
        var services = new ServiceCollection();
        services.AddSingleton<IScopedQuery>(query);
        services.AddSingleton(new TopologyPublicationFence(new MutableRevisionSource(new(7, 1))));
        services.AddTransient<TopologyCommonAncestorProvider>();
        using var provider = services.BuildServiceProvider();
        var gathering = provider.GetRequiredService<TopologyCommonAncestorProvider>().GatherAsync(
            Window, Scope, GatherBudget.Default, caller.Token);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gathering.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_exhausted_page_budget_prevents_reverse_or_continuation_io(bool continuation)
    {
        var query = Ready();
        var calls = 0;
        query.TopologyPathResponse = (request, _, _) =>
        {
            calls++;
            if (calls != 1) throw new IOException("Query must not run after page budget is exhausted.");
            return Task.FromResult(continuation
                ? new TopologyPathResult(TopologyGraphResultStatus.Found, [SourceA, Root], ["edge-1"], "next", 1)
                : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        var budget = new TopologyProviderBudget(100, 100, 1, 1024 * 1024);
        var result = await Provider("topology.graph-path", query, budget).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Contains("BudgetExceeded", result.Detail, StringComparison.Ordinal);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Production_caller_cancel_during_reverse_is_forwarded()
    {
        var query = Ready();
        using var caller = new CancellationTokenSource();
        var calls = 0;
        query.TopologyPathResponse = (request, _, token) =>
        {
            calls++;
            if (request.FromNodeId == SourceB)
            {
                caller.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
                Window, Scope, GatherBudget.Default, caller.Token));
        Assert.Equal(2, calls);
    }

    public static IEnumerable<object[]> BidirectionalBudgetCases()
    {
        foreach (var dimension in new[] { "node", "edge", "page", "byte" })
        foreach (var delta in new[] { -1, 0, 1 }) yield return [dimension, delta];
    }

    [Theory, MemberData(nameof(BidirectionalBudgetCases))]
    public async Task Production_bidirectional_candidates_share_each_budget(string dimension, int delta)
    {
        var selected = await Provider("topology.graph-path", BidirectionalReady(), TopologyProviderBudget.Default)
            .GatherAsync(Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        var reverseOnly = await Provider("topology.graph-path", BidirectionalReady(forwardEnabled: false),
            TopologyProviderBudget.Default).GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, selected.Status);
        Assert.Equal(EvidenceStatus.Gathered, reverseOnly.Status);
        var serializedBytes = JsonSerializer.SerializeToUtf8Bytes(Assert.Single(selected.Items), BundleSerializer.Options).Length
            + JsonSerializer.SerializeToUtf8Bytes(Assert.Single(reverseOnly.Items), BundleSerializer.Options).Length;
        var limits = new Dictionary<string, int> { ["node"] = 1000, ["edge"] = 4000, ["page"] = 20, ["byte"] = 1024 * 1024 };
        limits[dimension] = (dimension switch
        {
            "node" => 4, "edge" => 4, "page" => 6, _ => serializedBytes,
        }) + delta;
        var budget = new TopologyProviderBudget(limits["node"], limits["edge"], limits["page"], limits["byte"]);
        var actual = await Provider("topology.graph-path", BidirectionalReady(), budget).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(delta < 0 ? EvidenceStatus.Unavailable : EvidenceStatus.Gathered, actual.Status);
        Assert.Equal(delta < 0, actual.Truncated);
        Assert.Equal(delta < 0 ? "NotComparable" : "Evaluated", actual.Telemetry!.Evaluation);
        Assert.Equal(delta < 0 ? 0 : 1, actual.Items.Count);
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
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(request.FromNodeId == SourceA
            && request.ToNodeId == SourceB
                ? query.TopologyPath!
                : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        query.TopologyEdges.Add("edge-1", Detail("edge-1", SourceA, Root));
        query.TopologyEdges.Add("edge-2", Detail("edge-2", Root, SourceB));
        query.TopologyEdges.Add("edge-3", Detail("edge-3", Root, SourceA));
        return query;
    }

    private static RecordingScopedQuery BidirectionalReady(bool forwardEnabled = true, bool reverseShort = false)
    {
        var query = Ready();
        var reverse = reverseShort
            ? new TopologyPathResult(TopologyGraphResultStatus.Found, [SourceB, SourceA], ["edge-4"], null, 1)
            : new TopologyPathResult(TopologyGraphResultStatus.Found, [SourceB, Other, SourceA], ["edge-4", "edge-5"], null, 1);
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(request.FromNodeId == SourceA
            ? (forwardEnabled ? query.TopologyPath! : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1))
            : reverse);
        query.TopologyEdges.Add("edge-4", Detail("edge-4", SourceB, reverseShort ? SourceA : Other));
        if (!reverseShort) query.TopologyEdges.Add("edge-5", Detail("edge-5", Other, SourceA));
        return query;
    }

    private static TopologyEdgeDetail Detail(string id, string from, string to) => new(
        new(id, from, to, TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.75m,
            "A", "A", TopologyEdgeVisibility.SameOwner, 10, 1000, 2000, 1, 1, false),
        [new(id, "proof-1", "trace-logical-1", "span-logical-1", 1000)], "more-proof");

    private static string Node(int value) => TopologyIdentity.Node(TopologyNodeKind.Source,
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private sealed class MutableRevisionSource(TopologyPublicationRevision initial)
        : ITopologyPublicationRevisionSource
    {
        public TopologyPublicationRevision Current { get; set; } = initial;
        public int Reads { get; private set; }

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Current);
        }
    }
}
