namespace Bizigo.Contracts;

/// <summary>The relation is a wire value, not a display name or an unrestricted enum integer.</summary>
public static class TopologyEdgeRelations
{
    public const string DependsOn = "depends_on";
    public const string Contains = "contains";
    public const string ConnectsTo = "connects_to";

    public static bool Valid(string? relation) => relation is DependsOn or Contains or ConnectsTo;

    public static bool ValidEndpoints(string relation, TopologyNodeKind from, TopologyNodeKind to) =>
        relation != Contains || (from == TopologyNodeKind.Source && to == TopologyNodeKind.Service)
            || (from == TopologyNodeKind.Service && to == TopologyNodeKind.ServiceInstance);
}

public sealed record TopologyDeclaredEdgeInput(string FromNodeId, string ToNodeId, string Relation,
    string? Version = null, string? Provenance = null);

public sealed record TopologyDeclaredEdgeVersion(
    Guid Id, string FromNodeId, string ToNodeId, string Relation,
    string FromOwnerGroup, string ToOwnerGroup, bool Directed, string Provenance,
    decimal Confidence, string Version, DateTimeOffset? DeletedAt, string ValidFromUnixNano,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CreatedBy, string UpdatedBy);

public sealed record TopologyDeclaredEdgeResult(int Status, TopologyDeclaredEdgeVersion? Edge = null, string? Error = null);

/// <summary>Physical observed row contains both endpoint owner decisions; readers must authorize both.</summary>
public sealed record TopologyObservedEdge(
    string Id, string FromNodeId, string ToNodeId, string Relation,
    string FromOwnerGroup, string ToOwnerGroup, string ParentSourceId, string ChildSourceId,
    long ParentNodeBindingRevision, long ChildNodeBindingRevision,
    long ParentOwnerHistoryRevision, long ChildOwnerHistoryRevision,
    ulong ParentEventTimeUnixNano, ulong ChildEventTimeUnixNano,
    string ParentOccurrenceId, string ChildOccurrenceId,
    string ParentEventId, string ChildEventId, string ProofId,
    decimal FirstSeenUnixNano, decimal LastSeenUnixNano, decimal ExpiresUnixNano,
    string Fingerprint, ulong PublicationSequence);
