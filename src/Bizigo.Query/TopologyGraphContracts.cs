using Bizigo.Contracts;

namespace Bizigo.Query;

public sealed record TopologyGraphSnapshot(long PublishedSequence, IReadOnlyList<TopologyEdgeProjection> Edges)
{
    public IReadOnlyList<TopologyNodeProjection> Nodes { get; init; } = [];
    public IReadOnlyList<TopologyEvidenceReference> Evidence { get; init; } = [];
    public IReadOnlyList<TopologyEdgeProjection> ConflictedEdges { get; init; } = [];
    public IReadOnlyList<TopologyConflictProjection> ConflictCandidates { get; init; } = [];
    public bool ObservedMigrationRequired { get; init; }
}

/// <summary>Internal admission-captured conflict impact, never returned on public wire.</summary>
public sealed record TopologyConflictProjection(string OwnerGroup, string? NodeId,
    decimal EventTimeUnixNano, decimal ExpiresUnixNano);

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
    decimal? ToUnixNano = null);

public sealed record TopologyNodeQuery(decimal ReadClockUnixNano, int PageSize = 100, string? Cursor = null,
    TopologyNodeKind? Kind = null);

public sealed record TopologyEdgeQuery(decimal ReadClockUnixNano, int PageSize = 100, string? Cursor = null,
    TopologyRelation? Relation = null, TopologyProvenance? Provenance = null,
    decimal? FromUnixNano = null, decimal? ToUnixNano = null);

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
    decimal? ToUnixNano = null);

public sealed record TopologyNeighborhoodQuery(
    string NodeId,
    decimal ReadClockUnixNano,
    int PageSize = 100,
    string? Cursor = null,
    TopologyRelation? Relation = null,
    decimal? FromUnixNano = null,
    decimal? ToUnixNano = null);

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
