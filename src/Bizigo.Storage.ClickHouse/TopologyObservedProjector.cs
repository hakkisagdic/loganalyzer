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
    ITopologyProjectionCheckpoints? checkpoints = null,
    Func<CancellationToken, Task<string?>>? readPendingKey = null) : ITopologyObservedProjector
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
        ["semantic_anchor", "first_fingerprint", "conflicting_fingerprint", "candidate_context_json", "publication_seq"];
    private static readonly string[] LegacyConflictColumns =
        ["semantic_anchor", "first_fingerprint", "conflicting_fingerprint", "publication_seq"];
    private static readonly string[] ParentResolutionColumns =
        ["child_anchor", "child_fingerprint", "reason", "captured_context_json", "publication_seq"];
    private static readonly string[] ManifestColumns =
        ["publication_key", "payload_sha256", "rowset_sha256", "edge_count", "conflict_count", "payload_json"];

    private sealed record Manifest(TopologyProjectionBatch Batch, string Payload, string PayloadHash,
        string RowsetHash, uint EdgeCount, uint ConflictCount);

    public async Task ProjectAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        // A previous process may have inserted only part of an older A batch.
        // Finish that exact durable row set before reducing today's cumulative A+B trace.
        if (readPendingKey is not null)
        {
            var pendingKey = await readPendingKey(cancellationToken);
            if (pendingKey is not null)
            {
                var pending = await ReadManifestAsync(pendingKey, cancellationToken)
                    ?? throw new InvalidDataException("Pending topology publication has no durable batch manifest.");
                await PublishManifestAsync(pending, cancellationToken);
            }
        }

        foreach (var traceId in records.Where(r => r.Signal == TelemetrySignal.Traces && r.Topology is not null)
                     .Select(r => r.TraceId.ToLowerInvariant()).Distinct(StringComparer.Ordinal))
        {
            var persisted = await ReadTraceAsync(traceId, cancellationToken);
            var batch = TopologyObservation.Reduce(persisted);
            if (batch.Edges.Count == 0 && batch.Conflicts.Count == 0
                && batch.ParentResolutions.Count == 0) continue;
            await RejectLegacyConflictOverwriteAsync(batch.Conflicts, cancellationToken);
            var manifest = await EnsureManifestAsync(batch, cancellationToken);
            await PublishManifestAsync(manifest, cancellationToken);
        }
    }

    private async Task RejectLegacyConflictOverwriteAsync(IReadOnlyList<TopologySpanConflict> conflicts,
        CancellationToken token)
    {
        if (conflicts.Count == 0) return;
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        foreach (var conflict in conflicts)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT candidate_context_json FROM topology_span_conflicts "
                + "WHERE semantic_anchor = {anchor:String}";
            command.AddParameter("anchor", conflict.Anchor);
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var context = reader.GetString(0);
                var captured = context.Length == 0 ? null :
                    JsonSerializer.Deserialize<TopologyConflictCandidate[]>(context, RawSignalCodec.Json);
                if (captured is null || captured.Length == 0
                    || captured.Any(static candidate => candidate.ContextVersion != 3))
                    throw new InvalidDataException("Legacy topology conflict requires explicit attribution repair before republishing.");
            }
        }
    }

    private async Task PublishManifestAsync(Manifest manifest, CancellationToken cancellationToken)
    {
        // Recheck the row mapping, not only the semantic JSON: a software
        // upgrade cannot silently change an unfinished physical publication.
        if (RowsetHash(manifest.Batch) != manifest.RowsetHash
            && RowsetHash(manifest.Batch, legacyFormat: true) != manifest.RowsetHash)
            throw new InvalidDataException("Topology batch row set changed since its durable manifest was written.");
        await publish(manifest.Batch.PublicationKey, async (sequence, token) =>
        {
            var batch = manifest.Batch;
            if (batch.Edges.Count != 0)
            {
                var rows = batch.Edges.Select(e => EdgeRow(e, sequence)).ToArray();
                var written = await context.Client.InsertBinaryAsync("topology_edges_observed", EdgeColumns, rows,
                    new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
                if (written != rows.Length) throw new IOException("Incomplete observed topology insert.");
            }
            if (batch.Conflicts.Count != 0)
            {
                var legacy = batch.Conflicts.All(static conflict => conflict.Candidates.Count == 0);
                var rows = batch.Conflicts.Select(c => ConflictRow(c, sequence, legacy)).ToArray();
                var written = await context.Client.InsertBinaryAsync("topology_span_conflicts",
                    legacy ? LegacyConflictColumns : ConflictColumns, rows,
                    new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
                if (written != rows.Length) throw new IOException("Incomplete topology conflict insert.");
            }
            if (batch.ProjectionVersion >= 4 && batch.ParentResolutions.Count != 0)
            {
                var rows = batch.ParentResolutions.Select(item => ParentResolutionRow(item, sequence)).ToArray();
                var written = await context.Client.InsertBinaryAsync("topology_parent_resolution",
                    ParentResolutionColumns, rows,
                    new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
                if (written != rows.Length) throw new IOException("Incomplete parent-resolution insert.");
            }
            await (checkpoints ?? new NoTopologyProjectionCheckpoints())
                .ReachAsync("after-observed-db-before-publish", token);
        }, cancellationToken);
    }

    private async Task<Manifest> EnsureManifestAsync(TopologyProjectionBatch batch, CancellationToken token)
    {
        var proposed = BuildManifest(batch);
        var existing = await ReadManifestAsync(batch.PublicationKey, token);
        if (existing is not null)
        {
            VerifySameManifest(existing, proposed);
            return existing;
        }
        var row = new object[] { batch.PublicationKey, proposed.PayloadHash, proposed.RowsetHash,
            proposed.EdgeCount, proposed.ConflictCount, proposed.Payload };
        var written = await context.Client.InsertBinaryAsync("topology_projection_batches", ManifestColumns, [row],
            new InsertOptions { BatchSize = 1, MaxDegreeOfParallelism = 1 }, token);
        if (written != 1) throw new IOException("Incomplete durable topology batch manifest insert.");
        existing = await ReadManifestAsync(batch.PublicationKey, token)
            ?? throw new IOException("Durable topology batch manifest is not readable after insert.");
        VerifySameManifest(existing, proposed);
        return existing;
    }

    private static Manifest BuildManifest(TopologyProjectionBatch batch)
    {
        var payload = JsonSerializer.Serialize(batch, RawSignalCodec.Json);
        return new(batch, payload, RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(payload)),
            RowsetHash(batch), checked((uint)batch.Edges.Count), checked((uint)batch.Conflicts.Count));
    }

    private static void VerifySameManifest(Manifest actual, Manifest expected)
    {
        if (actual.PayloadHash != expected.PayloadHash || actual.RowsetHash != expected.RowsetHash
            || actual.EdgeCount != expected.EdgeCount || actual.ConflictCount != expected.ConflictCount
            || actual.Payload != expected.Payload)
            throw new InvalidDataException("Conflicting immutable topology batch manifest for the same publication key.");
    }

    private static string RowsetHash(TopologyProjectionBatch batch, bool legacyFormat = false)
    {
        var legacy = legacyFormat || batch.Conflicts.Count != 0
            && batch.Conflicts.All(static conflict => conflict.Candidates.Count == 0);
        var rows = batch.ProjectionVersion >= 4
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                EdgeColumns,
                Edges = batch.Edges.Select(e => EdgeRow(e, 0)).ToArray(),
                ConflictColumns = legacy ? LegacyConflictColumns : ConflictColumns,
                Conflicts = batch.Conflicts.Select(c => ConflictRow(c, 0, legacy)).ToArray(),
                ParentResolutionColumns,
                ParentResolutions = batch.ParentResolutions.Select(item => ParentResolutionRow(item, 0)).ToArray(),
            }, RawSignalCodec.Json)
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                EdgeColumns,
                Edges = batch.Edges.Select(e => EdgeRow(e, 0)).ToArray(),
                ConflictColumns = legacy ? LegacyConflictColumns : ConflictColumns,
                Conflicts = batch.Conflicts.Select(c => ConflictRow(c, 0, legacy)).ToArray(),
            }, RawSignalCodec.Json);
        return RawSignalEnvelope.Hash(rows);
    }

    private async Task<Manifest?> ReadManifestAsync(string key, CancellationToken token)
    {
        if (key.Length != 64 || key.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid pending topology publication key.");
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT payload_sha256, rowset_sha256, edge_count, conflict_count, payload_json "
            + "FROM topology_projection_batches WHERE publication_key = {publication_key:String} LIMIT 2 "
            + "SETTINGS max_execution_time="
            + Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300).ToString(CultureInfo.InvariantCulture);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        command.AddParameter("publication_key", key);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var payloadHash = reader.GetString(0);
        var rowsetHash = reader.GetString(1);
        var edgeCount = Convert.ToUInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        var conflictCount = Convert.ToUInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
        var payload = reader.GetString(4);
        if (await reader.ReadAsync(token))
            throw new InvalidDataException("Publication key has conflicting durable topology manifests.");
        if (RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(payload)) != payloadHash)
            throw new InvalidDataException("Durable topology batch manifest checksum mismatch.");
        var batch = JsonSerializer.Deserialize<TopologyProjectionBatch>(payload, RawSignalCodec.Json)
            ?? throw new InvalidDataException("Durable topology batch manifest is empty.");
        if (batch.PublicationKey != key || batch.Edges.Count != edgeCount || batch.Conflicts.Count != conflictCount
            || RowsetHash(batch) != rowsetHash && RowsetHash(batch, legacyFormat: true) != rowsetHash)
            throw new InvalidDataException("Durable topology batch manifest row count or payload mismatch.");
        return new(batch, payload, payloadHash, rowsetHash, edgeCount, conflictCount);
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

    private static object[] ConflictRow(TopologySpanConflict conflict, ulong sequence, bool legacy)
    {
        if (legacy) return [conflict.Anchor, conflict.FirstFingerprint, conflict.ConflictingFingerprint, sequence];
        if (conflict.Candidates.Count == 0)
            throw new InvalidDataException("A topology conflict needs admission-captured candidate context.");
        return [conflict.Anchor, conflict.FirstFingerprint, conflict.ConflictingFingerprint,
            JsonSerializer.Serialize(conflict.Candidates, RawSignalCodec.Json), sequence];
    }

    private static object[] ParentResolutionRow(TopologyParentResolution item, ulong sequence) =>
        [item.ChildAnchor, item.ChildFingerprint, item.Reason,
            JsonSerializer.Serialize(item, RawSignalCodec.Json), sequence];
}
