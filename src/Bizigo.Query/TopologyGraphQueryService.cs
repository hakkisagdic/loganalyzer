using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;

namespace Bizigo.Query;

/// <summary>Snapshot-bound, scope-required, deterministic topology traversals.</summary>
public sealed class TopologyGraphQueryService(ITopologyGraphSnapshotSource source)
{
    private const int MaxPageSize = 200;
    private readonly ITopologyGraphSnapshotSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<TopologyGraphPage<TopologyNodeProjection>> SearchNodesAsync(TopologyNodeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        var fingerprint = Fingerprint("nodes", scope, query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture),
            query.Kind?.ToString() ?? "*");
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint, cancellationToken);
        var rows = snapshot.Nodes.Where(node => scope.Allows(node.OwnerGroup) && node.Enabled && !node.Deleted
                && node.ValidFromUnixNano <= query.ReadClockUnixNano
                && (node.ValidToUnixNano is null || query.ReadClockUnixNano < node.ValidToUnixNano))
            .Where(node => query.Kind is null || node.Kind == query.Kind).OrderBy(static node => node.Id, StringComparer.Ordinal).ToArray();
        return Page(rows, query.PageSize, cursor?.LastKey, static node => node.Id, snapshot.PublishedSequence, fingerprint);
    }

    public async Task<TopologyNodeProjection?> GetNodeAsync(string nodeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ValidateNode(nodeId); ArgumentNullException.ThrowIfNull(scope); _ = TopologyExpiry.FromDecimal(readClockUnixNano);
        var snapshot = await _source.ReadAsync(null, cancellationToken);
        return snapshot.Nodes.SingleOrDefault(node => node.Id == nodeId && scope.Allows(node.OwnerGroup) && node.Enabled && !node.Deleted
            && node.ValidFromUnixNano <= readClockUnixNano && (node.ValidToUnixNano is null || readClockUnixNano < node.ValidToUnixNano));
    }

    public async Task<TopologyGraphPage<TopologyEdgeProjection>> SearchEdgesAsync(TopologyEdgeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var fingerprint = Fingerprint("edges", scope, query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture),
            query.Relation?.ToString() ?? "*", query.Provenance?.ToString() ?? "*", WindowPart(window));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint, cancellationToken);
        var rows = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window)
            .Where(edge => query.Relation is null || edge.Relation == query.Relation)
            .Where(edge => query.Provenance is null || edge.Provenance == query.Provenance)
            .OrderBy(static edge => edge.Id, StringComparer.Ordinal).ToArray();
        var page = Page(rows, query.PageSize, cursor?.LastKey, static edge => edge.Id, snapshot.PublishedSequence, fingerprint);
        return page with { EarliestEvidenceExpiryUnixNano = MinExpiry(page.Items) };
    }

    public async Task<TopologyEdgeDetail?> GetEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeId); ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await _source.ReadAsync(null, cancellationToken);
        var edge = VisibleActiveEdges(snapshot, scope, readClockUnixNano, ResolveWindow(readClockUnixNano, null, null))
            .SingleOrDefault(candidate => candidate.Id == edgeId);
        if (edge is null) return null;
        var evidence = snapshot.Evidence.Where(item => item.EdgeId == edgeId).OrderBy(static item => item.EventTimeUnixNano)
            .ThenBy(static item => item.Id, StringComparer.Ordinal).Take(200).ToArray();
        return new(edge, evidence, null);
    }

    public async Task<TopologyPathResult> PathAsync(TopologyPathQuery query, AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize);
        ValidateNode(query.FromNodeId); ValidateNode(query.ToNodeId);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var fingerprint = Fingerprint("path", scope, query.FromNodeId, query.ToNodeId,
            query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture), WindowPart(window));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint, cancellationToken);
        var edges = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window)
            .Where(static edge => edge.Relation == TopologyRelation.DependsOn).ToArray();
        var path = FindPath(query.FromNodeId, query.ToNodeId, edges);
        if (path.Nodes.Count == 0)
        {
            var partial = HasHiddenIncident(query.FromNodeId, snapshot, scope, query.ReadClockUnixNano, window)
                || HasHiddenIncident(query.ToNodeId, snapshot, scope, query.ReadClockUnixNano, window);
            return new(partial ? TopologyGraphResultStatus.NotVerified : TopologyGraphResultStatus.Unreachable,
                [], [], null, snapshot.PublishedSequence, partial ? "HiddenBoundary" : null);
        }
        var page = Page(path.Nodes, query.PageSize, cursor?.LastKey, static node => node,
            snapshot.PublishedSequence, fingerprint);
        return new TopologyPathResult(TopologyGraphResultStatus.Found, page.Items, path.EdgeIds, page.Cursor, page.PublishedSequence)
        {
            EarliestEvidenceExpiryUnixNano = MinExpiry(edges.Where(edge => path.EdgeIds.Contains(edge.Id, StringComparer.Ordinal))),
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
        var snapshot = await _source.ReadAsync(null, cancellationToken);
        var edges = VisibleActiveEdges(snapshot, scope, query.ReadClockUnixNano, window)
            .Where(static edge => edge.Relation == TopologyRelation.DependsOn).ToArray();
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
            return new(TopologyGraphResultStatus.Found, match.Node, match.Paths, snapshot.PublishedSequence);
        var partial = query.NodeIds.Any(node => HasHiddenIncident(node, snapshot, scope, query.ReadClockUnixNano, window));
        return new(partial ? TopologyGraphResultStatus.NotVerified : TopologyGraphResultStatus.Unreachable,
            null, [], snapshot.PublishedSequence, partial ? "HiddenBoundary" : null);
    }

    public async Task<TopologyNeighborhoodResult> NeighborhoodAsync(TopologyNeighborhoodQuery query,
        AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query); ValidateScopeAndPage(scope, query.PageSize); ValidateNode(query.NodeId);
        var window = ResolveWindow(query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano);
        var fingerprint = Fingerprint("neighborhood", scope, query.NodeId,
            query.ReadClockUnixNano.ToString(CultureInfo.InvariantCulture), query.Relation?.ToString() ?? "*", WindowPart(window));
        var (snapshot, cursor) = await ReadAsync(query.Cursor, fingerprint, cancellationToken);
        var active = ActiveEdges(snapshot, query.ReadClockUnixNano, window)
            .Where(edge => query.Relation is null || edge.Relation == query.Relation).ToArray();
        var neighbors = active.Where(edge => TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup))
            .SelectMany(edge => Neighbors(query.NodeId, edge))
            .OrderBy(static neighbor => neighbor.NodeId, StringComparer.Ordinal)
            .ThenBy(static neighbor => neighbor.EdgeId, StringComparer.Ordinal)
            .ThenBy(static neighbor => neighbor.Direction).ToArray();
        var externalCount = active.Select(edge => ExternalNeighbor(query.NodeId, edge, scope))
            .Where(static node => node is not null).Distinct(StringComparer.Ordinal).Count();
        var page = Page(neighbors, query.PageSize, cursor?.LastKey, NeighborKey, snapshot.PublishedSequence, fingerprint);
        return new TopologyNeighborhoodResult(page.Items, externalCount, null, page.Cursor, page.PublishedSequence)
        {
            EarliestEvidenceExpiryUnixNano = MinExpiry(active.Where(edge => page.Items.Any(item => item.EdgeId == edge.Id))),
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
        catch (Exception ex) when (ex is TimeoutException or IOException)
        { return new(null, ex is TimeoutException ? "Timeout" : "QueryUnavailable"); }
    }

    private async Task<(TopologyGraphSnapshot Snapshot, TopologyGraphCursor? Cursor)> ReadAsync(string? encodedCursor,
        byte[] fingerprint, CancellationToken cancellationToken)
    {
        TopologyGraphCursor? cursor = encodedCursor is null ? null : TopologyGraphCursorCodec.Decode(encodedCursor, fingerprint);
        var snapshot = await _source.ReadAsync(cursor?.PublishedSequence, cancellationToken);
        if (cursor is not null && snapshot.PublishedSequence != cursor.Value.PublishedSequence)
            throw new TopologySnapshotUnavailableException(cursor.Value.PublishedSequence);
        return (snapshot, cursor);
    }

    private static IEnumerable<TopologyEdgeProjection> ActiveEdges(TopologyGraphSnapshot snapshot, decimal readClockUnixNano,
        (decimal From, decimal To) window)
    {
        _ = TopologyExpiry.FromDecimal(readClockUnixNano);
        return snapshot.Edges.Where(edge => !edge.Deleted && edge.FirstSeenUnixNano <= readClockUnixNano
            && (edge.EffectiveExpiry is null || TopologyExpiry.IsReadable(readClockUnixNano, edge.EffectiveExpiry.Value))
            && (edge.Provenance != TopologyProvenance.Observed
                || edge.LastSeenUnixNano >= window.From && edge.LastSeenUnixNano < window.To));
    }

    private static IEnumerable<TopologyEdgeProjection> VisibleActiveEdges(TopologyGraphSnapshot snapshot, AccessScope scope,
        decimal readClockUnixNano, (decimal From, decimal To) window) => ActiveEdges(snapshot, readClockUnixNano, window)
        .Where(edge => TopologyIdentity.CanReadEdge(scope, edge.FromOwnerGroup, edge.ToOwnerGroup));

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
        decimal readClockUnixNano, (decimal From, decimal To) window) => ActiveEdges(snapshot, readClockUnixNano, window)
        .Any(edge => ExternalNeighbor(node, edge, scope) is not null);

    private static IEnumerable<TopologyNeighbor> Neighbors(string node, TopologyEdgeProjection edge)
    {
        if (edge.FromNode == node) yield return new(edge.ToNode, edge.Id, edge.Relation, edge.Provenance, TopologyNeighborDirection.Outgoing);
        if (edge.ToNode == node) yield return new(edge.FromNode, edge.Id, edge.Relation, edge.Provenance, TopologyNeighborDirection.Incoming);
    }

    private static string? ExternalNeighbor(string node, TopologyEdgeProjection edge, AccessScope scope)
    {
        if (edge.FromNode == node && scope.Allows(edge.FromOwnerGroup) && !scope.Allows(edge.ToOwnerGroup)) return edge.ToNode;
        if (edge.ToNode == node && scope.Allows(edge.ToOwnerGroup) && !scope.Allows(edge.FromOwnerGroup)) return edge.FromNode;
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

    private static string WindowPart((decimal From, decimal To) window) => string.Create(CultureInfo.InvariantCulture,
        $"{window.From}:{window.To}");

    private static decimal? MinExpiry(IEnumerable<TopologyEdgeProjection> edges)
    {
        decimal? minimum = null;
        foreach (var expiry in edges.Select(static edge => edge.EffectiveExpiry).Where(static expiry => expiry.HasValue))
            minimum = minimum is null ? expiry : decimal.Min(minimum.Value, expiry!.Value);
        return minimum;
    }

    private static void ValidateScopeAndPage(AccessScope scope, int pageSize)
    { ArgumentNullException.ThrowIfNull(scope); if (pageSize is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize)); }
    private static void ValidateNode(string nodeId)
    { ArgumentException.ThrowIfNullOrWhiteSpace(nodeId); _ = TopologyIdentity.Kind(nodeId); }
}
