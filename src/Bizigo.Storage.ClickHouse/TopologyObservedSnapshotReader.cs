using System.Globalization;
using System.Text;
using System.Text.Json;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public sealed record TopologyObservedSnapshotRow(
    string Id, string FromNodeId, string ToNodeId, string Relation, string Provenance,
    bool Directed, decimal Confidence, string FromOwnerGroup, string ToOwnerGroup,
    decimal FirstSeenUnixNano, decimal LastSeenUnixNano, decimal ExpiresUnixNano,
    ulong PublicationSequence, string ParentSemanticAnchor, string ChildSemanticAnchor,
    IReadOnlyList<string> EvidenceOccurrenceIds, string TraceLogicalId, string SpanLogicalId,
    decimal EventTimeUnixNano, string ParentSpanLogicalId, decimal ParentEventTimeUnixNano,
    IReadOnlyList<string> ParentOccurrenceIds, IReadOnlyList<string> ChildOccurrenceIds);

/// <summary>
/// Reads only published observed projections. The watermark predicate is
/// applied before argMax, so a newer unacknowledged retry cannot hide an older
/// committed row with the same logical edge ID.
/// </summary>
public sealed class TopologyObservedSnapshotReader(ClickHouseContext context)
{
    public async Task<IReadOnlyList<TopologyObservedSnapshotRow>> ReadAsync(ulong watermark,
        CancellationToken cancellationToken = default)
    {
        var conflicts = await ReadConflictsAsync(watermark, cancellationToken);
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
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var rows = new List<TopologyObservedSnapshotRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var parent = ReadString(reader.GetValue(13));
            var child = ReadString(reader.GetValue(14));
            if (conflicts.Contains(parent) || conflicts.Contains(child))
                throw new InvalidDataException("Conflicting published topology span anchor.");
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
                parentOccurrences, childOccurrences));
        }
        return rows;
    }

    private async Task<HashSet<string>> ReadConflictsAsync(ulong watermark, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT semantic_anchor FROM topology_span_conflicts
            WHERE publication_seq <= {watermark:UInt64}
            GROUP BY semantic_anchor
            """;
        command.AddParameter("watermark", watermark);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadString(reader.GetValue(0)));
        return result;
    }

    private static string ReadString(object value) => value switch
    {
        string text => text.TrimEnd('\0'),
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd('\0')
            ?? throw new InvalidDataException("Missing topology projection string."),
    };
}
