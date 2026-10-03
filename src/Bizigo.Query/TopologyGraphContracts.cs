using Bizigo.Contracts;

namespace Bizigo.Query;

public sealed record TopologyGraphSnapshot(long PublishedSequence, IReadOnlyList<TopologyEdgeProjection> Edges)
{
    public IReadOnlyList<TopologyNodeProjection> Nodes { get; init; } = [];
    public IReadOnlyList<TopologyEvidenceReference> Evidence { get; init; } = [];
    public IReadOnlyList<TopologyEdgeProjection> ConflictedEdges { get; init; } = [];
    public IReadOnlyList<TopologyConflictProjection> ConflictCandidates { get; init; } = [];
    public IReadOnlyList<TopologyConflictArc> ConflictArcs { get; init; } = [];
    public IReadOnlyList<TopologyUnresolvedParentProjection> UnresolvedParents { get; init; } = [];
    public bool ObservedMigrationRequired { get; init; }
}

/// <summary>Internal admission-captured conflict impact, never returned on public wire.</summary>
public sealed record TopologyConflictProjection(string OwnerGroup, string? NodeId,
    decimal EventTimeUnixNano, decimal ExpiresUnixNano)
{
    public string SourceId { get; init; } = string.Empty;
    public string ResolutionReason { get; init; } = string.Empty;
}

/// <summary>Admission-captured possible edge impact; it is never public proof.</summary>
public sealed record TopologyConflictArc(string FromNode, string ToNode,
    string FromOwnerGroup, string ToOwnerGroup, decimal ChildEventTimeUnixNano,
    decimal EffectiveExpiryUnixNano);

/// <summary>Captured negative parent decision; parent identity is never public.</summary>
public sealed record TopologyUnresolvedParentProjection(string OwnerGroup, string SourceId,
    string? ChildNodeId, string Reason, decimal ChildEventTimeUnixNano, decimal ChildExpiryUnixNano);

public sealed class TopologyObservedMigrationRequiredException()
    : IOException("Observed topology conflict attribution requires an explicit durable migration.");

public sealed class TopologyConflictException()
    : IOException("A related published topology identity is conflicted.");

/// <summary>
/// İlk okuma <paramref name="publishedSequence"/> olmadan en son tamamlanmış
/// snapshot'ı alır. Cursor devamı aynı sequence'i istemek zorundadır; kaynak o
/// snapshot'ı artık veremiyorsa yeni veriye kaymak yerine hata verir.
/// </summary>
public interface ITopologyGraphSnapshotSource
{
    Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken);
}

public sealed record TopologyPathQuery(
    string FromNodeId,
    string ToNodeId,
    decimal ReadClockUnixNano,
    int PageSize = 100,
    string? Cursor = null,
    decimal? FromUnixNano = null,
    decimal? ToUnixNano = null)
{
    /// <summary>Internal RCA declared-state cutoff; observed expiry uses the current expiry clock.</summary>
    public decimal? DeclaredStateClockUnixNano { get; init; }
    /// <summary>Current server read clock for observed TTL, independent of historical as-of.</summary>
    public decimal? ExpiryReadClockUnixNano { get; init; }
}

public sealed record TopologyNodeQuery(decimal ReadClockUnixNano, int PageSize = 100, string? Cursor = null,
    TopologyNodeKind? Kind = null);

public sealed record TopologyEdgeQuery(decimal ReadClockUnixNano, int PageSize = 100, string? Cursor = null,
    TopologyRelation? Relation = null, TopologyProvenance? Provenance = null,
    decimal? FromUnixNano = null, decimal? ToUnixNano = null)
{
    public decimal? ExpiryReadClockUnixNano { get; init; }
}

public sealed record TopologyNodeProjection(string Id, TopologyNodeKind Kind, string DisplayName, string OwnerGroup,
    bool Enabled, bool Deleted, long Version, decimal ValidFromUnixNano, decimal? ValidToUnixNano);

public sealed record TopologyEvidenceReference(string EdgeId, string Id, string TraceLogicalId, string SpanLogicalId,
    decimal EventTimeUnixNano);

