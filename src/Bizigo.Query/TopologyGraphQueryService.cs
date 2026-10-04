using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;

namespace Bizigo.Query;

/// <summary>Snapshot-bound, scope-required, deterministic topology traversals.</summary>
public sealed class TopologyGraphQueryService(ITopologyGraphSnapshotSource source)
{
    private const int MaxPageSize = 200;
    private static readonly IComparer<IReadOnlyList<string>> OrdinalSequence =
        Comparer<IReadOnlyList<string>>.Create((left, right) =>
        {
            for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
            {
                var comparison = string.CompareOrdinal(left[index], right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Count.CompareTo(right.Count);
        });
    private readonly ITopologyGraphSnapshotSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<TopologyGraphPage<TopologyNodeProjection>> SearchNodesAsync(TopologyNodeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        var fingerprint = Fingerprint("nodes", scope, query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture),
            query.Kind?.ToString() ?? "*");
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint,
            NodeRead(scope, query.ReadClockUnixNano), cancellationToken);
        var rows = snapshot.Nodes.Where(node => TopologyIdentity.CanReadOwner(scope, node.OwnerGroup) && node.Enabled && !node.Deleted
                && node.ValidFromUnixNano <= query.ReadClockUnixNano
                && (node.ValidToUnixNano is null || query.ReadClockUnixNano < node.ValidToUnixNano))
            .Where(node => query.Kind is null || node.Kind == query.Kind).OrderBy(static node => node.Id, StringComparer.Ordinal).ToArray();
        return Page(rows, query.PageSize, cursor?.LastKey, static node => node.Id, snapshot.PublishedSequence, fingerprint);
    }

    public async Task<TopologyNodeProjection?> GetNodeAsync(string nodeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateNode(nodeId); ArgumentNullException.ThrowIfNull(scope); _ = TopologyExpiry.FromDecimal(readClockUnixNano);
        var snapshot = await ReadSnapshotAsync(null, NodeRead(scope, readClockUnixNano, nodeId), cancellationToken);
        return snapshot.Nodes.SingleOrDefault(node => node.Id == nodeId && TopologyIdentity.CanReadOwner(scope, node.OwnerGroup) && node.Enabled && !node.Deleted
            && node.ValidFromUnixNano <= readClockUnixNano && (node.ValidToUnixNano is null || readClockUnixNano < node.ValidToUnixNano));
    }

