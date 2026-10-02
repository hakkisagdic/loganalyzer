using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

public sealed partial class ScopedQuery
{
    private TopologyGraphQueryService Topology => topology
        ?? throw new InvalidOperationException("Topology graph data plane is not configured.");

    public Task<TopologyGraphPage<TopologyNodeProjection>> SearchTopologyNodesAsync(TopologyNodeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => Topology.SearchNodesAsync(query, scope, cancellationToken);
    public Task<TopologyNodeProjection?> GetTopologyNodeAsync(string nodeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => Topology.GetNodeAsync(nodeId, readClockUnixNano, scope, cancellationToken);
    public Task<TopologyGraphPage<TopologyEdgeProjection>> SearchTopologyEdgesAsync(TopologyEdgeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => Topology.SearchEdgesAsync(query, scope, cancellationToken);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => Topology.GetEdgeAsync(edgeId, readClockUnixNano, scope, cancellationToken);

    public async Task<IReadOnlyList<TopologySourceNode>> ResolveTopologySourceNodesAsync(
        IReadOnlyList<string> sourceIds,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(scope);
        if (sourceIds.Count > 1000 || sourceIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid topology source lookup.", nameof(sourceIds));
        var wanted = sourceIds.Distinct(StringComparer.Ordinal).ToArray();
        var rows = await controlPlane.TopologyNodes.AsNoTracking()
            .Where(node => node.SourceId != null && wanted.Contains(node.SourceId) && node.Enabled && !node.Deleted)
            .ToArrayAsync(cancellationToken);
        return rows.Where(node => scope.Allows(node.OwnerGroup))
            .OrderBy(node => node.SourceId, StringComparer.Ordinal)
            .Select(node => new TopologySourceNode(node.SourceId!, node.Id, node.OwnerGroup)).ToArray();
    }

    public Task<TopologyPathResult> GetTopologyPathAsync(
        TopologyPathQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        Topology.PathAsync(query, scope, cancellationToken);

    public Task<TopologyCommonAncestorResult> GetTopologyCommonAncestorAsync(
        TopologyCommonAncestorQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        Topology.CommonAncestorAsync(query, scope, cancellationToken);

    public Task<TopologyNeighborhoodResult> GetTopologyNeighborhoodAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        Topology.NeighborhoodAsync(query, scope, cancellationToken);

    public Task<TopologyOutsideNeighborCount> CountExternalTopologyNeighborsAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        Topology.CountExternalNeighborsAsync(query, scope, cancellationToken);
}
