using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public sealed record TopologyObservedConflictRow(string Anchor,
    IReadOnlyList<TopologyConflictCandidate> Candidates, bool Unattributed);

public sealed record TopologyObservedSnapshot(IReadOnlyList<TopologyObservedSnapshotRow> Rows,
    IReadOnlyList<TopologyObservedConflictRow> Conflicts)
{
    public IReadOnlyList<TopologyParentResolution> ParentResolutions { get; init; } = [];
}

/// <summary>Operational preflight only; never serialize marker counts to public topology responses.</summary>
public sealed record TopologyObservedReadiness(ulong Watermark, bool Usable, int UnattributedMarkers);

/// <summary>Diagnostic SQL shape only; bound values and hidden owner identities are never observed.</summary>
public sealed record TopologySqlPlan(string Route, string Sql, IReadOnlyList<string> BoundParameterNames);

public sealed record TopologyObservedSnapshotRow(
    string Id, string FromNodeId, string ToNodeId, string Relation, string Provenance,
    bool Directed, decimal Confidence, string FromOwnerGroup, string ToOwnerGroup,
    decimal FirstSeenUnixNano, decimal LastSeenUnixNano, decimal ExpiresUnixNano,
    ulong PublicationSequence, string ParentSemanticAnchor, string ChildSemanticAnchor,
    IReadOnlyList<string> EvidenceOccurrenceIds, string TraceLogicalId, string SpanLogicalId,
    decimal EventTimeUnixNano, string ParentSpanLogicalId, decimal ParentEventTimeUnixNano,
    IReadOnlyList<string> ParentOccurrenceIds, IReadOnlyList<string> ChildOccurrenceIds)
{
    /// <summary>A committed anchor conflict invalidates this row as proof, but
    /// authorization and query-window decisions belong to the scoped reader.</summary>
    public bool HasPublishedConflict { get; init; }
}

/// <summary>
/// Reads only published observed projections. The watermark predicate is
/// applied before argMax, so a newer unacknowledged retry cannot hide an older
/// committed row with the same logical edge ID.
/// </summary>
public sealed class TopologyObservedSnapshotReader(ClickHouseContext context)
{
    public Action<TopologySqlPlan>? ObserveQuery { get; set; }

    public async Task<TopologyObservedReadiness> CheckReadinessAsync(ulong watermark,
        CancellationToken cancellationToken = default)
    {
        var conflicts = await ReadConflictsAsync(watermark, cancellationToken);
        var missing = conflicts.Count(static conflict => conflict.Unattributed);
        return new(watermark, missing == 0, missing);
    }

    public async Task<IReadOnlyList<TopologyObservedSnapshotRow>> ReadAsync(ulong watermark,
        CancellationToken cancellationToken = default) =>
        (await ReadSnapshotAsync(watermark, cancellationToken)).Rows;