public sealed record TopologyEdgeDetail(TopologyEdgeProjection Edge, IReadOnlyList<TopologyEvidenceReference> Evidence,
    string? EvidenceCursor);

public sealed record TopologyCommonAncestorQuery(
    IReadOnlyList<string> NodeIds,
    decimal ReadClockUnixNano,
    decimal? FromUnixNano = null,
    decimal? ToUnixNano = null)
{
    public decimal? ExpiryReadClockUnixNano { get; init; }
}

/// <summary>Internal RCA group candidates; public REST keeps the flat ancestor contract.</summary>
public sealed record TopologyGroupedAncestorQuery(
    IReadOnlyList<IReadOnlyList<string>> TargetGroups,
    decimal ReadClockUnixNano,
    decimal? FromUnixNano = null,
    decimal? ToUnixNano = null)
{
    /// <summary>Internal RCA declared-state cutoff; observed expiry uses the current expiry clock.</summary>
    public decimal? DeclaredStateClockUnixNano { get; init; }
    public decimal? ExpiryReadClockUnixNano { get; init; }
}

public sealed record TopologyNeighborhoodQuery(
    string NodeId,
    decimal ReadClockUnixNano,
    int PageSize = 100,
    string? Cursor = null,
    TopologyRelation? Relation = null,
    decimal? FromUnixNano = null,
    decimal? ToUnixNano = null)
{
    public decimal? ExpiryReadClockUnixNano { get; init; }
}

public sealed record TopologyGraphPage<T>(
    IReadOnlyList<T> Items,
    string? Cursor,
    long PublishedSequence)
{
    public decimal? EarliestEvidenceExpiryUnixNano { get; init; }
}

public sealed record TopologyPathResult(
    TopologyGraphResultStatus Status,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> EdgeIds,
    string? Cursor,
    long PublishedSequence,
    string? Reason = null)
{
    public decimal? EarliestEvidenceExpiryUnixNano { get; init; }
}

public sealed record TopologyCommonAncestorResult(
    TopologyGraphResultStatus Status,
    string? NodeId,
    IReadOnlyList<TopologyPathProof> Paths,
    long PublishedSequence,
    string? Reason = null);

public enum TopologyGraphResultStatus { Found = 1, Unreachable = 2, NotVerified = 3 }

public sealed record TopologyPathProof(
    string TargetNodeId,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> EdgeIds);

public enum TopologyNeighborDirection { Outgoing = 1, Incoming = 2 }

public sealed record TopologyNeighbor(
    string NodeId,
    string EdgeId,
    TopologyRelation Relation,
    TopologyProvenance Provenance,
    TopologyNeighborDirection Direction);

public sealed record TopologyNeighborhoodResult(
    IReadOnlyList<TopologyNeighbor> Neighbors,
    int? ExternalNeighborCount,
    string? ExternalNeighborReason,
    string? Cursor,
    long PublishedSequence)
{
    public decimal? EarliestEvidenceExpiryUnixNano { get; init; }
}

public sealed record TopologyOutsideNeighborCount(int? Count, string? Reason);

public sealed record TopologySourceNode(string SourceId, string NodeId, string OwnerGroup);

public enum TopologySourceTargetStatus { Complete = 1, Missing = 2, Hidden = 3, Ambiguous = 4 }

/// <summary>Declared Contains proof chain; never includes observed dependency proof IDs.</summary>
public sealed record TopologySourceTarget(string NodeId, IReadOnlyList<string> MappingEdgeIds);

public sealed record TopologySourceTargets(string SourceId, string SourceNodeId,
    IReadOnlyList<TopologySourceTarget> Targets, TopologySourceTargetStatus Status, string? Reason);

public sealed class TopologyCursorException : ArgumentException
{
    public TopologyCursorException(string message) : base(message) { }
    public TopologyCursorException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class TopologySnapshotUnavailableException(long publishedSequence)
    : InvalidOperationException($"Topology snapshot {publishedSequence} is unavailable.")
{
    public long PublishedSequence { get; } = publishedSequence;
}
