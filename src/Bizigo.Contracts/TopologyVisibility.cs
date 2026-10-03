namespace Bizigo.Contracts;

/// <summary>A node snapshot at the same event-time/publication revision as its edges.</summary>
public sealed record TopologyVisibilityNode(string Id, string OwnerGroup, string DisplayName);

/// <summary>
/// The endpoint owners are physical projection data. Visibility must not be
/// reconstructed from a payload claim or from today's mutable node owner.
/// </summary>
public sealed record TopologyVisibilityEdge(string Id, string FromNodeId, string ToNodeId,
    string FromOwnerGroup, string ToOwnerGroup, string? TraceId = null, string? SpanId = null);

public sealed record VisibleTopologyNode(string Id, string DisplayName, int ExternalNeighborCount);

public sealed record TopologyVisibilityResult(IReadOnlyList<VisibleTopologyNode> Nodes,
    IReadOnlyList<TopologyVisibilityEdge> Edges);

/// <summary>
/// Applies the cross-owner graph boundary. A hidden endpoint contributes only
/// one aggregate neighbour fact to an authorized endpoint; its identity,
/// attributes and trace proof never leave this boundary. Full edge content is
/// visible only when both physical endpoint-owner snapshots are authorized.
/// </summary>
public static class TopologyVisibility
{
    public static TopologyVisibilityResult Project(AccessScope scope,
        IReadOnlyList<TopologyVisibilityNode> nodes, IReadOnlyList<TopologyVisibilityEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        bool CanRead(string owner) => owner == OwnerGroups.Unassigned
            ? scope.IsUnrestricted
            : scope.Allows(owner);

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var visible = nodes.Where(n => CanRead(n.OwnerGroup)).ToArray();
        var external = visible.ToDictionary(n => n.Id,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var readableEdges = new List<TopologyVisibilityEdge>();

        foreach (var edge in edges)
        {
            if (!byId.TryGetValue(edge.FromNodeId, out var from)
                || !byId.TryGetValue(edge.ToNodeId, out var to)
                || from.OwnerGroup != edge.FromOwnerGroup
                || to.OwnerGroup != edge.ToOwnerGroup)
                throw new InvalidDataException("Topology edge endpoint ownership snapshot is inconsistent.");

            var canReadFrom = CanRead(edge.FromOwnerGroup);
            var canReadTo = CanRead(edge.ToOwnerGroup);
            if (canReadFrom && canReadTo)
            {
                readableEdges.Add(edge);
                continue;
            }

            if (canReadFrom && external.TryGetValue(edge.FromNodeId, out var fromExternal))
                fromExternal.Add(edge.ToNodeId);
            if (canReadTo && external.TryGetValue(edge.ToNodeId, out var toExternal))
                toExternal.Add(edge.FromNodeId);
        }

        return new(
            visible.Select(n => new VisibleTopologyNode(n.Id, n.DisplayName, external[n.Id].Count)).ToArray(),
            readableEdges);
    }
}