    public async Task<TopologyGraphPage<TopologyEdgeProjection>> SearchEdgesAsync(TopologyEdgeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var expiryClock = ResolveExpiryClock(query.ReadClockUnixNano, query.ExpiryReadClockUnixNano);
        var fingerprint = Fingerprint("edges", scope, query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture),
            query.Relation?.ToString() ?? "*", query.Provenance?.ToString() ?? "*", WindowPart(window));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint,
            EdgeRead(scope, query.ReadClockUnixNano, expiryClock, window,
                relation: query.Relation, provenance: query.Provenance), cancellationToken);
        EnsureEdgeListReady(snapshot, scope, query.ReadClockUnixNano, expiryClock, window,
            query.Relation, query.Provenance);
        var rows = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window,
                expiryReadClockUnixNano: expiryClock)
            .Where(edge => query.Relation is null || edge.Relation == query.Relation)
            .Where(edge => query.Provenance is null || edge.Provenance == query.Provenance)
            .OrderBy(static edge => edge.Id, StringComparer.Ordinal).ToArray();
        var page = Page(rows, query.PageSize, cursor?.LastKey, static edge => edge.Id, snapshot.PublishedSequence, fingerprint);
        return page with { EarliestEvidenceExpiryUnixNano = MinExpiry(rows) };
    }

    public Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) =>
        GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, null, MaxPageSize, null, null, null,
            null, cancellationToken);

    public Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default)
        => GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, evidenceCursor, evidencePageSize,
            null, null, null, null, cancellationToken);

    public Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default)
        => GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, null, MaxPageSize,
            fromUnixNano, toUnixNano, null, null, cancellationToken);

    public Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default)
        => GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, evidenceCursor, evidencePageSize,
            fromUnixNano, toUnixNano, null, null, cancellationToken);

    public Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, decimal declaredStateClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default)
        => GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, evidenceCursor, evidencePageSize,
            fromUnixNano, toUnixNano, declaredStateClockUnixNano, null, cancellationToken);

    /// <summary>The production scoped gate supplies the current server expiry clock.</summary>
    public Task<TopologyEdgeDetail?> GetEdgeAtExpiryAsync(string edgeId, decimal readClockUnixNano,
        decimal? fromUnixNano, decimal? toUnixNano, decimal? declaredStateClockUnixNano,
        decimal expiryReadClockUnixNano, AccessScope scope, string? evidenceCursor,
        int evidencePageSize, CancellationToken cancellationToken = default)
        => GetEdgeCoreAsync(edgeId, readClockUnixNano, scope, evidenceCursor, evidencePageSize,
            fromUnixNano, toUnixNano, declaredStateClockUnixNano, expiryReadClockUnixNano,
            cancellationToken);

    private async Task<TopologyEdgeDetail?> GetEdgeCoreAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, decimal? fromUnixNano, decimal? toUnixNano,
        decimal? declaredStateClockUnixNano, decimal? expiryReadClockUnixNano,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeId); ValidateScopeAndPage(scope, evidencePageSize);
        var window = ResolveWindow(readClockUnixNano, fromUnixNano, toUnixNano);
        ValidateDeclaredStateClock(declaredStateClockUnixNano, readClockUnixNano, window);
        var expiryClock = ResolveExpiryClock(readClockUnixNano, expiryReadClockUnixNano);
        var fingerprint = Fingerprint("edge-evidence", scope, edgeId,
            readClockUnixNano.ToString(CultureInfo.InvariantCulture), WindowPart(window),
            ClockPart(declaredStateClockUnixNano));
        var (snapshot, cursor) = await ReadAsync(evidenceCursor, fingerprint,
            EdgeRead(scope, readClockUnixNano, expiryClock, window,
                declaredStateClockUnixNano, edgeId: edgeId), cancellationToken);
        var visible = VisibleActiveEdges(snapshot, scope, readClockUnixNano, window,
            declaredStateClockUnixNano, expiryClock).ToArray();
        if (!visible.Any(candidate => candidate.Id == edgeId && candidate.Provenance == TopologyProvenance.Declared))
            EnsureEdgeDetailReady(snapshot, edgeId, scope, readClockUnixNano, expiryClock, window);
        var edge = visible
            .SingleOrDefault(candidate => candidate.Id == edgeId);
        if (edge is null) return null;
        var evidence = snapshot.Evidence.Where(item => item.EdgeId == edgeId).OrderBy(static item => item.EventTimeUnixNano)
            .ThenBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        var page = Page(evidence, evidencePageSize, cursor?.LastKey, EvidenceKey, snapshot.PublishedSequence, fingerprint);
        return new(edge, page.Items, page.Cursor);
    }

    public async Task<TopologyPathResult> PathAsync(TopologyPathQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        ValidateNode(query.FromNodeId); ValidateNode(query.ToNodeId);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        ValidateDeclaredStateClock(query.DeclaredStateClockUnixNano, query.ReadClockUnixNano, window);
        var expiryClock = ResolveExpiryClock(query.ReadClockUnixNano, query.ExpiryReadClockUnixNano);
        var fingerprint = Fingerprint("path", scope, query.FromNodeId, query.ToNodeId,
            query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture), WindowPart(window),
            ClockPart(query.DeclaredStateClockUnixNano));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint,
            EdgeRead(scope, query.ReadClockUnixNano, expiryClock, window,
                query.DeclaredStateClockUnixNano, TopologyRelation.DependsOn), cancellationToken);
        var edges = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window,
                query.DeclaredStateClockUnixNano, expiryClock)
            .Where(static edge => edge.Relation == TopologyRelation.DependsOn).ToArray();
        var earliestEligibleExpiry = MinExpiry(edges);
        EnsurePathReady(snapshot, scope, query.ReadClockUnixNano, expiryClock, window, edges,
            query.FromNodeId, query.ToNodeId);
        // A missing parent anywhere upstream of the destination can create a
        // shorter route, even when a longer complete route already exists.
        var unresolved = query.FromNodeId == query.ToNodeId ? null
            : UnresolvedForUpstream(snapshot, scope, expiryClock, window,
                edges, [query.ToNodeId]);
        if (unresolved is not null)
            return new(TopologyGraphResultStatus.NotVerified, [], [], null,
                snapshot.PublishedSequence, unresolved)
            { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };
        var path = FindPath(query.FromNodeId, query.ToNodeId, edges);
        if (path.Nodes.Count == 0)
        {
            var partial = HasHiddenIncident(query.FromNodeId, snapshot, scope, query.ReadClockUnixNano,
                    expiryClock, window, query.DeclaredStateClockUnixNano)
                || HasHiddenIncident(query.ToNodeId, snapshot, scope, query.ReadClockUnixNano,
                    expiryClock, window, query.DeclaredStateClockUnixNano);
            return new(partial ? TopologyGraphResultStatus.NotVerified
                    : TopologyGraphResultStatus.Unreachable,
                [], [], null, snapshot.PublishedSequence, partial ? "HiddenBoundary" : null)
            { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };
        }
        // Page complete hops, not independent node/edge arrays. The boundary
        // node is repeated on the next page so every returned edge has both
        // endpoints; the cursor still advances to the last emitted node.
        var start = 0;
        if (cursor is not null)
        {
            start = -1;
            for (var index = 0; index < path.Nodes.Count; index++)
                if (path.Nodes[index] == cursor.Value.LastKey) { start = index; break; }
            if (start < 0 || start >= path.EdgeIds.Count)
                throw new TopologyCursorException("Topology path cursor key is absent from its snapshot.");
        }
        var pageEdges = path.EdgeIds.Skip(start).Take(query.PageSize).ToArray();
        var pageNodes = path.Nodes.Skip(start).Take(pageEdges.Length + 1).ToArray();
        var nextCursor = start + pageEdges.Length < path.EdgeIds.Count
            ? TopologyGraphCursorCodec.Encode(snapshot.PublishedSequence, fingerprint, pageNodes[^1]) : null;
        return new TopologyPathResult(TopologyGraphResultStatus.Found, pageNodes, pageEdges, nextCursor, snapshot.PublishedSequence)
        {
            EarliestEvidenceExpiryUnixNano = MinExpiry(edges.Where(edge => path.EdgeIds.Contains(edge.Id, StringComparer.Ordinal))),
            EarliestEligibleExpiryUnixNano = earliestEligibleExpiry,
        };
    }

    public async Task<TopologyCommonAncestorResult> CommonAncestorAsync(TopologyCommonAncestorQuery query,
        AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(scope);
        if (query.NodeIds.Count is < 2 or > 20) throw new ArgumentOutOfRangeException(nameof(query.NodeIds));
        if (query.NodeIds.Distinct(StringComparer.Ordinal).Count() != query.NodeIds.Count)
            throw new ArgumentException("Duplicate ancestor targets are not allowed.", nameof(query));
        foreach (var nodeId in query.NodeIds) ValidateNode(nodeId);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var expiryClock = ResolveExpiryClock(query.ReadClockUnixNano, query.ExpiryReadClockUnixNano);
        var snapshot = await ReadSnapshotAsync(null,
            EdgeRead(scope, query.ReadClockUnixNano, expiryClock, window,
                relation: TopologyRelation.DependsOn), cancellationToken);
        var edges = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window,
                expiryReadClockUnixNano: expiryClock)
            .Where(static edge => edge.Relation == TopologyRelation.DependsOn).ToArray();
        var earliestEligibleExpiry = MinExpiry(edges);
        EnsureAncestorReady(snapshot, scope, query.ReadClockUnixNano, expiryClock, window, edges, query.NodeIds);
        var unresolvedTarget = UnresolvedForUpstream(snapshot, scope, expiryClock,
            window, edges, query.NodeIds);
        if (unresolvedTarget is not null)
            return new(TopologyGraphResultStatus.NotVerified, null, [], snapshot.PublishedSequence,
                unresolvedTarget)
            { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };
        var targets = query.NodeIds.ToHashSet(StringComparer.Ordinal);
        var matches = new List<(string Node, TopologyPathProof[] Paths)>();
        foreach (var candidate in edges.SelectMany(static edge => new[] { edge.FromNode, edge.ToNode })
            .Distinct(StringComparer.Ordinal).Where(node => !targets.Contains(node)))
        {
            var paths = query.NodeIds.Select(target => FindPath(candidate, target, edges)).ToArray();
            if (paths.All(static path => path.Nodes.Count > 1))
                matches.Add((candidate, paths.Select((path, index) =>
                    new TopologyPathProof(query.NodeIds[index], path.Nodes, path.EdgeIds)).ToArray()));
        }
        var match = matches.OrderBy(static item => item.Paths.Max(path => path.EdgeIds.Count))
            .ThenBy(static item => item.Paths.Sum(path => path.EdgeIds.Count))
            .ThenBy(static item => item.Node, StringComparer.Ordinal).FirstOrDefault();
        if (match.Node is not null)
            return new TopologyCommonAncestorResult(TopologyGraphResultStatus.Found, match.Node,
                match.Paths, snapshot.PublishedSequence)
            {
                EarliestEvidenceExpiryUnixNano = ProofExpiry(edges, match.Paths),
                EarliestEligibleExpiryUnixNano = earliestEligibleExpiry,
            };
        var partial = query.NodeIds.Any(node => HasHiddenIncident(node, snapshot, scope,
            query.ReadClockUnixNano, expiryClock, window));
        return new(partial ? TopologyGraphResultStatus.NotVerified : TopologyGraphResultStatus.Unreachable,
            null, [], snapshot.PublishedSequence, partial ? "HiddenBoundary" : null)
        { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };
    }

    public async Task<TopologyCommonAncestorResult> GroupedCommonAncestorAsync(
        TopologyGroupedAncestorQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        if (query.TargetGroups.Count is < 2 or > 20 || query.TargetGroups.Any(static group => group.Count == 0)
            || query.TargetGroups.Sum(static group => group.Count) > 200)
            throw new ArgumentOutOfRangeException(nameof(query.TargetGroups));
        var groups = query.TargetGroups.Select(group => group.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray()).ToArray();
        foreach (var target in groups.SelectMany(static group => group)) ValidateNode(target);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        ValidateDeclaredStateClock(query.DeclaredStateClockUnixNano, query.ReadClockUnixNano, window);
        var expiryClock = ResolveExpiryClock(query.ReadClockUnixNano, query.ExpiryReadClockUnixNano);
        var snapshot = await ReadSnapshotAsync(null,
            EdgeRead(scope, query.ReadClockUnixNano, expiryClock, window,
                query.DeclaredStateClockUnixNano, TopologyRelation.DependsOn), cancellationToken);
        var edges = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window,
                query.DeclaredStateClockUnixNano, expiryClock)
            .Where(static edge => edge.Relation == TopologyRelation.DependsOn).ToArray();
        var earliestEligibleExpiry = MinExpiry(edges);
        var allTargets = groups.SelectMany(static group => group).Distinct(StringComparer.Ordinal).ToArray();
        EnsureAncestorReady(snapshot, scope, query.ReadClockUnixNano, expiryClock, window, edges, allTargets);
        var unresolved = UnresolvedForUpstream(snapshot, scope, expiryClock, window,
            edges, allTargets);
        if (unresolved is not null)
            return new TopologyCommonAncestorResult(TopologyGraphResultStatus.NotVerified, null,
                [], snapshot.PublishedSequence, unresolved)
            { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };

        var matches = new List<(string Node, TopologyPathProof[] Paths)>();
        foreach (var candidate in edges.SelectMany(static edge => new[] { edge.FromNode, edge.ToNode })
            .Distinct(StringComparer.Ordinal))
        {
            var witnesses = new List<TopologyPathProof>(groups.Length);
            foreach (var group in groups)
            {
                var best = group.Select(target => (Target: target, Path: FindPath(candidate, target, edges)))
                    .Where(static item => item.Path.EdgeIds.Count > 0)
                    .OrderBy(static item => item.Path.EdgeIds.Count)
                    .ThenBy(static item => item.Path.Nodes, OrdinalSequence)
                    .ThenBy(static item => item.Path.EdgeIds, OrdinalSequence)
                    .FirstOrDefault();
                if (best.Target is null) break;
                witnesses.Add(new(best.Target, best.Path.Nodes, best.Path.EdgeIds));
            }
            if (witnesses.Count == groups.Length) matches.Add((candidate, witnesses.ToArray()));
        }
        var match = matches.OrderBy(static item => item.Paths.Max(path => path.EdgeIds.Count))
            .ThenBy(static item => item.Paths.Sum(path => path.EdgeIds.Count))
            .ThenBy(static item => item.Node, StringComparer.Ordinal).FirstOrDefault();
        if (match.Node is not null)
            return new TopologyCommonAncestorResult(TopologyGraphResultStatus.Found, match.Node,
                match.Paths, snapshot.PublishedSequence)
            {
                EarliestEvidenceExpiryUnixNano = ProofExpiry(edges, match.Paths),
                EarliestEligibleExpiryUnixNano = earliestEligibleExpiry,
            };
        var upstream = Reachable(edges, allTargets, reverse: true);
        var partial = upstream.Any(node => HasHiddenIncident(node, snapshot, scope,
            query.ReadClockUnixNano, expiryClock, window, query.DeclaredStateClockUnixNano));
        return new(partial ? TopologyGraphResultStatus.NotVerified : TopologyGraphResultStatus.Unreachable,
            null, [], snapshot.PublishedSequence, partial ? "HiddenBoundary" : null)
        { EarliestEligibleExpiryUnixNano = earliestEligibleExpiry };
    }

    public async Task<TopologyNeighborhoodResult> NeighborhoodAsync(TopologyNeighborhoodQuery query,
        AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize); ValidateNode(query.NodeId);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var expiryClock = ResolveExpiryClock(query.ReadClockUnixNano, query.ExpiryReadClockUnixNano);
        var fingerprint = Fingerprint("neighborhood", scope, query.NodeId,
            query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture), query.Relation?.ToString() ?? "*", WindowPart(window));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint,
            EdgeRead(scope, query.ReadClockUnixNano, expiryClock, window,
                relation: query.Relation, nodeId: query.NodeId), cancellationToken);
        EnsureNeighborhoodReady(snapshot, scope, query.ReadClockUnixNano, expiryClock,
            window, query.NodeId, query.Relation);
        var active = ActiveEdges(snapshot, query.ReadClockUnixNano, window,
                expiryReadClockUnixNano: expiryClock)
            .Where(edge => query.Relation is null || edge.Relation == query.Relation).ToArray();
        var visibleIncident = active.Where(edge => TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup)
            && (edge.FromNode == query.NodeId || edge.ToNode == query.NodeId)).ToArray();
        var neighbors = visibleIncident
            .SelectMany(edge => Neighbors(query.NodeId, edge))
            .OrderBy(static neighbor => neighbor.NodeId, StringComparer.Ordinal)
            .ThenBy(static neighbor => neighbor.EdgeId, StringComparer.Ordinal)
            .ThenBy(static neighbor => neighbor.Direction).ToArray();
        var externalIncident = active.Where(edge => ExternalNeighbor(query.NodeId, edge, scope) is not null).ToArray();
        var externalCount = externalIncident.Select(edge => ExternalNeighbor(query.NodeId, edge, scope))
            .Distinct(StringComparer.Ordinal).Count();
        var unresolved = query.Relation is null or TopologyRelation.DependsOn
            ? UnresolvedForNode(snapshot, scope, expiryClock, window, query.NodeId)
            : null;
        var page = Page(neighbors, query.PageSize, cursor?.LastKey, NeighborKey, snapshot.PublishedSequence, fingerprint);
        return new TopologyNeighborhoodResult(page.Items, unresolved is null ? externalCount : null,
            unresolved, page.Cursor, page.PublishedSequence)
        {
            // A hidden endpoint never appears in the response, but its edge can
            // contribute to the outside count. The final response/cursor gate
            // must expire that aggregate at the same logical deadline.
            EarliestEvidenceExpiryUnixNano = MinExpiry(visibleIncident.Concat(externalIncident)),
        };
    }

    public async Task<TopologyOutsideNeighborCount> CountExternalNeighborsAsync(TopologyNeighborhoodQuery query,
        AccessScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await NeighborhoodAsync(query with { PageSize = 1, Cursor = null }, scope, cancellationToken);
            return new(result.ExternalNeighborCount, result.ExternalNeighborReason);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is TimeoutException or IOException or DbException or HttpRequestException)
        { return new(null, ex is TimeoutException ? "Timeout" : "QueryUnavailable"); }
    }

    private async Task<(TopologyGraphSnapshot Snapshot, TopologyGraphCursor? Cursor)> ReadAsync(string? encodedCursor,
        byte[] fingerprint, TopologyGraphSnapshotReadRequest request, CancellationToken cancellationToken)
    {
        TopologyGraphCursor? cursor = encodedCursor is null ? null : TopologyGraphCursorCodec.Decode(encodedCursor, fingerprint);
        var snapshot = await ReadSnapshotAsync(cursor?.PublishedSequence, request, cancellationToken);
        if (cursor is not null && snapshot.PublishedSequence != cursor.Value.PublishedSequence)
            throw new TopologySnapshotUnavailableException(cursor.Value.PublishedSequence);
        return (snapshot, cursor);
    }

    private Task<TopologyGraphSnapshot> ReadSnapshotAsync(long? publishedSequence,
        TopologyGraphSnapshotReadRequest request, CancellationToken cancellationToken) =>
        _source is ITopologyScopedGraphSnapshotSource scoped
            ? scoped.ReadScopedAsync(publishedSequence, request, cancellationToken)
            : _source.ReadAsync(publishedSequence, cancellationToken);

    private static TopologyGraphSnapshotReadRequest NodeRead(AccessScope scope, decimal asOf,
        string? nodeId = null) =>
        new(scope, asOf, asOf, decimal.Max(0, asOf - TopologyExpiry.NanosecondsPerDay), asOf,
            IncludeNodes: true, IncludeObserved: false, NodeId: nodeId);

    private static TopologyGraphSnapshotReadRequest EdgeRead(AccessScope scope, decimal asOf,
        decimal expiryClock, (decimal From, decimal To) window, decimal? declaredStateClock = null,
        TopologyRelation? relation = null, TopologyProvenance? provenance = null,
        string? nodeId = null, string? edgeId = null) =>
        new(scope, asOf, expiryClock, window.From, window.To, declaredStateClock,
            relation, provenance, IncludeObserved: provenance != TopologyProvenance.Declared
                && (relation is null or TopologyRelation.DependsOn), NodeId: nodeId, EdgeId: edgeId);

    private static IEnumerable<TopologyEdgeProjection> ActiveEdges(TopologyGraphSnapshot snapshot, decimal readClockUnixNano,
        (decimal From, decimal To) window, decimal? declaredStateClockUnixNano = null,
        decimal? expiryReadClockUnixNano = null)
    {
        _ = TopologyExpiry.FromDecimal(readClockUnixNano);
        var expiryClock = ResolveExpiryClock(readClockUnixNano, expiryReadClockUnixNano);
        var active = snapshot.Edges.Where(edge =>
        {
            // Declared history is selected immediately before RCA Window.To;
            // observed event window and logical TTL use the current read clock.
            var edgeClock = edge.Provenance == TopologyProvenance.Declared
                ? declaredStateClockUnixNano ?? readClockUnixNano : expiryClock;
            return !edge.Deleted && edge.FirstSeenUnixNano <= edgeClock
                && (edge.EffectiveExpiry is null || TopologyExpiry.IsReadable(edgeClock, edge.EffectiveExpiry.Value))
                && edge.FirstSeenUnixNano <= readClockUnixNano
                && (edge.Provenance != TopologyProvenance.Observed
                    || edge.LastSeenUnixNano >= window.From && edge.LastSeenUnixNano < window.To);
        });
        return active.GroupBy(static edge => edge.Id, StringComparer.Ordinal).Select(group =>
        {
            if (group.Distinct().Skip(1).Any())
                throw new InvalidDataException($"Conflicting topology edge projection '{group.Key}'.");
            return group.First();
        }).ToArray();
    }

    private static IEnumerable<TopologyEdgeProjection> VisibleActiveEdges(TopologyGraphSnapshot snapshot, AccessScope scope,
        decimal readClockUnixNano, (decimal From, decimal To) window,
        decimal? declaredStateClockUnixNano = null, decimal? expiryReadClockUnixNano = null) =>
        ActiveEdges(snapshot, readClockUnixNano, window, declaredStateClockUnixNano, expiryReadClockUnixNano)
        .Where(edge => TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup));

    private static void EnsureObservedReady(TopologyGraphSnapshot snapshot)
    {
        if (snapshot.ObservedMigrationRequired) throw new TopologyObservedMigrationRequiredException();
    }

    private static bool CandidateActive(TopologyConflictProjection candidate, AccessScope scope, decimal clock,
        (decimal From, decimal To) window) =>
        candidate.NodeId is not null && TopologyIdentity.CanReadOwner(scope, candidate.OwnerGroup)
        && candidate.EventTimeUnixNano >= window.From && candidate.EventTimeUnixNano < window.To
        && clock < candidate.ExpiresUnixNano;

    private static string? UnresolvedForNode(TopologyGraphSnapshot snapshot, AccessScope scope, decimal clock,
        (decimal From, decimal To) window, string nodeId) => snapshot.UnresolvedParents
        .Where(item => item.ChildNodeId == nodeId && TopologyIdentity.CanReadOwner(scope, item.OwnerGroup)
            && item.ChildEventTimeUnixNano >= window.From && item.ChildEventTimeUnixNano < window.To
            && clock < item.ChildExpiryUnixNano)
        .Select(static item => item.Reason).Order(StringComparer.Ordinal).FirstOrDefault();

    private static string? UnresolvedForUpstream(TopologyGraphSnapshot snapshot, AccessScope scope, decimal clock,
        (decimal From, decimal To) window, IReadOnlyList<TopologyEdgeProjection> edges,
        IEnumerable<string> targets)
    {
        var upstream = Reachable(edges, targets, reverse: true);
        return snapshot.UnresolvedParents
            .Where(item => item.ChildNodeId is not null && upstream.Contains(item.ChildNodeId)
                && TopologyIdentity.CanReadOwner(scope, item.OwnerGroup)
                && item.ChildEventTimeUnixNano >= window.From && item.ChildEventTimeUnixNano < window.To
                && clock < item.ChildExpiryUnixNano)
            .Select(static item => item.Reason).Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static IEnumerable<TopologyConflictArc> ActiveConflictArcs(TopologyGraphSnapshot snapshot,
        decimal clock, (decimal From, decimal To) window) => snapshot.ConflictArcs.Where(arc =>
            arc.ChildEventTimeUnixNano >= window.From && arc.ChildEventTimeUnixNano < window.To
            && clock < arc.EffectiveExpiryUnixNano);

    private static TopologyEdgeProjection[] ConflictedActiveEdges(TopologyGraphSnapshot snapshot, decimal asOfClock,
        decimal expiryClock, (decimal From, decimal To) window) => ActiveEdges(
        new TopologyGraphSnapshot(snapshot.PublishedSequence, snapshot.ConflictedEdges), asOfClock,
        window, expiryReadClockUnixNano: expiryClock).ToArray();

    private static void EnsureEdgeListReady(TopologyGraphSnapshot snapshot, AccessScope scope, decimal asOfClock,
        decimal expiryClock,
        (decimal From, decimal To) window, TopologyRelation? relation, TopologyProvenance? provenance)
    {
        if (provenance == TopologyProvenance.Declared || relation is not null and not TopologyRelation.DependsOn) return;
        EnsureObservedReady(snapshot);
        if (snapshot.ConflictCandidates.Any(candidate => CandidateActive(candidate, scope, expiryClock, window))
            || ActiveConflictArcs(snapshot, expiryClock, window).Any(arc =>
                TopologyIdentity.CanReadEdge(scope, arc.FromOwnerGroup, arc.ToOwnerGroup))
            || ConflictedActiveEdges(snapshot, asOfClock, expiryClock, window).Any(edge =>
                TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup)))
            throw new TopologyConflictException();
    }

    private static void EnsureEdgeDetailReady(TopologyGraphSnapshot snapshot, string edgeId,
        AccessScope scope, decimal asOfClock, decimal expiryClock, (decimal From, decimal To) window)
    {
        EnsureObservedReady(snapshot);
        if (ConflictedActiveEdges(snapshot, asOfClock, expiryClock, window).Any(edge => edge.Id == edgeId
            && TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup)))
            throw new TopologyConflictException();
    }

    private static void EnsureNeighborhoodReady(TopologyGraphSnapshot snapshot, AccessScope scope,
        decimal asOfClock, decimal expiryClock,
        (decimal From, decimal To) window, string nodeId, TopologyRelation? relation)
    {
        if (relation is not null and not TopologyRelation.DependsOn) return;
        EnsureObservedReady(snapshot);
        if (snapshot.ConflictCandidates.Any(candidate => candidate.NodeId == nodeId
                && CandidateActive(candidate, scope, expiryClock, window))
            || ActiveConflictArcs(snapshot, expiryClock, window).Any(arc =>
                (arc.FromNode == nodeId && TopologyIdentity.CanReadOwner(scope, arc.FromOwnerGroup))
                || (arc.ToNode == nodeId && TopologyIdentity.CanReadOwner(scope, arc.ToOwnerGroup)))
            || ConflictedActiveEdges(snapshot, asOfClock, expiryClock, window).Any(edge =>
                (edge.FromNode == nodeId && TopologyIdentity.CanReadOwner(scope, edge.FromOwnerGroup))
                || (edge.ToNode == nodeId && TopologyIdentity.CanReadOwner(scope, edge.ToOwnerGroup))))
            throw new TopologyConflictException();
    }

    private static HashSet<string> Reachable(IReadOnlyList<TopologyEdgeProjection> edges,
        IEnumerable<string> endpoints, bool reverse)
    {
        var reachable = new HashSet<string>(endpoints, StringComparer.Ordinal);
        var queue = new Queue<string>(reachable);
        while (queue.Count != 0)
        {
            var node = queue.Dequeue();
            foreach (var edge in edges.Where(edge => reverse ? edge.ToNode == node || !edge.Directed && edge.FromNode == node
                : edge.FromNode == node || !edge.Directed && edge.ToNode == node))
            {
                var other = reverse ? edge.FromNode == node && !edge.Directed ? edge.ToNode : edge.FromNode
                    : edge.FromNode == node ? edge.ToNode : edge.FromNode;
                if (reachable.Add(other)) queue.Enqueue(other);
            }
        }
        return reachable;
    }

    private static void EnsurePathReady(TopologyGraphSnapshot snapshot, AccessScope scope, decimal asOfClock,
        decimal expiryClock,
        (decimal From, decimal To) window, IReadOnlyList<TopologyEdgeProjection> edges,
        string from, string to)
    {
        EnsureObservedReady(snapshot);
        var conflicted = ActiveConflictArcs(snapshot, expiryClock, window)
            .Where(arc => TopologyIdentity.CanReadEdge(scope, arc.FromOwnerGroup, arc.ToOwnerGroup))
            .Select(static arc => (From: arc.FromNode, To: arc.ToNode))
            .Concat(ConflictedActiveEdges(snapshot, asOfClock, expiryClock, window)
                .Where(edge => edge.Relation == TopologyRelation.DependsOn
                    && TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup))
                .Select(static edge => (From: edge.FromNode, To: edge.ToNode))).ToArray();
        var possible = edges.Select(static edge => (From: edge.FromNode, To: edge.ToNode))
            .Concat(conflicted).ToArray();
        var forward = ReachableDirected(possible, from, reverse: false);
        var backward = ReachableDirected(possible, to, reverse: true);
        if (snapshot.ConflictCandidates.Any(candidate => CandidateActive(candidate, scope, expiryClock, window)
                && (candidate.NodeId == from || candidate.NodeId == to))
            || conflicted.Any(arc => forward.Contains(arc.From) && backward.Contains(arc.To)))
            throw new TopologyConflictException();
    }

    private static HashSet<string> ReachableDirected(IReadOnlyList<(string From, string To)> arcs,
        string start, bool reverse)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { start };
        var pending = new Queue<string>(); pending.Enqueue(start);
        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            foreach (var arc in arcs.Where(arc => reverse ? arc.To == current : arc.From == current))
            {
                var next = reverse ? arc.From : arc.To;
                if (reached.Add(next)) pending.Enqueue(next);
            }
        }
        return reached;
    }

    private static void EnsureAncestorReady(TopologyGraphSnapshot snapshot, AccessScope scope,
        decimal asOfClock, decimal expiryClock,
        (decimal From, decimal To) window, IReadOnlyList<TopologyEdgeProjection> edges,
        IReadOnlyList<string> targets)
    {
        EnsureObservedReady(snapshot);
        var conflicted = ActiveConflictArcs(snapshot, expiryClock, window)
            .Where(arc => TopologyIdentity.CanReadEdge(scope, arc.FromOwnerGroup, arc.ToOwnerGroup))
            .Select(static arc => (From: arc.FromNode, To: arc.ToNode))
            .Concat(ConflictedActiveEdges(snapshot, asOfClock, expiryClock, window)
                .Where(edge => edge.Relation == TopologyRelation.DependsOn
                    && TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup))
                .Select(static edge => (From: edge.FromNode, To: edge.ToNode))).ToArray();
        var possible = edges.Select(static edge => (From: edge.FromNode, To: edge.ToNode))
            .Concat(conflicted).ToArray();
        var upstream = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
            upstream.UnionWith(ReachableDirected(possible, target, reverse: true));
        if (snapshot.ConflictCandidates.Any(candidate => CandidateActive(candidate, scope, expiryClock, window)
                && upstream.Contains(candidate.NodeId!))
            || conflicted.Any(arc => upstream.Contains(arc.To)))
            throw new TopologyConflictException();
    }

    private sealed record TraversalPath(IReadOnlyList<string> Nodes, IReadOnlyList<string> EdgeIds);

    private static TraversalPath FindPath(string fromNodeId, string toNodeId, IReadOnlyList<TopologyEdgeProjection> edges)
    {
        if (fromNodeId == toNodeId) return new([fromNodeId], []);
        var previous = new Dictionary<string, (string Node, string Edge)?>(StringComparer.Ordinal) { [fromNodeId] = null };
        var pending = new Queue<string>(); pending.Enqueue(fromNodeId);
        while (pending.Count != 0)
        {
            var current = pending.Dequeue();
            foreach (var (next, edgeId) in Outgoing(current, edges))
            {
                if (!previous.TryAdd(next, (current, edgeId))) continue;
                if (next != toNodeId) { pending.Enqueue(next); continue; }
                var nodes = new List<string>(); var edgeIds = new List<string>();
                for (var node = next; ;)
                {
                    nodes.Add(node); var prior = previous[node]; if (prior is null) break;
                    edgeIds.Add(prior.Value.Edge); node = prior.Value.Node;
                }
                nodes.Reverse(); edgeIds.Reverse(); return new(nodes, edgeIds);
            }
        }
        return new([], []);
    }

    private static IEnumerable<(string Node, string EdgeId)> Outgoing(string node,
        IReadOnlyList<TopologyEdgeProjection> edges) => edges
        .Where(edge => edge.FromNode == node || !edge.Directed && edge.ToNode == node)
        .Select(edge => (Node: edge.FromNode == node ? edge.ToNode : edge.FromNode, EdgeId: edge.Id))
        .OrderBy(static next => next.Node, StringComparer.Ordinal).ThenBy(static next => next.EdgeId, StringComparer.Ordinal)
        .DistinctBy(static next => next.Node, StringComparer.Ordinal);

    private static bool HasHiddenIncident(string node, TopologyGraphSnapshot snapshot, AccessScope scope,
        decimal readClockUnixNano, decimal expiryReadClockUnixNano,
        (decimal From, decimal To) window, decimal? declaredStateClockUnixNano = null) =>
        ActiveEdges(snapshot, readClockUnixNano, window, declaredStateClockUnixNano,
            expiryReadClockUnixNano)
        .Any(edge => ExternalNeighbor(node, edge, scope) is not null);

    private static IEnumerable<TopologyNeighbor> Neighbors(string node, TopologyEdgeProjection edge)
    {
        if (edge.FromNode == node) yield return new(edge.ToNode, edge.Id, edge.Relation, edge.Provenance, TopologyNeighborDirection.Outgoing);
        if (edge.ToNode == node) yield return new(edge.FromNode, edge.Id, edge.Relation, edge.Provenance, TopologyNeighborDirection.Incoming);
    }

    private static string? ExternalNeighbor(string node, TopologyEdgeProjection edge, AccessScope scope)
    {
        if (edge.FromNode == node && TopologyIdentity.CanReadOwner(scope, edge.FromOwnerGroup)
            && !TopologyIdentity.CanReadOwner(scope, edge.ToOwnerGroup)) return edge.ToNode;
        if (edge.ToNode == node && TopologyIdentity.CanReadOwner(scope, edge.ToOwnerGroup)
            && !TopologyIdentity.CanReadOwner(scope, edge.FromOwnerGroup)) return edge.FromNode;
        return null;
    }

    private static TopologyGraphPage<T> Page<T>(IReadOnlyList<T> rows, int pageSize, string? after, Func<T, string> key,
        long publishedSequence, byte[] fingerprint)
    {
        var start = 0;
        if (after is not null)
        {
            start = -1;
            for (var i = 0; i < rows.Count; i++) if (key(rows[i]) == after) { start = i + 1; break; }
            if (start < 0) throw new TopologyCursorException("Topology cursor key is absent from its snapshot.");
        }
        var items = rows.Skip(start).Take(pageSize).ToArray();
        var cursor = start + items.Length < rows.Count && items.Length != 0
            ? TopologyGraphCursorCodec.Encode(publishedSequence, fingerprint, key(items[^1])) : null;
        return new(items, cursor, publishedSequence);
    }

    private static string NeighborKey(TopologyNeighbor neighbor) => string.Create(CultureInfo.InvariantCulture,
        $"{neighbor.NodeId}\0{neighbor.EdgeId}\0{(int)neighbor.Direction}");

    private static string EvidenceKey(TopologyEvidenceReference evidence) => string.Create(CultureInfo.InvariantCulture,
        $"{evidence.EventTimeUnixNano}\0{evidence.Id}");

    private static byte[] Fingerprint(string kind, AccessScope scope, params string[] parts)
    {
        var scopeKey = scope.IsUnrestricted ? "*" : string.Join('\u001f', scope.OwnerGroups.Order(StringComparer.Ordinal));
        return SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', new[] { kind, scope.Subject, scopeKey }.Concat(parts))))[..16];
    }

    private static (decimal From, decimal To) ResolveWindow(decimal readClockUnixNano, decimal? from, decimal? to)
    {
        _ = TopologyExpiry.FromDecimal(readClockUnixNano);
        if (from.HasValue != to.HasValue)
            throw new ArgumentException("Topology observed window requires both from and to.");
        var resolvedFrom = from ?? decimal.Max(0, readClockUnixNano - TopologyExpiry.NanosecondsPerDay);
        var resolvedTo = to ?? readClockUnixNano;
        _ = TopologyExpiry.FromDecimal(resolvedFrom); _ = TopologyExpiry.FromDecimal(resolvedTo);
        if (resolvedFrom >= resolvedTo || resolvedTo > readClockUnixNano)
            throw new ArgumentOutOfRangeException(nameof(to), "Topology observed window must satisfy from < to <= asOf.");
        return (resolvedFrom, resolvedTo);
    }

    private static decimal ResolveExpiryClock(decimal asOfUnixNano, decimal? expiryReadClockUnixNano)
    {
        var clock = expiryReadClockUnixNano ?? asOfUnixNano;
        _ = TopologyExpiry.FromDecimal(clock);
        if (clock < asOfUnixNano)
            throw new ArgumentOutOfRangeException(nameof(expiryReadClockUnixNano),
                "Observed expiry clock cannot precede historical as-of.");
        return clock;
    }

    private static string WindowPart((decimal From, decimal To) window) => string.Create(CultureInfo.InvariantCulture,
        $"{window.From}:{window.To}");

    private static string ClockPart(decimal? clock) =>
        clock?.ToString(CultureInfo.InvariantCulture) ?? "*";

    private static void ValidateDeclaredStateClock(decimal? stateClock, decimal readClock,
        (decimal From, decimal To) window)
    {
        if (stateClock is null) return;
        _ = TopologyExpiry.FromDecimal(stateClock.Value);
        if (stateClock.Value >= window.To || stateClock.Value > readClock)
            throw new ArgumentOutOfRangeException(nameof(stateClock),
                "Declared RCA state must precede the observed window end.");
    }

    private static decimal? MinExpiry(IEnumerable<TopologyEdgeProjection> edges)
    {
        decimal? minimum = null;
        // Declared EffectiveExpiry is the history interval end, not a captured
        // observed-evidence TTL. B05 cursors must not expire on that value.
        foreach (var expiry in edges.Where(static edge => edge.Provenance == TopologyProvenance.Observed)
            .Select(static edge => edge.EffectiveExpiry).Where(static expiry => expiry.HasValue))
            minimum = minimum is null ? expiry : decimal.Min(minimum.Value, expiry!.Value);
        return minimum;
    }

    private static decimal? ProofExpiry(IEnumerable<TopologyEdgeProjection> edges,
        IEnumerable<TopologyPathProof> paths)
    {
        var chosenIds = paths.SelectMany(static path => path.EdgeIds)
            .ToHashSet(StringComparer.Ordinal);
        return MinExpiry(edges.Where(edge => chosenIds.Contains(edge.Id)));
    }

    private static void ValidateScopeAndPage(AccessScope scope, int pageSize)
    { ArgumentNullException.ThrowIfNull(scope); if (pageSize is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize)); }
    private static void ValidateNode(string nodeId)
    { ArgumentException.ThrowIfNullOrWhiteSpace(nodeId); _ = TopologyIdentity.Kind(nodeId); }
}
