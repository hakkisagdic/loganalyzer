using System.Globalization;

namespace Bizigo.Contracts;

public enum TopologyNodeKind { Source = 1, Service = 2, ServiceInstance = 3, Network = 4 }
public enum TopologyProvenance { Declared = 1, Observed = 2 }
public enum TopologyRelation { DependsOn = 1, Contains = 2, ConnectsTo = 3 }

/// <summary>Names and aliases are never identities. The UUID is allocated by the registry.</summary>
public static class TopologyIdentity
{
    public static string Node(TopologyNodeKind kind, Guid id)
    {
        if (!Enum.IsDefined(kind) || id == Guid.Empty) throw new ArgumentException("Invalid topology identity.");
        return kind.ToString().ToLowerInvariant() + ":" + id.ToString("D", CultureInfo.InvariantCulture);
    }

    public static TopologyNodeKind Kind(string id)
    {
        if (id is null) throw new ArgumentException("Invalid topology identity.", nameof(id));
        var separator = id.IndexOf(':', StringComparison.Ordinal);
        if (separator < 1 || !Enum.TryParse<TopologyNodeKind>(id[..separator], true, out var kind)
            || !Guid.TryParseExact(id[(separator + 1)..], "D", out var uuid)
            || !string.Equals(Node(kind, uuid), id, StringComparison.Ordinal))
            throw new ArgumentException("Invalid canonical topology identity.", nameof(id));
        return kind;
    }

    public static decimal Nano(DateTimeOffset time) => (decimal)(time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
    public static bool At(decimal from, decimal? to, ulong time) => from <= time && (to is null || time < to);
    public static bool CanReadOwner(AccessScope scope, string ownerGroup) =>
        (ownerGroup != OwnerGroups.Unassigned || scope.IsUnrestricted) && scope.Allows(ownerGroup);
    public static bool CanReadEdge(AccessScope scope, string parentOwner, string childOwner) =>
        CanReadOwner(scope, parentOwner) && CanReadOwner(scope, childOwner);
}

/// <summary>Immutable topology decision for a single admitted leaf. Negative decisions are durable too.</summary>
public sealed record TopologyLeafBinding(
    string LeafKey, ulong EventTimeUnixNano, string SourceId, string OwnerGroup, long SourceHistoryRevision,
    string? ServiceNodeId, string? InstanceNodeId, long? ServiceBindingRevision, long? InstanceBindingRevision,
    long? NodeHistoryRevision, string? DisplayName, string Reason)
{
    public string? NodeId => InstanceNodeId ?? ServiceNodeId;
    public bool Resolved => Reason == "Resolved" && NodeId is not null;
}

public sealed record TopologyBindingRequest(TelemetryOwnerBinding Owner, string ServiceNamespace,
    string ServiceName, string? InstanceId);

public interface ITopologyBindingResolver
{
    Task<TopologyLeafBinding[]> ResolveAsync(IReadOnlyList<TopologyBindingRequest> requests, CancellationToken cancellationToken);
}
