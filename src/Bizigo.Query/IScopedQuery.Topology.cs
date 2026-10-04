using Bizigo.Contracts;

namespace Bizigo.Query;

public partial interface IScopedQuery
{
    Task<TopologyGraphPage<TopologyNodeProjection>> SearchTopologyNodesAsync(TopologyNodeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Topology graph is not configured.");
    Task<TopologyNodeProjection?> GetTopologyNodeAsync(string nodeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Topology graph is not configured.");
    Task<TopologyGraphPage<TopologyEdgeProjection>> SearchTopologyEdgesAsync(TopologyEdgeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Topology graph is not configured.");
    Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Topology graph is not configured.");
    Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph evidence paging is not configured.");

    Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph scoped evidence window is not configured.");

    Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph scoped evidence paging is not configured.");

    Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, decimal declaredStateClockUnixNano,
        AccessScope scope, string? evidenceCursor, int evidencePageSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph RCA declared-state detail is not configured.");

    Task<IReadOnlyList<TopologySourceNode>> ResolveTopologySourceNodesAsync(
        IReadOnlyList<string> sourceIds,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph is not configured.");

    /// <summary>Internal, fail-closed mapped-source stream. Every source needs a terminal chunk.</summary>
    Task<TopologySourceTargetsPage> ResolveTopologySourceTargetsPageAsync(
        IReadOnlyList<string> sourceIds, decimal asOfUnixNano, AccessScope scope,
        int pageSize = 100, string? cursor = null, CancellationToken cancellationToken = default);

    Task<TopologyPathResult> GetTopologyPathAsync(
        TopologyPathQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph is not configured.");

    Task<TopologyCommonAncestorResult> GetTopologyCommonAncestorAsync(
        TopologyCommonAncestorQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph is not configured.");

    Task<TopologyCommonAncestorResult> GetTopologyGroupedCommonAncestorAsync(
        TopologyGroupedAncestorQuery query, AccessScope scope,
        CancellationToken cancellationToken = default);

    Task<TopologyNeighborhoodResult> GetTopologyNeighborhoodAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph is not configured.");

    Task<TopologyOutsideNeighborCount> CountExternalTopologyNeighborsAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Topology graph is not configured.");
}
