using System.Diagnostics;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Query;

namespace Bizigo.UnitTests;

public sealed class TopologyGraphOrderingOracleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AccessScope Scope = AccessScope.ForGroups("ordering-oracle", ["A"]);
    private static readonly string Root = Node(1), Left = Node(2), Right = Node(3), Target = Node(4), Isolated = Node(5);

    [Fact]
    public async Task R01_parallel_edges_choose_ordinal_edge_sequence_after_node_sequence()
    {
        TopologyEdgeProjection[] edges = [Edge("z-root-left", Root, Left), Edge("a-root-left", Root, Left),
            Edge("z-left-target", Left, Target), Edge("a-left-target", Left, Target),
            Edge("0-root-right", Root, Right), Edge("0-right-target", Right, Target)];
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var query = Query(edges.OrderBy(_ => random.Next()).ToArray());
            var result = await query.PathAsync(new(Root, Target, 1000), Scope, Ct);
            Assert.Equal(TopologyGraphResultStatus.Found, result.Status);
            Assert.Equal([Root, Left, Target], result.Nodes); // node sequence wins before edge IDs
            Assert.Equal(["a-root-left", "a-left-target"], result.EdgeIds);
        }
    }

    [Fact]
    public async Task R03_cycle_selfloop_permutations_preserve_query_proof_and_bundle_content_hash()
    {
        TopologyEdgeProjection[] edges = [Edge("root-left", Root, Left), Edge("root-right", Root, Right),
            Edge("left-target", Left, Target), Edge("right-target", Right, Target),
            Edge("cycle", Target, Root), Edge("self", Left, Left)];
        string? expectedHash = null;
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var result = await Query(edges.OrderBy(_ => random.Next()).ToArray()).PathAsync(new(Root, Target, 1000), Scope, Ct);
            Assert.Equal([Root, Left, Target], result.Nodes);
            Assert.Equal(["root-left", "left-target"], result.EdgeIds);
            var window = new RcaWindow
            {
                BaselineFrom = DateTimeOffset.UnixEpoch.AddSeconds(-1), BaselineTo = DateTimeOffset.UnixEpoch,
                From = DateTimeOffset.UnixEpoch, To = DateTimeOffset.UnixEpoch.AddSeconds(1),
            };
            var bundle = new EvidenceBundle
            {
                Id = Guid.NewGuid(), GatheredAt = window.To.AddSeconds(seed), Window = window,
                Scope = new(["A"], false), Trust = new(1, 0),
                Slices = [new EvidenceSlice
                {
                    ProviderId = "topology.graph-path", Kind = EvidenceKind.Topology, Status = EvidenceStatus.Gathered,
                    Items = [new("stable-query-proof", "topology.graph-path", EvidenceKind.Topology, window.To, 1,
                        "Observed graph path is not proof of causation", new Dictionary<string, string>
                        {
                            ["node_ids"] = JsonSerializer.Serialize(result.Nodes),
                            ["edge_ids"] = JsonSerializer.Serialize(result.EdgeIds),
                        })],
                }],
            };
            expectedHash ??= bundle.ContentHash;
            Assert.Equal(expectedHash, bundle.ContentHash);
            Assert.Equal(expectedHash, BundleSerializer.Deserialize(BundleSerializer.Serialize(bundle)).ContentHash);
        }
    }

    [Fact]
    public async Task R03_disconnected_cycle_terminates_with_decision_and_honors_cancelled_token()
    {
        var query = Query([Edge("out", Root, Left), Edge("back", Left, Root), Edge("self", Root, Root)]);
        var watch = Stopwatch.StartNew();
        // The task boundary prevents synchronous traversal from hiding a hung
        // implementation before the bounded wait starts. A mutation timeout
        // alone is NOT a kill: final M10 also requires its decision oracle.
        var pending = Task.Run(() => query.PathAsync(new(Root, Isolated, 1000), Scope, Ct), Ct);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(TopologyGraphResultStatus.Unreachable, result.Status);
        Assert.Empty(result.Nodes); Assert.Empty(result.EdgeIds);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            query.PathAsync(new(Root, Target, 1000), Scope, cancelled.Token));
    }

    private static string Node(int id) => TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse($"00000000-0000-0000-0000-{id:D12}"));
    private static TopologyEdgeProjection Edge(string id, string from, string to) => new(id, from, to,
        TopologyRelation.DependsOn, TopologyProvenance.Declared, true, 1, "A", "A", TopologyEdgeVisibility.SameOwner,
        0, 1000, null, 9, 1, false);
    private static TopologyGraphQueryService Query(IReadOnlyList<TopologyEdgeProjection> edges) => new(new Snapshot(new(9, edges)));
    private sealed class Snapshot(TopologyGraphSnapshot value) : ITopologyGraphSnapshotSource
    {
        public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publishedSequence is not null && publishedSequence != value.PublishedSequence)
                throw new TopologySnapshotUnavailableException(publishedSequence.Value);
            return Task.FromResult(value);
        }
    }
}
