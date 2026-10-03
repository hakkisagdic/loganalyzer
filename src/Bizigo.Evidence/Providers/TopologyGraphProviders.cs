using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

public sealed class TopologyGraphPathProvider : IEvidenceProvider
{
    private readonly IScopedQuery _query;
    private readonly TopologyProviderBudget _topologyBudget;
    private readonly TopologyPublicationFence? _publicationFence;

    public TopologyGraphPathProvider(IScopedQuery query) : this(query, TopologyProviderBudget.Default, null) { }
    public TopologyGraphPathProvider(IScopedQuery query, TopologyProviderBudget topologyBudget)
        : this(query, topologyBudget, null) { }
    public TopologyGraphPathProvider(IScopedQuery query, TopologyPublicationFence publicationFence)
        : this(query, TopologyProviderBudget.Default, publicationFence) { }
    public TopologyGraphPathProvider(IScopedQuery query, TopologyProviderBudget topologyBudget,
        TopologyPublicationFence? publicationFence)
    { _query = query; _topologyBudget = topologyBudget; _publicationFence = publicationFence; }

    public string Id => "topology.graph-path";
    public EvidenceKind Kind => EvidenceKind.Topology;
    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget,
        CancellationToken cancellationToken) =>
        TopologyGraphEvidence.GatherAsync(_query, false, Id, window, scope, budget, _topologyBudget,
            _publicationFence, cancellationToken);
}

public sealed class TopologyCommonAncestorProvider : IEvidenceProvider
{
    private readonly IScopedQuery _query;
    private readonly TopologyProviderBudget _topologyBudget;

    public TopologyCommonAncestorProvider(IScopedQuery query) : this(query, TopologyProviderBudget.Default) { }
    public TopologyCommonAncestorProvider(IScopedQuery query, TopologyProviderBudget topologyBudget)
    { _query = query; _topologyBudget = topologyBudget; }

    public string Id => "topology.common-ancestor";
    public EvidenceKind Kind => EvidenceKind.Topology;
    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget,
        CancellationToken cancellationToken) =>
        TopologyGraphEvidence.GatherAsync(_query, true, Id, window, scope, budget, _topologyBudget,
            null, cancellationToken);
}

