using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using ClickHouse.Driver;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public interface ITopologyObservedProjector
{
    Task ProjectAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken);
}

public interface ITopologyProjectionCheckpoints
{
    Task ReachAsync(string stage, CancellationToken cancellationToken);
}

public sealed class NoTopologyProjectionCheckpoints : ITopologyProjectionCheckpoints
{
    public Task ReachAsync(string stage, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Re-reads durable typed trace rows after each raw insert, so child-first and
/// parent-later requests/processes converge. Projection callbacks use the
/// publication sequence allocated by the PG/CH coordinator; a crash before
/// that coordinator commits leaves these rows invisible to product readers.
/// </summary>
public sealed class TopologyObservedProjector(
    ClickHouseContext context,
    Func<string, Func<ulong, CancellationToken, Task>, CancellationToken, Task<ulong>> publish,
    ITopologyProjectionCheckpoints? checkpoints = null) : ITopologyObservedProjector
{
    private static readonly string[] EdgeColumns =
    [
        "owner_group", "child_owner_group", "parent_owner_group", "from_node_id", "to_node_id",
        "relation", "directed", "provenance", "confidence", "edge_id", "trace_logical_id",
        "parent_span_logical_id", "span_logical_id", "parent_semantic_anchor", "child_semantic_anchor",
        "parent_fingerprint", "child_fingerprint", "parent_source_id", "child_source_id",
        "parent_node_binding_revision", "child_node_binding_revision", "parent_owner_history_revision",
        "child_owner_history_revision", "parent_node_history_revision", "child_node_history_revision",
        "parent_event_time_nano", "child_event_time_nano", "first_seen", "last_seen",
        "parent_occurrence_ids", "child_occurrence_ids", "evidence_occurrence_ids",
        "parent_trace_expiry", "child_trace_expiry", "observed_expiry", "expires_nano",
        "fingerprint", "publication_seq", "ttl_at", "ttl_supported",
    ];
    private static readonly string[] ConflictColumns =
        ["semantic_anchor", "first_fingerprint", "conflicting_fingerprint", "publication_seq"];

    public async Task ProjectAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        foreach (var traceId in records.Where(r => r.Signal == TelemetrySignal.Traces && r.Topology is not null)
                     .Select(r => r.TraceId.ToLowerInvariant()).Distinct(StringComparer.Ordinal))
        {
            var persisted = await ReadTraceAsync(traceId, cancellationToken);
            var batch = TopologyObservation.Reduce(persisted);
            if (batch.Edges.Count == 0 && batch.Conflicts.Count == 0) continue;
            await publish(batch.PublicationKey, async (sequence, token) =>
            {
                if (batch.Edges.Count != 0)
                {
                    var rows = batch.Edges.Select(e => EdgeRow(e, sequence)).ToArray();
                    var written = await context.Client.InsertBinaryAsync("topology_edges_observed", EdgeColumns, rows,
                        new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
                    if (written != rows.Length) throw new IOException("Incomplete observed topology insert.");
                }
                if (batch.Conflicts.Count != 0)
                {
                    var rows = batch.Conflicts.Select(c => new object[]
                        { c.Anchor, c.FirstFingerprint, c.ConflictingFingerprint, sequence }).ToArray();
                    var written = await context.Client.InsertBinaryAsync("topology_span_conflicts", ConflictColumns, rows,
                        new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
                    if (written != rows.Length) throw new IOException("Incomplete topology conflict insert.");
                }
                await (checkpoints ?? new NoTopologyProjectionCheckpoints())
                    .ReachAsync("after-observed-db-before-publish", token);
            }, cancellationToken);
        }
    }

    private async Task<TelemetryRecord[]> ReadTraceAsync(string traceId, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record, record_sha256, record_version FROM trace_spans "
            + "WHERE trace_id = {trace_id:String} SETTINGS max_execution_time="
            + Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300).ToString(CultureInfo.InvariantCulture);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        command.AddParameter("trace_id", traceId);
        var records = new List<TelemetryRecord>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (records.Count >= 100_000) throw new InvalidDataException("Trace exceeds topology projector safety bound.");
            var json = reader.GetString(0);
            if (Convert.ToUInt16(reader.GetValue(2), CultureInfo.InvariantCulture) != 1
                || RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(json)) != reader.GetString(1))
                throw new InvalidDataException("Invalid persisted typed span checksum/version.");
            var record = JsonSerializer.Deserialize<TelemetryRecord>(json, RawSignalCodec.Json)
                ?? throw new InvalidDataException("Missing persisted typed span.");
            TelemetryWriter.Validate(record);
            if (!string.Equals(record.TraceId, traceId, StringComparison.Ordinal))
                throw new InvalidDataException("Persisted trace index does not match the typed span.");
            records.Add(record);
        }
        return records.ToArray();
    }

    private static object[] EdgeRow(TopologyObservedEvent edge, ulong sequence)
    {
        var parent = edge.Parent;
        var child = edge.Child;
        if (parent.NodeId is null || child.NodeId is null || string.IsNullOrWhiteSpace(parent.OwnerGroup)
            || string.IsNullOrWhiteSpace(child.OwnerGroup) || !parent.Resolved || !child.Resolved)
            throw new InvalidDataException("Unresolved topology endpoint cannot be published.");
        var evidence = edge.ParentOccurrences.Concat(edge.ChildOccurrences).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var seconds = decimal.Ceiling(edge.EffectiveExpiry / 1_000_000_000m);
        var ttlSupported = seconds <= uint.MaxValue;
        var ttlAt = DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(seconds, uint.MaxValue)).UtcDateTime;
        var parentBindingRevision = parent.InstanceBindingRevision ?? parent.ServiceBindingRevision ?? 0;
        var childBindingRevision = child.InstanceBindingRevision ?? child.ServiceBindingRevision ?? 0;
        return
        [
            child.OwnerGroup, child.OwnerGroup, parent.OwnerGroup, parent.NodeId, child.NodeId,
            TopologyEdgeRelations.DependsOn, (byte)1, "observed", 0.5f, edge.EdgeId, edge.TraceId,
            edge.ParentSpanId, edge.ChildSpanId, edge.ParentAnchor, edge.ChildAnchor,
            edge.ParentFingerprint, edge.ChildFingerprint, parent.SourceId, child.SourceId,
            parentBindingRevision, childBindingRevision, parent.SourceHistoryRevision, child.SourceHistoryRevision,
            parent.NodeHistoryRevision ?? 0, child.NodeHistoryRevision ?? 0,
            edge.ParentStartNano, edge.ChildStartNano, edge.ChildStartNano, edge.ChildStartNano,
            edge.ParentOccurrences.ToArray(), edge.ChildOccurrences.ToArray(), evidence,
            edge.ParentTraceExpiry, edge.ChildTraceExpiry, edge.ObservedExpiry, edge.EffectiveExpiry,
            edge.EdgeId, sequence, ttlAt, (byte)(ttlSupported ? 1 : 0),
        ];
    }
}
