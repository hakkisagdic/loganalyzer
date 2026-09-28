using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

/// <summary>One history query resolves positive and negative admission decisions. Store failures propagate.</summary>
public sealed class HistoricalTopologyBindings(IDbContextFactory<ControlPlaneDbContext> factory) : ITopologyBindingResolver
{
    public async Task<TopologyLeafBinding[]> ResolveAsync(IReadOnlyList<TopologyBindingRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        // The binding and node history join is one committed SQL snapshot.
        var sources = requests.Select(r => r.Owner.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        var lower = (decimal)requests.Min(r => r.Owner.EventTimeUnixNano);
        var upper = (decimal)requests.Max(r => r.Owner.EventTimeUnixNano);
        var nodeHistory = db.TopologyNodeHistory.AsNoTracking()
            .Where(node => node.FromNano <= upper && (node.ToNano == null || node.ToNano > lower));
        var rows = await (from binding in db.TopologyBindings.AsNoTracking()
                          join node in nodeHistory on binding.TargetNodeId equals node.NodeId into histories
                          from node in histories.DefaultIfEmpty()
                          where sources.Contains(binding.SourceId)
                            && binding.FromNano <= upper && (binding.ToNano == null || binding.ToNano > lower)
                          select new Row(binding, node)).ToArrayAsync(cancellationToken);
        return requests.Select(r => Resolve(r, rows)).ToArray();
    }

    public sealed record Row(TopologyBindingEntity Binding, TopologyNodeHistoryEntity? Node);

    public static TopologyLeafBinding Resolve(TopologyBindingRequest request, IReadOnlyList<Row> rows)
    {
        var owner = request.Owner;
        TopologyLeafBinding Missing(string reason) => new(owner.LeafKey, owner.EventTimeUnixNano,
            owner.SourceId, owner.OwnerGroup, owner.HistoryRevision, null, null, null, null, null, null, reason);
        if (owner.OwnerGroup == OwnerGroups.Unassigned || owner.HistoryRevision <= 0 || owner.Reason != "known")
            return Missing("SourceUnresolved");
        if (string.IsNullOrWhiteSpace(request.ServiceName) || request.ServiceNamespace is null)
            return Missing("MissingBinding");
        var eligible = rows.Where(r => r.Binding.SourceId == owner.SourceId
            && TopologyIdentity.At(r.Binding.FromNano, r.Binding.ToNano, owner.EventTimeUnixNano)).ToArray();
        // Several history rows for one binding are not several aliases. Missing,
        // gapped or overlapping node history is corruption, never a negative binding.
        var services = eligible.Where(r => r.Binding.InstanceId == "" && r.Binding.ServiceNamespace == request.ServiceNamespace
            && r.Binding.ServiceName == request.ServiceName).GroupBy(r => r.Binding.Revision).ToArray();
        if (services.Length != 1) return Missing(services.Length == 0 ? "MissingBinding" : "AmbiguousBinding");
        var service = AtEvent(services[0], owner.EventTimeUnixNano);
        if (TopologyIdentity.Kind(service.Binding.TargetNodeId) != TopologyNodeKind.Service
            || service.Binding.ServiceNodeId != service.Binding.TargetNodeId)
            throw new InvalidDataException("Invalid service topology binding.");
        if (!service.Node.Enabled || service.Node.OwnerGroup != owner.OwnerGroup) return Missing("BindingOwnerMismatch");
        var endpoint = service;
        if (request.InstanceId is not null)
        {
            if (string.IsNullOrWhiteSpace(request.InstanceId)) return Missing("MissingInstanceBinding");
            var instances = eligible.Where(r => r.Binding.InstanceId == request.InstanceId
                && r.Binding.ServiceNodeId == service.Binding.TargetNodeId).GroupBy(r => r.Binding.Revision).ToArray();
            if (instances.Length != 1) return Missing(instances.Length == 0 ? "MissingInstanceBinding" : "AmbiguousBinding");
            endpoint = AtEvent(instances[0], owner.EventTimeUnixNano);
            if (TopologyIdentity.Kind(endpoint.Binding.TargetNodeId) != TopologyNodeKind.ServiceInstance)
                throw new InvalidDataException("Invalid instance topology binding.");
            if (!endpoint.Node.Enabled || endpoint.Node.OwnerGroup != owner.OwnerGroup) return Missing("BindingOwnerMismatch");
        }
        return new(owner.LeafKey, owner.EventTimeUnixNano, owner.SourceId, owner.OwnerGroup, owner.HistoryRevision,
            service.Binding.TargetNodeId, request.InstanceId is null ? null : endpoint.Binding.TargetNodeId,
            service.Binding.Revision, request.InstanceId is null ? null : endpoint.Binding.Revision,
            endpoint.Node.Revision, endpoint.Node.DisplayName, "Resolved");
    }

    private static (TopologyBindingEntity Binding, TopologyNodeHistoryEntity Node) AtEvent(IEnumerable<Row> rows, ulong time)
    {
        var matching = rows.Where(r => r.Node is not null && TopologyIdentity.At(r.Node.FromNano, r.Node.ToNano, time)).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("Missing or overlapping topology node history for an authoritative binding.");
        return (matching[0].Binding, matching[0].Node!);
    }
}
