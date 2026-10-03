using System.Text.Json.Serialization;

namespace Bizigo.Api;

// Public graph wire types are deliberately independent of the storage/query
// records. Every counter, version, sequence and nanosecond value is a decimal
// string so JavaScript clients cannot silently round an authoritative value.
public sealed record TopologyNodeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("valid_from_unix_nano")] string ValidFromUnixNano,
    [property: JsonPropertyName("valid_to_unix_nano")] string? ValidToUnixNano);

public sealed record TopologyNodePageDto(
    [property: JsonPropertyName("nodes")] IReadOnlyList<TopologyNodeDto> Nodes,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("published_sequence")] string PublishedSequence);

public sealed record TopologyNodeDetailDto(
    [property: JsonPropertyName("node")] TopologyNodeDto Node);

public sealed record TopologyEdgeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from_node_id")] string FromNodeId,
    [property: JsonPropertyName("to_node_id")] string ToNodeId,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("directed")] bool Directed,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("from_owner_group")] string FromOwnerGroup,
    [property: JsonPropertyName("to_owner_group")] string ToOwnerGroup,
    [property: JsonPropertyName("visibility")] string Visibility,
    [property: JsonPropertyName("first_seen_unix_nano")] string FirstSeenUnixNano,
    [property: JsonPropertyName("last_seen_unix_nano")] string LastSeenUnixNano,
    [property: JsonPropertyName("effective_expiry_unix_nano")] string? EffectiveExpiryUnixNano,
    [property: JsonPropertyName("publication_sequence")] string PublicationSequence,
    [property: JsonPropertyName("version")] string Version);

public sealed record TopologyEdgePageDto(
    [property: JsonPropertyName("edges")] IReadOnlyList<TopologyEdgeDto> Edges,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("published_sequence")] string PublishedSequence);

public sealed record TopologyEvidenceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("trace_logical_id")] string TraceLogicalId,
    [property: JsonPropertyName("span_logical_id")] string SpanLogicalId,
    [property: JsonPropertyName("event_time_unix_nano")] string EventTimeUnixNano);

public sealed record TopologyEdgeDetailDto(
    [property: JsonPropertyName("edge")] TopologyEdgeDto Edge,
    [property: JsonPropertyName("evidence")] IReadOnlyList<TopologyEvidenceDto> Evidence,
    [property: JsonPropertyName("evidence_cursor")] string? EvidenceCursor);

public sealed record TopologyNeighborDto(
    [property: JsonPropertyName("node_id")] string NodeId,
    [property: JsonPropertyName("edge_id")] string EdgeId,
    [property: JsonPropertyName("relation")] string Relation,
    [property: JsonPropertyName("provenance")] string Provenance,
    [property: JsonPropertyName("direction")] string Direction);

public sealed record TopologyNeighborhoodDto(
    [property: JsonPropertyName("neighbors")] IReadOnlyList<TopologyNeighborDto> Neighbors,
    [property: JsonPropertyName("outside_neighbor_count")] string? OutsideNeighborCount,
    [property: JsonPropertyName("outside_neighbor_reason")] string? OutsideNeighborReason,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("published_sequence")] string PublishedSequence);

public sealed record TopologyPathDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("nodes")] IReadOnlyList<string> Nodes,
    [property: JsonPropertyName("edge_ids")] IReadOnlyList<string> EdgeIds,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("published_sequence")] string PublishedSequence);

public sealed record TopologyPathProofDto(
    [property: JsonPropertyName("target_node_id")] string TargetNodeId,
    [property: JsonPropertyName("nodes")] IReadOnlyList<string> Nodes,
    [property: JsonPropertyName("edge_ids")] IReadOnlyList<string> EdgeIds);

public sealed record TopologyAncestorsDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("node_id")] string? NodeId,
    [property: JsonPropertyName("paths")] IReadOnlyList<TopologyPathProofDto> Paths,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("published_sequence")] string PublishedSequence);

public sealed record TopologyProblemDto(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message);