    public async Task<TopologyObservedSnapshot> ReadSnapshotAsync(ulong watermark,
        CancellationToken cancellationToken = default)
    {
        var conflicts = await ReadConflictsAsync(watermark, cancellationToken);
        var conflictedAnchors = conflicts.Select(static conflict => conflict.Anchor).ToHashSet(StringComparer.Ordinal);
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT edge_id,
                argMax(from_node_id, publication_seq),
                argMax(to_node_id, publication_seq),
                argMax(relation, publication_seq),
                argMax(provenance, publication_seq),
                argMax(directed, publication_seq),
                argMax(confidence, publication_seq),
                argMax(parent_owner_group, publication_seq),
                argMax(child_owner_group, publication_seq),
                argMax(first_seen, publication_seq),
                argMax(last_seen, publication_seq),
                argMax(expires_nano, publication_seq),
                max(publication_seq),
                argMax(parent_semantic_anchor, publication_seq),
                argMax(child_semantic_anchor, publication_seq),
                argMax(toJSONString(evidence_occurrence_ids), publication_seq),
                argMax(trace_logical_id, publication_seq),
                argMax(span_logical_id, publication_seq),
                argMax(child_event_time_nano, publication_seq),
                argMax(parent_span_logical_id, publication_seq),
                argMax(parent_event_time_nano, publication_seq),
                argMax(toJSONString(parent_occurrence_ids), publication_seq),
                argMax(toJSONString(child_occurrence_ids), publication_seq)
            FROM topology_edges_observed
            WHERE publication_seq <= {watermark:UInt64}
            GROUP BY edge_id
            ORDER BY edge_id
            """;
        command.AddParameter("watermark", watermark);
        ObserveQuery?.Invoke(new("observed-edges", command.CommandText, ["watermark"]));
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var rows = new List<TopologyObservedSnapshotRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var parent = ReadString(reader.GetValue(13));
            var child = ReadString(reader.GetValue(14));
            var hasConflict = conflictedAnchors.Contains(parent) || conflictedAnchors.Contains(child);
            var evidenceJson = ReadString(reader.GetValue(15));
            var evidence = JsonSerializer.Deserialize<string[]>(evidenceJson)
                ?? throw new InvalidDataException("Invalid topology evidence occurrence vector.");
            var parentOccurrences = JsonSerializer.Deserialize<string[]>(ReadString(reader.GetValue(21)))
                ?? throw new InvalidDataException("Invalid parent topology evidence occurrence vector.");
            var childOccurrences = JsonSerializer.Deserialize<string[]>(ReadString(reader.GetValue(22)))
                ?? throw new InvalidDataException("Invalid child topology evidence occurrence vector.");
            rows.Add(new(
                ReadString(reader.GetValue(0)), ReadString(reader.GetValue(1)), ReadString(reader.GetValue(2)),
                ReadString(reader.GetValue(3)), ReadString(reader.GetValue(4)),
                Convert.ToByte(reader.GetValue(5), CultureInfo.InvariantCulture) != 0,
                Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                ReadString(reader.GetValue(7)), ReadString(reader.GetValue(8)),
                Convert.ToDecimal(reader.GetValue(9), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(11), CultureInfo.InvariantCulture),
                Convert.ToUInt64(reader.GetValue(12), CultureInfo.InvariantCulture), parent, child,
                evidence, ReadString(reader.GetValue(16)), ReadString(reader.GetValue(17)),
                Convert.ToDecimal(reader.GetValue(18), CultureInfo.InvariantCulture),
                ReadString(reader.GetValue(19)), Convert.ToDecimal(reader.GetValue(20), CultureInfo.InvariantCulture),
                parentOccurrences, childOccurrences) { HasPublishedConflict = hasConflict });
        }
        return new(rows, conflicts)
        {
            ParentResolutions = await ReadParentResolutionsAsync(watermark, cancellationToken),
        };
    }

    private async Task<IReadOnlyList<TopologyParentResolution>> ReadParentResolutionsAsync(
        ulong watermark, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT child_anchor, child_fingerprint,
                argMax(reason, publication_seq),
                argMax(captured_context_json, publication_seq)
            FROM topology_parent_resolution
            WHERE publication_seq <= {watermark:UInt64}
            GROUP BY child_anchor, child_fingerprint
            ORDER BY child_anchor, child_fingerprint
            """;
        command.AddParameter("watermark", watermark);
        ObserveQuery?.Invoke(new("parent-resolution", command.CommandText, ["watermark"]));
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new List<TopologyParentResolution>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var childAnchor = ReadString(reader.GetValue(0));
            var fingerprint = ReadString(reader.GetValue(1));
            var reason = ReadString(reader.GetValue(2));
            var captured = JsonSerializer.Deserialize<TopologyParentResolution>(
                ReadString(reader.GetValue(3)), RawSignalCodec.Json)
                ?? throw new InvalidDataException("Published parent decision lacks captured context.");
            if (captured.ChildAnchor != childAnchor || captured.ChildFingerprint != fingerprint
                || captured.Reason != reason || string.IsNullOrWhiteSpace(captured.SourceId)
                || string.IsNullOrWhiteSpace(captured.OwnerGroup))
                throw new InvalidDataException("Published parent decision context is invalid.");
            result.Add(captured);
        }
        return result;
    }

    private async Task<IReadOnlyList<TopologyObservedConflictRow>> ReadConflictsAsync(ulong watermark, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT semantic_anchor, candidate_context_json FROM topology_span_conflicts
            WHERE publication_seq <= {watermark:UInt64}
            ORDER BY semantic_anchor, publication_seq
            """;
        command.AddParameter("watermark", watermark);
        ObserveQuery?.Invoke(new("conflicts", command.CommandText, ["watermark"]));
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new Dictionary<string, List<TopologyConflictCandidate>>(StringComparer.Ordinal);
        var legacy = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var anchor = ReadString(reader.GetValue(0));
            if (!result.TryGetValue(anchor, out var candidates)) result.Add(anchor, candidates = []);
            var context = ReadString(reader.GetValue(1));
            if (context.Length == 0) { legacy.Add(anchor); continue; }
            var parsed = JsonSerializer.Deserialize<TopologyConflictCandidate[]>(context, RawSignalCodec.Json)
                ?? throw new InvalidDataException("Published topology conflict context is missing.");
            if (parsed.Length == 0 || parsed.Any(candidate => string.IsNullOrWhiteSpace(candidate.OwnerGroup)
                    || string.IsNullOrWhiteSpace(candidate.SourceId)))
                throw new InvalidDataException("Published topology conflict context is invalid.");
            if (parsed.Any(candidate => candidate.ContextVersion != 3
                    || string.IsNullOrWhiteSpace(candidate.Anchor)
                    || string.IsNullOrWhiteSpace(candidate.ResolutionReason)))
                legacy.Add(anchor);
            candidates.AddRange(parsed);
        }
        return result.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TopologyObservedConflictRow(pair.Key,
                pair.Value.GroupBy(candidate => (candidate.Fingerprint, candidate.OwnerGroup,
                        candidate.SourceId, candidate.NodeId, candidate.EventTimeNano,
                        candidate.ParentAnchor, candidate.IsConflictedAnchor, candidate.Anchor,
                        candidate.ResolutionReason, candidate.ContextVersion))
                    .Select(group => group.First() with
                    {
                        TraceExpiryNano = group.Min(static candidate => candidate.TraceExpiryNano),
                        ObservedExpiryNano = group.Min(static candidate => candidate.ObservedExpiryNano),
                    })
                    .OrderBy(static candidate => candidate.Fingerprint, StringComparer.Ordinal).ToArray(),
                legacy.Contains(pair.Key))).ToArray();
    }

    private static string ReadString(object value) => value switch
    {
        string text => text.TrimEnd('\0'),
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd('\0')
            ?? throw new InvalidDataException("Missing topology projection string."),
    };
}
