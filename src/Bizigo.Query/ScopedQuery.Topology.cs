using System.Diagnostics;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

public sealed partial class ScopedQuery
{
    private TopologyGraphQueryService Topology => topology
        ?? throw new InvalidOperationException("Topology graph data plane is not configured.");

    public Task<TopologyGraphPage<TopologyNodeProjection>> SearchTopologyNodesAsync(TopologyNodeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => AuditedTopologyAsync("nodes.list", scope,
        Summary(query.ReadClockUnixNano, query.PageSize, query.Cursor is not null),
        () => Topology.SearchNodesAsync(query, scope, cancellationToken), result => result.Items.Count);
    public Task<TopologyNodeProjection?> GetTopologyNodeAsync(string nodeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => AuditedTopologyAsync("nodes.detail", scope,
        Summary(readClockUnixNano, 1, false),
        () => Topology.GetNodeAsync(nodeId, readClockUnixNano, scope, cancellationToken), result => result is null ? 0 : 1);
    public Task<TopologyGraphPage<TopologyEdgeProjection>> SearchTopologyEdgesAsync(TopologyEdgeQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => AuditedTopologyAsync("edges.list", scope,
        Summary(query.ReadClockUnixNano, query.PageSize, query.Cursor is not null),
        () => Topology.SearchEdgesAsync(query, scope, cancellationToken), result => result.Items.Count);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => AuditedTopologyAsync("edges.detail", scope,
        Summary(readClockUnixNano, 1, false),
        () => Topology.GetEdgeAsync(edgeId, readClockUnixNano, scope, cancellationToken), result => result is null ? 0 : 1);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("edges.detail", scope,
            Summary(readClockUnixNano, evidencePageSize, evidenceCursor is not null),
            () => Topology.GetEdgeAsync(edgeId, readClockUnixNano, scope, evidenceCursor, evidencePageSize, cancellationToken),
            result => result?.Evidence.Count ?? 0);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => AuditedTopologyAsync("edges.detail", scope,
            Summary(readClockUnixNano, 1, false),
            () => Topology.GetEdgeAsync(edgeId, readClockUnixNano, fromUnixNano, toUnixNano,
                scope, cancellationToken), result => result?.Evidence.Count ?? 0);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("edges.detail", scope,
            Summary(readClockUnixNano, evidencePageSize, evidenceCursor is not null),
            () => Topology.GetEdgeAsync(edgeId, readClockUnixNano, fromUnixNano, toUnixNano,
                scope, evidenceCursor, evidencePageSize, cancellationToken),
            result => result?.Evidence.Count ?? 0);

    public async Task<IReadOnlyList<TopologySourceNode>> ResolveTopologySourceNodesAsync(
        IReadOnlyList<string> sourceIds,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(scope);
        if (sourceIds.Count > 1000 || sourceIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid topology source lookup.", nameof(sourceIds));
        return await AuditedTopologyAsync("sources.resolve", scope,
            string.Create(CultureInfo.InvariantCulture, $"sources={sourceIds.Count}"), async () =>
            {
                var wanted = sourceIds.Distinct(StringComparer.Ordinal).ToArray();
                var rows = await controlPlane.TopologyNodes.AsNoTracking()
                    .Where(node => node.SourceId != null && wanted.Contains(node.SourceId) && node.Enabled && !node.Deleted)
                    .ToArrayAsync(cancellationToken);
                return rows.Where(node => TopologyIdentity.CanReadOwner(scope, node.OwnerGroup))
                    .OrderBy(node => node.SourceId, StringComparer.Ordinal)
                    .Select(node => new TopologySourceNode(node.SourceId!, node.Id, node.OwnerGroup)).ToArray();
            }, result => result.Length);
    }

    public Task<TopologyPathResult> GetTopologyPathAsync(
        TopologyPathQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("path", scope, Summary(query.ReadClockUnixNano, query.PageSize, query.Cursor is not null),
            () => Topology.PathAsync(query, scope, cancellationToken), result => result.EdgeIds.Count);

    public Task<TopologyCommonAncestorResult> GetTopologyCommonAncestorAsync(
        TopologyCommonAncestorQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("ancestors", scope, Summary(query.ReadClockUnixNano, query.NodeIds.Count, false),
            () => Topology.CommonAncestorAsync(query, scope, cancellationToken), result => result.Paths.Count);

    public Task<TopologyCommonAncestorResult> GetTopologyGroupedCommonAncestorAsync(
        TopologyGroupedAncestorQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("ancestors", scope, Summary(query.ReadClockUnixNano, query.TargetGroups.Count, false),
            () => Topology.GroupedCommonAncestorAsync(query, scope, cancellationToken), result => result.Paths.Count);

    public Task<TopologyNeighborhoodResult> GetTopologyNeighborhoodAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("neighbors", scope, Summary(query.ReadClockUnixNano, query.PageSize, query.Cursor is not null),
            () => Topology.NeighborhoodAsync(query, scope, cancellationToken), result => result.Neighbors.Count);

    public Task<TopologyOutsideNeighborCount> CountExternalTopologyNeighborsAsync(
        TopologyNeighborhoodQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        AuditedTopologyAsync("neighbors.outside", scope, Summary(query.ReadClockUnixNano, 1, false),
            () => Topology.CountExternalNeighborsAsync(query, scope, cancellationToken), result => result.Count ?? 0);

    private static string Summary(decimal readClock, int limit, bool continuation) =>
        string.Create(CultureInfo.InvariantCulture, $"as_of_nano={readClock};limit={limit};cursor={continuation}");

    private async Task<T> AuditedTopologyAsync<T>(string action, AccessScope scope, string summary,
        Func<Task<T>> operation, Func<T, int> count)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var watch = Stopwatch.StartNew();
        var outcome = "Failed";
        var succeeded = false;
        var rows = 0;
        try
        {
            var result = await operation();
            rows = count(result);
            succeeded = true;
            outcome = "Success";
            return result;
        }
        catch (OperationCanceledException) { outcome = "Cancelled"; throw; }
        finally
        {
            using var auditBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _audit.RecordAsync(new AuditRecord(scope.Subject, "topology." + action, "topology",
                Describe(scope, ScopePredicate.From(scope)), summary + ";outcome=" + outcome,
                rows, (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue), succeeded), auditBudget.Token);
        }
    }
}