internal static class TopologyGraphEvidence
{
    internal static async Task<EvidenceSlice> GatherAsync(IScopedQuery query, bool ancestor, string providerId,
        RcaWindow window, AccessScope scope, GatherBudget budget, TopologyProviderBudget topologyBudget,
        TopologyPublicationFence? publicationFence, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(topologyBudget); topologyBudget.Validate();
        if (budget.MaxItems < 1 || budget.MaxDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        callerToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeout.CancelAfter(budget.MaxDuration);
        try
        {
            var feed = await query.GetTelemetryFeedAsync(TelemetrySignal.Traces, null, scope, timeout.Token);
            if (feed.Status == TelemetryResultStatus.Failed)
                return Slice(providerId, EvidenceStatus.Failed, feed.Error ?? "QueryUnavailable", "Failed");
            if (feed.Status == TelemetryResultStatus.NeverFed)
                return Slice(providerId, EvidenceStatus.NeverFed, "Yetkili trace beslemesi hiç alınmamış.", "NotRun");

            var correlation = new CorrelationWindow
            {
                From = window.From, To = window.To, BaselineFrom = window.BaselineFrom, BaselineTo = window.BaselineTo,
                OwnerGroups = window.OwnerGroups, SourceIds = window.SourceIds,
            };
            var onsets = await query.GetPropagationAsync(correlation, scope, 3, 1001, timeout.Token);
            var affectedSourceIds = onsets.Select(static onset => onset.SourceId).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).Take(21).ToArray();
            if (affectedSourceIds.Length < 2)
                return Slice(providerId, EvidenceStatus.Empty, "Ortak graph değerlendirmesi için en az iki etkilenen kaynak gerekir.", "Evaluated");
            if (affectedSourceIds.Length > 20)
                return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: affected source count exceeds 20.", "NotComparable", true);
            var resolved = await query.ResolveTopologySourceNodesAsync(affectedSourceIds, scope, timeout.Token);
            var nodes = resolved.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (nodes.Length != affectedSourceIds.Length)
                return Slice(providerId, EvidenceStatus.Unavailable,
                    "Affected source topology mapping is absent or outside scope; identity was not guessed.", "NotComparable");
            var usage = new TopologyProviderUsage(topologyBudget);
            RequireBudget(usage.IncludeNodes(nodes));
            if (ancestor)
                return await AncestorAsync(query, providerId, nodes, window, scope, usage, timeout.Token);
            return publicationFence is null
                ? await PathsAsync(query, providerId, nodes, window, scope, budget, usage, timeout.Token)
                : await publicationFence.ExecuteAsync((_, token) =>
                    PathsAsync(query, providerId, nodes, window, scope, budget, usage, token), timeout.Token);
        }
        catch (TopologyBudgetExceededException)
        { return Slice(providerId, EvidenceStatus.Unavailable, "BudgetExceeded: topology proof is incomplete.", "NotComparable", true); }
        catch (TopologyProofUnavailableException ex)
        { return Slice(providerId, EvidenceStatus.Unavailable, ex.Message, "NotComparable", true); }
        catch (TopologyRestartRequiredException)
        { return Slice(providerId, EvidenceStatus.Unavailable, "Graph publication changed during path evaluation.", "NotComparable", true); }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        { return Slice(providerId, EvidenceStatus.Failed, "Timeout", "Failed", true); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        { return Slice(providerId, EvidenceStatus.Failed, "QueryUnavailable", "Failed", true); }
    }

    private static async Task<EvidenceSlice> PathsAsync(IScopedQuery query, string providerId, string[] nodes,
        RcaWindow window, AccessScope scope, GatherBudget budget, TopologyProviderUsage usage,
        CancellationToken token)
    {
        var items = new List<EvidenceItem>();
        var clock = TopologyIdentity.Nano(window.To);
        long? publishedSequence = null;
        for (var i = 0; i < nodes.Length; i++)
        for (var j = i + 1; j < nodes.Length; j++)
        {
            if (items.Count >= budget.MaxItems) throw new TopologyBudgetExceededException();
            // Affected sources are a set. An opaque node ID must not choose
            // which directed relationship is evaluated. Both directions share
            // one usage counter and one published graph revision.
            var forward = await ReadPathAsync(query, nodes[i], nodes[j], clock, scope, usage,
                publishedSequence, token);
            publishedSequence ??= forward.PublishedSequence;
            var reverse = await ReadPathAsync(query, nodes[j], nodes[i], clock, scope, usage,
                publishedSequence, token);
            if (forward.NotVerified || reverse.NotVerified)
                return Slice(providerId, EvidenceStatus.Unavailable,
                    PartialReason(forward.NotVerified ? forward.Reason : reverse.Reason) + "; graph path not verified.",
                    "NotComparable", true);

            var candidates = new List<(TraversalProof Proof, EvidenceItem Item)>(2);
            foreach (var proof in new[] { forward.Proof, reverse.Proof })
            {
                if (proof is null) continue;
                var details = await ReadProofEdgesAsync(query, proof.EdgeIds, clock, scope, usage, token);
                VerifyDirectedProof(proof.Nodes, proof.EdgeIds, details);
                var item = Item(providerId, proof.EdgeIds, proof.Nodes, null, details, window);
                RequireBudget(usage.IncludeSerializedBytes(JsonSerializer.SerializeToUtf8Bytes(item, BundleSerializer.Options).Length));
                candidates.Add((proof, item));
            }
            if (candidates.Count > 0)
                items.Add(candidates.OrderBy(static candidate => candidate.Proof, TraversalProofComparer.Instance)
                    .First().Item);
        }
        var status = items.Count > 0 ? EvidenceStatus.Gathered : EvidenceStatus.Empty;
        return new EvidenceSlice
        {
            ProviderId = providerId, Kind = EvidenceKind.Topology, Status = status, Items = items,
            Detail = items.Count == 0 ? "Visible graph path not found." :
                "Shortest directed depends_on paths; observed edges are correlation, not causal proof.",
            Telemetry = new(TelemetryResultStatus.Data, "Evaluated", []),
        };
    }

    private sealed record TraversalProof(IReadOnlyList<string> Nodes, IReadOnlyList<string> EdgeIds);
    private sealed record PathRead(TraversalProof? Proof, bool NotVerified, long PublishedSequence, string? Reason = null);

    private static string PartialReason(string? reason) => reason is "HiddenBoundary" or "BudgetExceeded" or "QueryPartial"
        ? reason : "QueryPartial";

    private static async Task<PathRead> ReadPathAsync(IScopedQuery query, string from, string to, decimal clock,
        AccessScope scope, TopologyProviderUsage usage, long? expectedSequence, CancellationToken token)
    {
        var proofNodes = new List<string>();
        var proofEdgeIds = new List<string>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        long? sequence = null;
        do
        {
            RequireBudget(usage.NextPage());
            var result = await query.GetTopologyPathAsync(new(from, to, clock, 200, cursor), scope, token);
            if (expectedSequence is not null && result.PublishedSequence != expectedSequence ||
                sequence is not null && result.PublishedSequence != sequence)
                throw new TopologyProofUnavailableException("Graph publication changed during path evaluation.");
            sequence ??= result.PublishedSequence;
            if (result.Status == TopologyGraphResultStatus.NotVerified)
            {
                if (proofNodes.Count != 0) throw new TopologyProofUnavailableException("Graph path changed during paging.");
                return new(null, true, sequence.Value, result.Reason);
            }
            if (result.Status == TopologyGraphResultStatus.Unreachable)
            {
                if (proofNodes.Count != 0 || result.Cursor is not null || result.Nodes.Count != 0 || result.EdgeIds.Count != 0)
                    throw new TopologyProofUnavailableException("Graph path changed during paging.");
                return new(null, false, sequence.Value);
            }
            if (result.Status != TopologyGraphResultStatus.Found || result.Nodes.Count < 2 ||
                result.EdgeIds.Count != result.Nodes.Count - 1 ||
                (proofNodes.Count != 0 && result.Nodes[0] != proofNodes[^1]))
                throw new TopologyProofUnavailableException("Graph path page is inconsistent.");
            RequireBudget(usage.IncludeNodes(result.Nodes));
            RequireBudget(usage.IncludeEdges(result.EdgeIds));
            proofNodes.AddRange(proofNodes.Count == 0 ? result.Nodes : result.Nodes.Skip(1));
            proofEdgeIds.AddRange(result.EdgeIds);
            cursor = result.Cursor;
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new TopologyProofUnavailableException("Graph path cursor repeated.");
        } while (cursor is not null);
        if (proofEdgeIds.Count != proofNodes.Count - 1 || proofNodes[0] != from || proofNodes[^1] != to)
            throw new TopologyProofUnavailableException("Graph path proof is incomplete.");
        return new(new(proofNodes, proofEdgeIds), false, sequence!.Value);
    }

    private sealed class TraversalProofComparer : IComparer<TraversalProof>
    {
        public static TraversalProofComparer Instance { get; } = new();

        public int Compare(TraversalProof? left, TraversalProof? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var byHop = left.EdgeIds.Count.CompareTo(right.EdgeIds.Count);
            if (byHop != 0) return byHop;
            var byNode = CompareKeys(left.Nodes, right.Nodes);
            return byNode != 0 ? byNode : CompareKeys(left.EdgeIds, right.EdgeIds);
        }

        private static int CompareKeys(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
            {
                var compare = string.CompareOrdinal(left[index], right[index]);
                if (compare != 0) return compare;
            }
            return left.Count.CompareTo(right.Count);
        }
    }

    private static async Task<EvidenceSlice> AncestorAsync(IScopedQuery query, string providerId, string[] nodes,
        RcaWindow window, AccessScope scope, TopologyProviderUsage usage, CancellationToken token)
    {
        var result = await query.GetTopologyCommonAncestorAsync(new(nodes, TopologyIdentity.Nano(window.To)), scope, token);
        RequireBudget(usage.NextPage());
        if (result.Status == TopologyGraphResultStatus.NotVerified)
            return Slice(providerId, EvidenceStatus.Unavailable, result.Reason ?? "HiddenBoundary", "NotComparable", true);
        if (result.Status != TopologyGraphResultStatus.Found || result.NodeId is null)
            return Slice(providerId, EvidenceStatus.Empty, "Visible strict common ancestor not found.", "Evaluated");
        var edgeIds = result.Paths.SelectMany(static path => path.EdgeIds).Distinct(StringComparer.Ordinal).ToArray();
        var nodesInProof = result.Paths.SelectMany(static path => path.Nodes).Distinct(StringComparer.Ordinal).ToArray();
        if (result.Paths.Count != nodes.Length ||
            !result.Paths.Select(static path => path.TargetNodeId).Order(StringComparer.Ordinal).SequenceEqual(nodes) ||
            result.Paths.Any(path => path.Nodes.Count < 2 || path.Nodes[0] != result.NodeId ||
                path.Nodes[^1] != path.TargetNodeId || path.EdgeIds.Count != path.Nodes.Count - 1))
            throw new TopologyProofUnavailableException("Common ancestor proof is incomplete.");
        RequireBudget(usage.IncludeNodes(nodesInProof));
        RequireBudget(usage.IncludeEdges(edgeIds));
        var details = await ReadProofEdgesAsync(query, edgeIds, TopologyIdentity.Nano(window.To), scope, usage, token);
        foreach (var path in result.Paths) VerifyDirectedProof(path.Nodes, path.EdgeIds, details);
        var item = Item(providerId, edgeIds, nodesInProof, result.NodeId, details, window);
        RequireBudget(usage.IncludeSerializedBytes(JsonSerializer.SerializeToUtf8Bytes(item, BundleSerializer.Options).Length));
        return new EvidenceSlice
        {
            ProviderId = providerId, Kind = EvidenceKind.Topology, Status = EvidenceStatus.Gathered,
            Items = [item],
            Detail = "Strict common ancestor selected by max-hop, sum-hop, ordinal ID.",
            Telemetry = new(TelemetryResultStatus.Data, "Evaluated", []),
        };
    }

    private static async Task<IReadOnlyList<TopologyEdgeDetail>> ReadProofEdgesAsync(IScopedQuery query,
        IReadOnlyList<string> edgeIds, decimal readClock, AccessScope scope, TopologyProviderUsage usage,
        CancellationToken token)
    {
        var details = new List<TopologyEdgeDetail>(edgeIds.Count);
        foreach (var edgeId in edgeIds)
        {
            RequireBudget(usage.NextPage());
            var detail = await query.GetTopologyEdgeAsync(edgeId, readClock, scope, token);
            if (detail is null || detail.Edge.Id != edgeId ||
                !TopologyIdentity.CanReadEdge(scope, detail.Edge.FromOwnerGroup, detail.Edge.ToOwnerGroup))
                throw new TopologyProofUnavailableException("Scoped edge proof is unavailable.");
            RequireBudget(usage.IncludeNodes([detail.Edge.FromNode, detail.Edge.ToNode]));
            details.Add(detail);
        }
        return details;
    }

    private static void VerifyDirectedProof(IReadOnlyList<string> nodes, IReadOnlyList<string> edgeIds,
        IReadOnlyList<TopologyEdgeDetail> details)
    {
        var byId = details.ToDictionary(static detail => detail.Edge.Id, StringComparer.Ordinal);
        for (var index = 0; index < edgeIds.Count; index++)
        {
            if (!byId.TryGetValue(edgeIds[index], out var detail) ||
                !detail.Edge.Directed || detail.Edge.Relation != TopologyRelation.DependsOn ||
                detail.Edge.FromNode != nodes[index] || detail.Edge.ToNode != nodes[index + 1])
                throw new TopologyProofUnavailableException("Directed depends_on edge proof is inconsistent.");
        }
    }

    private static EvidenceItem Item(string providerId, IReadOnlyList<string> edgeIds, IReadOnlyList<string> nodes,
        string? ancestor, IReadOnlyList<TopologyEdgeDetail> details, RcaWindow window)
    {
        var proofs = details.Select(detail => new
        {
            detail.Edge.Id, detail.Edge.FromNode, detail.Edge.ToNode, detail.Edge.Relation,
            detail.Edge.Provenance, detail.Edge.Directed, detail.Edge.Confidence,
            detail.Edge.FirstSeenUnixNano, detail.Edge.LastSeenUnixNano, detail.Edge.EffectiveExpiry,
            detail.Evidence, detail.EvidenceCursor,
        }).ToArray();
        var payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["edge_ids"] = JsonSerializer.Serialize(edgeIds, BundleSerializer.Options),
            ["node_ids"] = JsonSerializer.Serialize(nodes, BundleSerializer.Options),
            ["proof_edges"] = JsonSerializer.Serialize(proofs, BundleSerializer.Options),
            ["window_from"] = window.From.ToString("O", CultureInfo.InvariantCulture),
            ["window_to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
        };
        if (ancestor is not null) payload["ancestor_node_id"] = ancestor;
        var canonical = providerId + "|" + string.Join('|', edgeIds) + "|" + string.Join('|', nodes) + "|" + ancestor;
        return new(RawSignalEnvelope.Hash(System.Text.Encoding.UTF8.GetBytes(canonical)), providerId,
            EvidenceKind.Topology, window.To, 1, ancestor is null ? "Directed topology path" : "Strict common topology ancestor", payload);
    }

    private static void RequireBudget(bool withinBudget)
    { if (!withinBudget) throw new TopologyBudgetExceededException(); }

    private sealed class TopologyBudgetExceededException : Exception;
    private sealed class TopologyProofUnavailableException(string message) : Exception(message);

    private static EvidenceSlice Slice(string providerId, EvidenceStatus status, string detail, string evaluation,
        bool truncated = false) => new()
    {
        ProviderId = providerId, Kind = EvidenceKind.Topology, Status = status, Detail = detail, Truncated = truncated,
        Telemetry = new(status == EvidenceStatus.NeverFed ? TelemetryResultStatus.NeverFed : status == EvidenceStatus.Failed
            ? TelemetryResultStatus.Failed : TelemetryResultStatus.Empty, evaluation, []),
    };
}
