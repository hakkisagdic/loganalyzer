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
    private readonly TopologyPublicationFence? _publicationFence;

    public TopologyCommonAncestorProvider(IScopedQuery query) : this(query, TopologyProviderBudget.Default, null) { }
    public TopologyCommonAncestorProvider(IScopedQuery query, TopologyProviderBudget topologyBudget)
        : this(query, topologyBudget, null) { }
    public TopologyCommonAncestorProvider(IScopedQuery query, TopologyPublicationFence publicationFence)
        : this(query, TopologyProviderBudget.Default, publicationFence) { }
    public TopologyCommonAncestorProvider(IScopedQuery query, TopologyProviderBudget topologyBudget,
        TopologyPublicationFence? publicationFence)
    { _query = query; _topologyBudget = topologyBudget; _publicationFence = publicationFence; }

    public string Id => "topology.common-ancestor";
    public EvidenceKind Kind => EvidenceKind.Topology;
    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget,
        CancellationToken cancellationToken) =>
        TopologyGraphEvidence.GatherAsync(_query, true, Id, window, scope, budget, _topologyBudget,
            _publicationFence, cancellationToken);
}

internal static class TopologyGraphEvidence
{
    private const int MappingPageSize = 100;
    private sealed record SourceCandidate(string NodeId, IReadOnlyList<string> MappingEdgeIds,
        IReadOnlyList<string> MappingNodeIds);
    private sealed record SourceCandidateGroup(string SourceId, string SourceNodeId,
        IReadOnlyList<SourceCandidate> Candidates);
    private sealed record SourceWitness(string SourceId, string TargetNodeId,
        IReadOnlyList<string> MappingEdgeIds, IReadOnlyList<string> Nodes, IReadOnlyList<string> EdgeIds);
    private sealed class SourceGroupBuilder(string sourceId)
    {
        public string SourceId { get; } = sourceId;
        public string? SourceNodeId { get; set; }
        public Dictionary<string, SourceCandidate> Targets { get; } = new(StringComparer.Ordinal);
        public bool Complete { get; set; }
    }

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
            var usage = new TopologyProviderUsage(topologyBudget);
            return publicationFence is null
                ? await EvaluateAsync(query, ancestor, providerId, affectedSourceIds, window, scope, budget, usage,
                    null, timeout.Token)
                : await publicationFence.ExecuteAsync((revision, token) =>
                    EvaluateAsync(query, ancestor, providerId, affectedSourceIds, window, scope, budget, usage,
                        revision, token),
                    timeout.Token);
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or ArgumentException)
        { return Slice(providerId, EvidenceStatus.Failed, "QueryUnavailable", "Failed", true); }
    }

    private static async Task<EvidenceSlice> EvaluateAsync(IScopedQuery query, bool ancestor, string providerId,
        string[] affectedSourceIds, RcaWindow window, AccessScope scope, GatherBudget budget,
        TopologyProviderUsage usage, TopologyPublicationRevision? expectedRevision, CancellationToken token)
    {
        var declaredStateClock = TopologyIdentity.Nano(window.To) - 1m;
        if (declaredStateClock < 0)
            throw new ArgumentOutOfRangeException(nameof(window), "RCA end must have a preceding graph instant.");
        var sourceGroups = await ResolveSourceGroupsAsync(query, affectedSourceIds,
            declaredStateClock, scope, usage, expectedRevision, token);
        if (expectedRevision?.ClickHouseWatermark > long.MaxValue)
            throw new TopologyProofUnavailableException("Graph publication cannot be represented by the query.");
        long? expectedSequence = expectedRevision is null ? null : checked((long)expectedRevision.ClickHouseWatermark);
        if (ancestor)
            return await AncestorAsync(query, providerId, sourceGroups, window, scope, usage,
                expectedSequence, token);
        return await PathsAsync(query, providerId, sourceGroups, window, scope, budget, usage,
            expectedSequence, token);
    }

    private static async Task<SourceCandidateGroup[]> ResolveSourceGroupsAsync(IScopedQuery query,
        string[] affectedSourceIds, decimal readClock, AccessScope scope, TopologyProviderUsage usage,
        TopologyPublicationRevision? expectedRevision, CancellationToken token)
    {
        var builders = affectedSourceIds.ToDictionary(static sourceId => sourceId,
            static sourceId => new SourceGroupBuilder(sourceId), StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        TopologyPublicationRevision? pageRevision = null;
        do
        {
            // Reserve each mapping page before I/O. A full final page needs no
            // speculative extra read; an explicit continuation does.
            RequireBudget(usage.NextPage());
            var page = await query.ResolveTopologySourceTargetsPageAsync(affectedSourceIds, readClock,
                scope, MappingPageSize, cursor, token);
            var current = new TopologyPublicationRevision(page.PostgresEpoch, page.ClickHouseWatermark);
            if (page.Items.Count > MappingPageSize || current.PostgresEpoch < 0)
                throw new TopologyProofUnavailableException("Source mapping page is inconsistent.");
            if (pageRevision is not null && current != pageRevision ||
                expectedRevision is not null && current != expectedRevision)
                throw new TopologyRestartRequiredException("Source mapping publication changed during paging.");
            pageRevision ??= current;
            foreach (var chunk in page.Items)
            {
                if (!builders.TryGetValue(chunk.SourceId, out var builder) || builder.Complete)
                    throw new TopologyProofUnavailableException("Source mapping page is inconsistent.");
                if (chunk.FinalStatus is TopologySourceTargetStatus.Missing or TopologySourceTargetStatus.Hidden
                    or TopologySourceTargetStatus.Ambiguous)
                    throw new TopologyProofUnavailableException(
                        "Affected source topology mapping is incomplete or outside scope; identity was not guessed.");
                if ((chunk.Target is null) == (chunk.FinalStatus is null) ||
                    chunk.FinalStatus is not null and not TopologySourceTargetStatus.Complete ||
                    !HasKind(chunk.SourceNodeId, TopologyNodeKind.Source) ||
                    builder.SourceNodeId is not null && builder.SourceNodeId != chunk.SourceNodeId)
                    throw new TopologyProofUnavailableException("Source mapping identity is inconsistent.");
                builder.SourceNodeId = chunk.SourceNodeId;
                RequireBudget(usage.IncludeNodes([chunk.SourceNodeId]));
                if (chunk.Target is { } target)
                {
                    var kind = KindOrNull(target.NodeId);
                    var expectedLength = kind == TopologyNodeKind.Service ? 1 :
                        kind == TopologyNodeKind.ServiceInstance ? 2 : 0;
                    if (expectedLength == 0 || target.MappingEdgeIds.Count != expectedLength ||
                        target.MappingNodeIds.Count != expectedLength + 1 ||
                        target.MappingNodeIds[0] != chunk.SourceNodeId ||
                        target.MappingNodeIds[^1] != target.NodeId ||
                        expectedLength == 2 && !HasKind(target.MappingNodeIds[1], TopologyNodeKind.Service) ||
                        target.MappingEdgeIds.Any(string.IsNullOrWhiteSpace) ||
                        target.MappingEdgeIds.Distinct(StringComparer.Ordinal).Count() != expectedLength ||
                        target.MappingNodeIds.Distinct(StringComparer.Ordinal).Count() != expectedLength + 1)
                        throw new TopologyProofUnavailableException("Source mapping proof is inconsistent.");
                    // Charge every raw authorized chain before deduplicating a
                    // target. Distinct intermediate nodes and Contains edges
                    // still count even when two chains end at one Instance.
                    RequireBudget(usage.IncludeNodes(target.MappingNodeIds));
                    RequireBudget(usage.IncludeEdges(target.MappingEdgeIds));
                    var candidate = new SourceCandidate(target.NodeId, target.MappingEdgeIds,
                        target.MappingNodeIds);
                    if (!builder.Targets.TryAdd(target.NodeId, candidate) &&
                        CompareMapping(candidate, builder.Targets[target.NodeId]) < 0)
                        builder.Targets[target.NodeId] = candidate;
                }
                else if (chunk.FinalStatus is null)
                    throw new TopologyProofUnavailableException("Source mapping page is incomplete.");
                if (chunk.FinalStatus == TopologySourceTargetStatus.Complete) builder.Complete = true;
            }
            cursor = page.Cursor;
            if (cursor is not null && !seenCursors.Add(cursor))
                throw new TopologyProofUnavailableException("Source mapping cursor repeated.");
        } while (cursor is not null);
        if (builders.Values.Any(static builder => !builder.Complete || builder.SourceNodeId is null))
            throw new TopologyProofUnavailableException(
                "Affected source topology mapping is incomplete or outside scope; identity was not guessed.");
        foreach (var builder in builders.Values)
        foreach (var instance in builder.Targets.Values.Where(static target =>
                     KindOrNull(target.NodeId) == TopologyNodeKind.ServiceInstance))
        {
            // An instance proof traverses Source→Service→Instance. The service
            // must be an authorized candidate too, so that the intermediate
            // node is charged to the shared graph budget before traversal.
            if (!builder.Targets.Values.Any(service =>
                    KindOrNull(service.NodeId) == TopologyNodeKind.Service &&
                    service.NodeId == instance.MappingNodeIds[1] &&
                    service.MappingEdgeIds[0] == instance.MappingEdgeIds[0]))
                throw new TopologyProofUnavailableException("Source mapping proof is incomplete.");
        }
        return affectedSourceIds.Select(sourceId =>
        {
            var builder = builders[sourceId];
            return new SourceCandidateGroup(builder.SourceId, builder.SourceNodeId!,
                [new SourceCandidate(builder.SourceNodeId!, [], [builder.SourceNodeId!]),
                    .. builder.Targets.Values.OrderBy(static target => target.NodeId, StringComparer.Ordinal)]);
        }).ToArray();
    }

    private static int CompareMapping(SourceCandidate left, SourceCandidate right)
    {
        static int CompareKeys(IReadOnlyList<string> first, IReadOnlyList<string> second)
        {
            for (var index = 0; index < Math.Min(first.Count, second.Count); index++)
            {
                var compare = string.CompareOrdinal(first[index], second[index]);
                if (compare != 0) return compare;
            }
            return first.Count.CompareTo(second.Count);
        }
        var nodes = CompareKeys(left.MappingNodeIds, right.MappingNodeIds);
        return nodes != 0 ? nodes : CompareKeys(left.MappingEdgeIds, right.MappingEdgeIds);
    }

    private static TopologyNodeKind? KindOrNull(string nodeId)
    {
        try { return TopologyIdentity.Kind(nodeId); }
        catch (ArgumentException) { return null; }
    }

    private static bool HasKind(string nodeId, TopologyNodeKind kind) => KindOrNull(nodeId) == kind;

    private static async Task<EvidenceSlice> PathsAsync(IScopedQuery query, string providerId,
        IReadOnlyList<SourceCandidateGroup> sourceGroups,
        RcaWindow window, AccessScope scope, GatherBudget budget, TopologyProviderUsage usage,
        long? expectedSequence, CancellationToken token)
    {
        var items = new List<EvidenceItem>();
        var clock = TopologyIdentity.Nano(window.To);
        var fromClock = TopologyIdentity.Nano(window.From);
        long? publishedSequence = expectedSequence;
        for (var i = 0; i < sourceGroups.Count; i++)
        for (var j = i + 1; j < sourceGroups.Count; j++)
        {
            if (items.Count >= budget.MaxItems) throw new TopologyBudgetExceededException();
            var candidates = new List<(TraversalProof Proof, EvidenceItem Item)>();
            foreach (var from in sourceGroups[i].Candidates)
            foreach (var to in sourceGroups[j].Candidates)
            {
                // Affected sources are groups, not opaque node-ID ordering.
                // Every authorized cross-group candidate and both directed
                // orientations share one revision and one usage counter.
                var forward = await ReadPathAsync(query, from.NodeId, to.NodeId, clock, fromClock, scope, usage,
                    publishedSequence, token);
                publishedSequence ??= forward.PublishedSequence;
                var reverse = await ReadPathAsync(query, to.NodeId, from.NodeId, clock, fromClock, scope, usage,
                    publishedSequence, token);
                if (forward.NotVerified || reverse.NotVerified)
                    return Slice(providerId, EvidenceStatus.Unavailable,
                        PartialReason(forward.NotVerified ? forward.Reason : reverse.Reason) + "; graph path not verified.",
                        "NotComparable", true);
                foreach (var proof in new[] { forward.Proof, reverse.Proof })
                {
                    if (proof is null) continue;
                    var details = await ReadProofEdgesAsync(query, proof.EdgeIds, window, scope, usage, token);
                    VerifyDirectedProof(proof.Nodes, proof.EdgeIds, details);
                    var witnesses = new[]
                    {
                        new SourceWitness(sourceGroups[i].SourceId, from.NodeId, from.MappingEdgeIds, [], []),
                        new SourceWitness(sourceGroups[j].SourceId, to.NodeId, to.MappingEdgeIds, [], []),
                    };
                    var item = Item(providerId, proof.EdgeIds, proof.Nodes, null, details, window, witnesses);
                    RequireBudget(usage.IncludeSerializedBytes(JsonSerializer.SerializeToUtf8Bytes(item, BundleSerializer.Options).Length));
                    candidates.Add((proof, item));
                }
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
        decimal fromClock,
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
            var result = await query.GetTopologyPathAsync(
                new TopologyPathQuery(from, to, clock, 200, cursor, fromClock, clock)
                { DeclaredStateClockUnixNano = clock - 1m }, scope, token);
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

    private static async Task<EvidenceSlice> AncestorAsync(IScopedQuery query, string providerId,
        IReadOnlyList<SourceCandidateGroup> sourceGroups,
        RcaWindow window, AccessScope scope, TopologyProviderUsage usage, long? expectedSequence,
        CancellationToken token)
    {
        RequireBudget(usage.NextPage());
        var targetGroups = sourceGroups.Select(static group => (IReadOnlyList<string>)group.Candidates
            .Select(static candidate => candidate.NodeId).ToArray()).ToArray();
        var clock = TopologyIdentity.Nano(window.To);
        var result = await query.GetTopologyGroupedCommonAncestorAsync(
            new TopologyGroupedAncestorQuery(targetGroups, clock, TopologyIdentity.Nano(window.From), clock)
            { DeclaredStateClockUnixNano = clock - 1m }, scope, token);
        if (expectedSequence is not null && result.PublishedSequence != expectedSequence)
            throw new TopologyRestartRequiredException("Graph publication changed during ancestor evaluation.");
        if (result.Status == TopologyGraphResultStatus.NotVerified)
            return Slice(providerId, EvidenceStatus.Unavailable, result.Reason ?? "HiddenBoundary", "NotComparable", true);
        if (result.Status != TopologyGraphResultStatus.Found || result.NodeId is null)
            return Slice(providerId, EvidenceStatus.Empty, "Visible strict common ancestor not found.", "Evaluated");
        var edgeIds = result.Paths.SelectMany(static path => path.EdgeIds).Distinct(StringComparer.Ordinal).ToArray();
        var nodesInProof = result.Paths.SelectMany(static path => path.Nodes).Distinct(StringComparer.Ordinal).ToArray();
        if (result.Paths.Count != sourceGroups.Count ||
            result.Paths.Where((path, index) => !sourceGroups[index].Candidates
                    .Any(candidate => candidate.NodeId == path.TargetNodeId))
                .Any() ||
            result.Paths.Any(path => path.Nodes.Count < 2 || path.Nodes[0] != result.NodeId ||
                path.Nodes[^1] != path.TargetNodeId || path.EdgeIds.Count != path.Nodes.Count - 1))
            throw new TopologyProofUnavailableException("Common ancestor proof is incomplete.");
        RequireBudget(usage.IncludeNodes(nodesInProof));
        RequireBudget(usage.IncludeEdges(edgeIds));
        var details = await ReadProofEdgesAsync(query, edgeIds, window, scope, usage, token);
        foreach (var path in result.Paths) VerifyDirectedProof(path.Nodes, path.EdgeIds, details);
        var witnesses = result.Paths.Select((path, index) =>
        {
            var group = sourceGroups[index];
            var candidate = group.Candidates.Single(candidate => candidate.NodeId == path.TargetNodeId);
            return new SourceWitness(group.SourceId, candidate.NodeId, candidate.MappingEdgeIds,
                path.Nodes, path.EdgeIds);
        }).ToArray();
        var item = Item(providerId, edgeIds, nodesInProof, result.NodeId, details, window, witnesses);
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
        IReadOnlyList<string> edgeIds, RcaWindow window, AccessScope scope, TopologyProviderUsage usage,
        CancellationToken token)
    {
        var details = new List<TopologyEdgeDetail>(edgeIds.Count);
        var fromClock = TopologyIdentity.Nano(window.From);
        var toClock = TopologyIdentity.Nano(window.To);
        foreach (var edgeId in edgeIds)
        {
            RequireBudget(usage.NextPage());
            var detail = await query.GetTopologyEdgeAsync(edgeId, toClock, fromClock, toClock,
                toClock - 1m, scope, null, 200, token);
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
        string? ancestor, IReadOnlyList<TopologyEdgeDetail> details, RcaWindow window,
        IReadOnlyList<SourceWitness> sourceWitnesses)
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
            ["source_witnesses"] = JsonSerializer.Serialize(sourceWitnesses, BundleSerializer.Options),
            ["window_from"] = window.From.ToString("O", CultureInfo.InvariantCulture),
            ["window_to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
        };
        if (ancestor is not null) payload["ancestor_node_id"] = ancestor;
        var canonical = providerId + "|" + string.Join('|', edgeIds) + "|" + string.Join('|', nodes) + "|" + ancestor
            + "|" + payload["source_witnesses"];
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
