using System.Diagnostics;
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
            "page" => (provider == "topology.graph-path" ? 5 : 4) + delta,
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

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Production_graph_queries_use_exact_rca_observed_window(string providerId)
    {
        var query = Ready();
        var pathRequests = new List<TopologyPathQuery>();
        var detailWindows = new List<(decimal ReadClock, decimal From, decimal To, decimal DeclaredStateClock)>();
        TopologyGroupedAncestorQuery? ancestorRequest = null;
        var pathResponse = query.TopologyPathResponse!;
        query.TopologyPathResponse = async (request, scope, token) =>
        {
            pathRequests.Add(request);
            return await pathResponse(request, scope, token);
        };
        query.TopologyGroupedAncestorResponse = (request, _, _) =>
        {
            ancestorRequest = request;
            return Task.FromResult(query.TopologyAncestor!);
        };
        query.TopologyEdgeRcaWindowResponse = (edgeId, readClock, from, to, declaredStateClock, scope, token) =>
        {
            detailWindows.Add((readClock, from, to, declaredStateClock));
            return Task.FromResult<TopologyEdgeDetail?>(query.TopologyEdges[edgeId]);
        };
        var result = await Provider(providerId, query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var from = TopologyIdentity.Nano(Window.From);
        var to = TopologyIdentity.Nano(Window.To);
        Assert.Equal(2, detailWindows.Count);
        Assert.All(detailWindows, actual =>
        {
            Assert.Equal(to, actual.ReadClock);
            Assert.Equal(from, actual.From);
            Assert.Equal(to, actual.To);
            Assert.Equal(to - 1m, actual.DeclaredStateClock);
        });
        if (providerId == "topology.graph-path")
        {
            Assert.Equal(2, pathRequests.Count);
            Assert.All(pathRequests, request =>
            {
                Assert.Equal(from, request.FromUnixNano);
                Assert.Equal(to, request.ToUnixNano);
                Assert.Equal(to, request.ReadClockUnixNano);
                Assert.Equal(to - 1m, request.DeclaredStateClockUnixNano);
            });
        }
        else
        {
            Assert.NotNull(ancestorRequest);
            Assert.Equal(from, ancestorRequest.FromUnixNano);
            Assert.Equal(to, ancestorRequest.ToUnixNano);
            Assert.Equal(to, ancestorRequest.ReadClockUnixNano);
            Assert.Equal(to - 1m, ancestorRequest.DeclaredStateClockUnixNano);
        }
    }

    [Fact]
    public async Task Declared_contains_at_exact_window_end_is_excluded_but_pre_end_proof_remains()
    {
        var startingAtEnd = GraphNode(TopologyNodeKind.Service, 21);
        var endingAtEnd = GraphNode(TopologyNodeKind.Service, 22);
        var to = TopologyIdentity.Nano(Window.To);
        var from = TopologyIdentity.Nano(Window.From);
        var query = Ready();
        var mappingClocks = new List<decimal>();
        var pathRequests = new List<TopologyPathQuery>();
        query.TopologySourceTargetPageResponse = (_, asOf, _, _, _, _) =>
        {
            mappingClocks.Add(asOf);
            // Simulate half-open declared validity at the exact RCA endpoint.
            var chunks = new List<TopologySourceTargetChunk>();
            if (asOf >= to)
                chunks.Add(new("source-1", SourceA,
                    new TopologySourceTarget(startingAtEnd, ["starts-at-to"], [SourceA, startingAtEnd]), null, null));
            chunks.Add(new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null));
            if (asOf < to)
                chunks.Add(new("source-2", SourceB,
                    new TopologySourceTarget(endingAtEnd, ["ends-at-to"], [SourceB, endingAtEnd]), null, null));
            chunks.Add(new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null));
            return Task.FromResult(new TopologySourceTargetsPage(chunks, null, 7, 1));
        };
        query.TopologyPathResponse = (request, _, _) =>
        {
            pathRequests.Add(request);
            return Task.FromResult(request.FromNodeId == endingAtEnd && request.ToNodeId == SourceA
                ? new TopologyPathResult(TopologyGraphResultStatus.Found,
                    [endingAtEnd, SourceA], ["observed-at-to-minus-one"], null, 1)
                : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        query.TopologyEdges["observed-at-to-minus-one"] = Detail("observed-at-to-minus-one", endingAtEnd, SourceA);

        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        Assert.Equal(new[] { to - 1m }, mappingClocks);
        Assert.All(pathRequests, request =>
        {
            Assert.Equal(to, request.ReadClockUnixNano);
            Assert.Equal(from, request.FromUnixNano);
            Assert.Equal(to, request.ToUnixNano);
        });
        var payload = Assert.Single(result.Items).Payload;
        Assert.Equal("[\"observed-at-to-minus-one\"]", payload["edge_ids"]);
        using var witnesses = JsonDocument.Parse(payload["source_witnesses"]);
        var rows = witnesses.RootElement.EnumerateArray().ToArray();
        Assert.Equal(SourceA, rows[0].GetProperty("target_node_id").GetString());
        Assert.Equal(endingAtEnd, rows[1].GetProperty("target_node_id").GetString());
        Assert.Equal("ends-at-to", Assert.Single(rows[1].GetProperty("mapping_edge_ids").EnumerateArray())
            .GetString());
    }

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Production_root_witnesses_keep_authorized_source_identity_separate_from_dependency_proof(
        string providerId)
    {
        var result = await Provider(providerId, Ready(), TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var payload = Assert.Single(result.Items).Payload;
        using var witnesses = JsonDocument.Parse(payload["source_witnesses"]);
        var rows = witnesses.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("source-1", rows[0].GetProperty("source_id").GetString());
        Assert.Equal(SourceA, rows[0].GetProperty("target_node_id").GetString());
        Assert.Equal("source-2", rows[1].GetProperty("source_id").GetString());
        Assert.Equal(SourceB, rows[1].GetProperty("target_node_id").GetString());
        Assert.All(rows, row => Assert.Equal(0, row.GetProperty("mapping_edge_ids").GetArrayLength()));
        using var dependencyEdges = JsonDocument.Parse(payload["proof_edges"]);
        Assert.All(dependencyEdges.RootElement.EnumerateArray().ToArray(), edge =>
            Assert.Equal("depends_on", edge.GetProperty("relation").GetString()));
    }

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Complete_root_only_mapping_can_prove_empty_graph_without_guessing_service_names(
        string providerId)
    {
        var query = Ready();
        query.TopologyPathResponse = (_, _, _) => Task.FromResult(
            new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        query.TopologyGroupedAncestorResponse = (_, _, _) => Task.FromResult(
            new TopologyCommonAncestorResult(TopologyGraphResultStatus.Unreachable, null, [], 1));
        var result = await Provider(providerId, query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Empty, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("Evaluated", result.Telemetry!.Evaluation);
    }

    [Theory]
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Missing_source_identity_never_uses_a_visible_other_source_as_a_guess(string providerId)
    {
        var query = Ready();
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                new("source-2", "", null, TopologySourceTargetStatus.Missing, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (_, _, _) =>
        { graphCalls++; throw new InvalidOperationException("No graph traversal after missing identity."); };
        query.TopologyGroupedAncestorResponse = (_, _, _) =>
        { graphCalls++; throw new InvalidOperationException("No grouped ancestor after missing identity."); };
        var result = await Provider(providerId, query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Contains("not guessed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, graphCalls);
    }

    [Fact]
    public async Task Mapping_second_page_reserves_shared_budget_before_io_and_empty_requires_all_finals()
    {
        var query = Ready();
        var mappingCalls = 0;
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, cursor, _) =>
        {
            mappingCalls++;
            return Task.FromResult(cursor is null
                ? new TopologySourceTargetsPage([
                    new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                ], "next", 7, 1)
                : new TopologySourceTargetsPage([
                    new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
                ], null, 7, 1));
        };
        query.TopologyPathResponse = (_, _, _) =>
        {
            graphCalls++;
            return Task.FromResult(new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        var blocked = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(100, 100, 1, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, blocked.Status);
        Assert.Contains("BudgetExceeded", blocked.Detail, StringComparison.Ordinal);
        Assert.Equal(1, mappingCalls);
        Assert.Equal(0, graphCalls);

        mappingCalls = 0;
        var complete = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(100, 100, 4, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Empty, complete.Status);
        Assert.Equal(2, mappingCalls);
        Assert.Equal(2, graphCalls);
    }

    [Fact]
    public async Task Full_mapping_target_page_requires_a_separate_terminal_page_and_shared_budget()
    {
        var query = Ready();
        var targets = Enumerable.Range(0, 100).Select(index =>
            new TopologySourceTargetChunk("source-1", SourceA,
                new TopologySourceTarget(GraphNode(TopologyNodeKind.Service, 1000 + index),
                    [$"contains-{index}"], [SourceA, GraphNode(TopologyNodeKind.Service, 1000 + index)]),
                null, null)).ToArray();
        var mappingCalls = 0;
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, pageSize, cursor, _) =>
        {
            mappingCalls++;
            Assert.Equal(100, pageSize);
            return Task.FromResult(cursor is null
                ? new TopologySourceTargetsPage(targets, "terminal-page", 7, 1)
                : new TopologySourceTargetsPage([
                    new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                    new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
                ], null, 7, 1));
        };
        query.TopologyPathResponse = (_, _, _) =>
        {
            graphCalls++;
            return Task.FromResult(new TopologyPathResult(TopologyGraphResultStatus.Unreachable,
                [], [], null, 1));
        };
        var blocked = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(102, 100, 1, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, blocked.Status);
        Assert.Contains("BudgetExceeded", blocked.Detail, StringComparison.Ordinal);
        Assert.Equal(1, mappingCalls);
        Assert.Equal(0, graphCalls);

        mappingCalls = 0;
        var complete = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(102, 100, 204, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Empty, complete.Status);
        Assert.Equal("Evaluated", complete.Telemetry!.Evaluation);
        Assert.Equal(2, mappingCalls);
        Assert.Equal(202, graphCalls);
    }

    [Fact]
    public async Task Mapping_target_cannot_claim_complete_without_a_separate_terminal_chunk()
    {
        var query = Ready();
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA,
                    new TopologySourceTarget(GraphNode(TopologyNodeKind.Service, 1101), ["contains"],
                        [SourceA, GraphNode(TopologyNodeKind.Service, 1101)]),
                    TopologySourceTargetStatus.Complete, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (_, _, _) =>
        {
            graphCalls++;
            throw new InvalidOperationException("A target is not an exhaustion marker.");
        };
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Equal(0, graphCalls);
    }

    [Fact]
    public async Task Hidden_source_mapping_never_falls_back_to_root_dependency_or_leaks_target_identity()
    {
        var query = Ready();
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Hidden, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (_, _, _) =>
        { graphCalls++; return Task.FromResult(query.TopologyPath!); };
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.DoesNotContain(SourceB, result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, graphCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diamond_mapping_keeps_one_canonical_target_after_all_raw_pages_and_charges_both_chains(
        bool reverseArrival)
    {
        var serviceA = GraphNode(TopologyNodeKind.Service, 31);
        var serviceB = GraphNode(TopologyNodeKind.Service, 32);
        var instance = GraphNode(TopologyNodeKind.ServiceInstance, 33);
        var query = Ready();
        var mappingCalls = 0;
        var graphCalls = 0;
        TopologySourceTargetChunk[] Chain(string service, string firstEdge, string secondEdge) =>
        [
            new("source-1", SourceA, new TopologySourceTarget(service, [firstEdge],
                [SourceA, service]), null, null),
            new("source-1", SourceA, new TopologySourceTarget(instance, [firstEdge, secondEdge],
                [SourceA, service, instance]), null, null),
        ];
        var first = reverseArrival ? Chain(serviceB, "contains-b0", "contains-b1") :
            Chain(serviceA, "contains-a0", "contains-a1");
        var second = reverseArrival ? Chain(serviceA, "contains-a0", "contains-a1") :
            Chain(serviceB, "contains-b0", "contains-b1");
        query.TopologySourceTargetPageResponse = (_, _, _, _, cursor, _) =>
        {
            mappingCalls++;
            return Task.FromResult(cursor is null
                ? new TopologySourceTargetsPage(first, "diamond-next", 7, 1)
                : new TopologySourceTargetsPage([
                    .. second,
                    new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                    new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
                ], null, 7, 1));
        };
        query.TopologyPathResponse = (request, _, _) =>
        {
            graphCalls++;
            return Task.FromResult(request.FromNodeId == instance && request.ToNodeId == SourceB
                ? new TopologyPathResult(TopologyGraphResultStatus.Found,
                    [instance, SourceB], ["observed-diamond"], null, 1)
                : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        query.TopologyEdges["observed-diamond"] = Detail("observed-diamond", instance, SourceB);

        var exact = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(5, 5, 11, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, exact.Status);
        Assert.Equal(2, mappingCalls);
        Assert.Equal(8, graphCalls);
        using (var witnessJson = JsonDocument.Parse(Assert.Single(exact.Items).Payload["source_witnesses"]))
        {
            var witness = witnessJson.RootElement[0];
            Assert.Equal("source-1", witness.GetProperty("source_id").GetString());
            Assert.Equal(instance, witness.GetProperty("target_node_id").GetString());
            Assert.Equal(new[] { "contains-a0", "contains-a1" },
                witness.GetProperty("mapping_edge_ids").EnumerateArray()
                    .Select(static value => value.GetString()!));
        }

        mappingCalls = 0;
        graphCalls = 0;
        var over = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(5, 3, 11, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, over.Status);
        Assert.Equal("NotComparable", over.Telemetry!.Evaluation);
        Assert.Empty(over.Items);
        Assert.Contains("BudgetExceeded", over.Detail, StringComparison.Ordinal);
        Assert.Equal(2, mappingCalls);
        Assert.Equal(0, graphCalls);
    }

    [Fact]
    public async Task Diamond_hidden_terminal_after_raw_chains_never_emits_a_finding()
    {
        var service = GraphNode(TopologyNodeKind.Service, 34);
        var query = Ready();
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, cursor, _) => Task.FromResult(cursor is null
            ? new TopologySourceTargetsPage([
                new("source-1", SourceA, new TopologySourceTarget(service, ["contains-visible"],
                    [SourceA, service]), null, null),
            ], "hidden-next", 7, 1)
            : new TopologySourceTargetsPage([
                new("source-1", SourceA, null, TopologySourceTargetStatus.Hidden, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (_, _, _) =>
        {
            graphCalls++;
            throw new InvalidOperationException("Hidden mapping must not reach graph traversal.");
        };
        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Empty(result.Items);
        Assert.Equal(0, graphCalls);
        Assert.DoesNotContain(service, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shared_mapped_target_zero_hop_does_not_poison_another_source_pair_proof()
    {
        var shared = GraphNode(TopologyNodeKind.Service, 35);
        var query = Ready();
        var pathCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA, new TopologySourceTarget(shared, ["contains-a"],
                    [SourceA, shared]), null, null),
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                new("source-2", SourceB, new TopologySourceTarget(shared, ["contains-b"],
                    [SourceB, shared]), null, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (request, _, _) =>
        {
            pathCalls++;
            Assert.False(request.FromNodeId == shared && request.ToNodeId == shared,
                "A shared mapped ID is not a causal path query.");
            return Task.FromResult(request.FromNodeId == SourceA && request.ToNodeId == SourceB
                ? new TopologyPathResult(TopologyGraphResultStatus.Found,
                    [SourceA, SourceB], ["real-dependency"], null, 1)
                : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        };
        query.TopologyEdges["real-dependency"] = Detail("real-dependency", SourceA, SourceB);

        var result = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(3, 3, 8, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        Assert.Single(result.Items);
        Assert.Equal(6, pathCalls); // 2×2 cross-pairs minus shared→shared, each remaining in both directions.
        Assert.Equal("[\"real-dependency\"]", result.Items[0].Payload["edge_ids"]);
        Assert.Equal("Evaluated", result.Telemetry!.Evaluation);
    }

    [Fact]
    public async Task Production_mapping_page_revision_change_restarts_before_traversal()
    {
        var query = Ready();
        var graphCalls = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, cursor, _) => Task.FromResult(cursor is null
            ? new TopologySourceTargetsPage([
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
            ], "next", 7, 1)
            : new TopologySourceTargetsPage([
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 8, 1));
        query.TopologyPathResponse = (_, _, _) =>
        { graphCalls++; return Task.FromResult(query.TopologyPath!); };
        var revisions = new MutableRevisionSource(new(7, 1));
        var provider = new TopologyGraphPathProvider(query, TopologyProviderBudget.Default,
            new TopologyPublicationFence(revisions));
        var result = await provider.GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.True(result.Truncated);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Equal(0, graphCalls);
    }

    [Fact]
    public async Task Service_and_instance_are_both_candidates_and_shorter_observed_proof_wins()
    {
        var serviceA = GraphNode(TopologyNodeKind.Service, 11);
        var instanceA = GraphNode(TopologyNodeKind.ServiceInstance, 12);
        var serviceB = GraphNode(TopologyNodeKind.Service, 13);
        var middle = GraphNode(TopologyNodeKind.Service, 14);
        var query = Ready();
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA, new TopologySourceTarget(serviceA, ["contains-a"],
                    [SourceA, serviceA]), null, null),
                new("source-1", SourceA, new TopologySourceTarget(instanceA, ["contains-a", "contains-i"],
                    [SourceA, serviceA, instanceA]),
                    null, null),
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                new("source-2", SourceB, new TopologySourceTarget(serviceB, ["contains-b"],
                    [SourceB, serviceB]),
                    null, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (request, _, _) => Task.FromResult(
            request.FromNodeId == instanceA && request.ToNodeId == serviceB
                ? new TopologyPathResult(TopologyGraphResultStatus.Found,
                    [instanceA, serviceB], ["observed-fast"], null, 1)
                : request.FromNodeId == serviceA && request.ToNodeId == serviceB
                    ? new TopologyPathResult(TopologyGraphResultStatus.Found,
                        [serviceA, middle, serviceB], ["observed-slow-1", "observed-slow-2"], null, 1)
                    : new TopologyPathResult(TopologyGraphResultStatus.Unreachable, [], [], null, 1));
        query.TopologyEdges["observed-fast"] = Detail("observed-fast", instanceA, serviceB);
        query.TopologyEdges["observed-slow-1"] = Detail("observed-slow-1", serviceA, middle);
        query.TopologyEdges["observed-slow-2"] = Detail("observed-slow-2", middle, serviceB);
        var result = await Provider("topology.graph-path", query,
            new TopologyProviderBudget(6, 6, 20, 1024 * 1024)).GatherAsync(
                Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        var payload = Assert.Single(result.Items).Payload;
        Assert.Equal(JsonSerializer.Serialize(new[] { instanceA, serviceB }, BundleSerializer.Options),
            payload["node_ids"]);
        Assert.Equal("[\"observed-fast\"]", payload["edge_ids"]);
        using var witnesses = JsonDocument.Parse(payload["source_witnesses"]);
        var rows = witnesses.RootElement.EnumerateArray().ToArray();
        Assert.Equal("source-1", rows[0].GetProperty("source_id").GetString());
        Assert.Equal(instanceA, rows[0].GetProperty("target_node_id").GetString());
        Assert.Equal(new[] { "contains-a", "contains-i" }, rows[0].GetProperty("mapping_edge_ids")
            .EnumerateArray().Select(static value => value.GetString()!));
        Assert.Equal("source-2", rows[1].GetProperty("source_id").GetString());
        Assert.Equal(serviceB, rows[1].GetProperty("target_node_id").GetString());
        using var proof = JsonDocument.Parse(payload["proof_edges"]);
        Assert.Equal("observed-fast", Assert.Single(proof.RootElement.EnumerateArray().ToArray())
            .GetProperty("id").GetString());
    }

    [Fact]
    public async Task Instance_mapping_without_intermediate_service_is_not_treated_as_complete_or_budget_free()
    {
        var instance = GraphNode(TopologyNodeKind.ServiceInstance, 15);
        var query = Ready();
        var traversals = 0;
        query.TopologySourceTargetPageResponse = (_, _, _, _, _, _) => Task.FromResult(
            new TopologySourceTargetsPage([
                new("source-1", SourceA, new TopologySourceTarget(instance, ["contains-service", "contains-instance"],
                    [SourceA, GraphNode(TopologyNodeKind.Service, 16), instance]),
                    null, null),
                new("source-1", SourceA, null, TopologySourceTargetStatus.Complete, null),
                new("source-2", SourceB, null, TopologySourceTargetStatus.Complete, null),
            ], null, 7, 1));
        query.TopologyPathResponse = (_, _, _) =>
        {
            traversals++;
            throw new InvalidOperationException("Incomplete Contains chain must not reach traversal.");
        };

        var result = await Provider("topology.graph-path", query, TopologyProviderBudget.Default).GatherAsync(
            Window, Scope, GatherBudget.Default, TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal("NotComparable", result.Telemetry!.Evaluation);
        Assert.Contains("mapping proof is incomplete", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, traversals);
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
    [InlineData("topology.graph-path")]
    [InlineData("topology.common-ancestor")]
    public async Task Production_ready_repair_stamp_allows_mapping_and_proof_before_outer_fence(string providerId)
    {
        var query = Ready();
        var revisions = new MutableRevisionSource(new(7, 1)
        {
            RepairStamp = new(17, "ready-certificate-digest"),
        });
        var fence = new TopologyPublicationFence(revisions);
        IEvidenceProvider provider = providerId == "topology.graph-path"
            ? new TopologyGraphPathProvider(query, TopologyProviderBudget.Default, fence)
            : new TopologyCommonAncestorProvider(query, TopologyProviderBudget.Default, fence);

        var result = await provider.GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);

        Assert.Equal(EvidenceStatus.Gathered, result.Status);
        Assert.Single(result.Items);
        Assert.Equal(2, revisions.Reads);
        Assert.Equal(new TopologyRepairReadStamp(17, "ready-certificate-digest"), revisions.Current.RepairStamp);
    }

    [Theory]
    [InlineData("topology.graph-path", false)]
    [InlineData("topology.graph-path", true)]
    [InlineData("topology.common-ancestor", false)]
    [InlineData("topology.common-ancestor", true)]
    public async Task Production_repair_stamp_change_during_proof_restarts_outer_fence(
        string providerId, bool changeGeneration)
    {
        var query = Ready();
        var revisions = new MutableRevisionSource(new(7, 1)
        {
            RepairStamp = new(17, "ready-certificate-digest"),
        });
        var proofReads = 0;
        query.TopologyEdgeResponse = (edgeId, _, _, _) =>
        {
            proofReads++;
            revisions.Current = new(7, 1)
            {
                RepairStamp = changeGeneration
                    ? new(18, "ready-certificate-digest")
                    : new(17, "different-certificate-digest"),
            };
            return Task.FromResult<TopologyEdgeDetail?>(query.TopologyEdges[edgeId]);
        };
        var fence = new TopologyPublicationFence(revisions);
        IEvidenceProvider provider = providerId == "topology.graph-path"
            ? new TopologyGraphPathProvider(query, TopologyProviderBudget.Default, fence)
            : new TopologyCommonAncestorProvider(query, TopologyProviderBudget.Default, fence);

        var result = await provider.GatherAsync(Window, Scope, GatherBudget.Default,
            TestContext.Current.CancellationToken);

        Assert.True(proofReads > 0, "Repair drift must be injected after a real proof-detail read.");
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
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var cancelClock = Stopwatch.StartNew();
        caller.Cancel();
        await tokenObserved.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var tokenObservedAfter = cancelClock.Elapsed;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gathering.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        var completedAfter = cancelClock.Elapsed;
        Assert.True(tokenObservedAfter <= TimeSpan.FromSeconds(1),
            $"Ancestor query token observed cancellation after {tokenObservedAfter}.");
        Assert.True(completedAfter <= TimeSpan.FromSeconds(2),
            $"Ancestor provider returned cancellation after {completedAfter}.");
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
        var budget = new TopologyProviderBudget(100, 100, 2, 1024 * 1024);
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
            "node" => 4, "edge" => 4, "page" => 7, _ => serializedBytes,
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

    private static string GraphNode(TopologyNodeKind kind, int value) => TopologyIdentity.Node(kind,
        Guid.Parse($"00000000-0000-0000-0000-{value:D12}"));

    private sealed class MutableRevisionSource(TopologyPublicationRevision initial)
        : ITopologyPublicationRevisionSource
    {
        private TopologyPublicationRevision _current = Ready(initial);
        public TopologyPublicationRevision Current
        {
            get => _current;
            set => _current = Ready(value);
        }
        public int Reads { get; private set; }

        private static TopologyPublicationRevision Ready(TopologyPublicationRevision revision) =>
            revision.RepairStamp is null ? revision with { RepairStamp = new(1, "provider-test-ready") } : revision;

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Current);
        }
    }
}
