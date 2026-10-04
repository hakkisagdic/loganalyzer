using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <summary>Fresh per-attempt targets; a crashed attempt is never appended to on resume.</summary>
public sealed record TopologyRepairCopyTables(string Observed, string Conflicts,
    string ParentResolutions, string Lifecycle);

/// <summary>
/// CH half of the quiesced 0014 cutover. PG phase, attestation, receipt prefix
/// and session lock are owned by the Query runner; this component never makes
/// a copy of a surviving legacy physical row.
/// </summary>
public sealed class TopologyPublicationRepairStorage(ClickHouseContext context)
{
    private static readonly string[] ParentColumns =
        ["child_anchor", "child_fingerprint", "reason", "captured_context_json", "publication_seq"];
    private static readonly (string Canonical, string Template)[] TablePairs =
    [
        ("topology_edges_observed", "topology_edges_observed_v0014_template"),
        ("topology_span_conflicts", "topology_span_conflicts_v0014_template"),
        ("topology_parent_resolution", "topology_parent_resolution_v0014_template"),
        ("topology_edge_lifecycle", "topology_edge_lifecycle_v0014_template"),
    ];

    public Task<TopologyVerifiedManifest?> ReadVerifiedManifestAsync(string key, CancellationToken token) =>
        TopologyObservedProjector.LoadVerifiedManifestBatchAsync(context, key, token);

    public async Task<bool> IsProvablyEmptyAsync(CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        foreach (var table in new[] { "topology_projection_batches", "topology_edges_observed",
                     "topology_span_conflicts", "topology_parent_resolution", "topology_edge_lifecycle" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count() FROM {table}";
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            if (Convert.ToUInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
                return false;
        }
        return await new TopologyPublicationWatermarkReader(context).ReadAsync(token) == 0;
    }

    public async Task<TopologyRepairCopyTables> CreateFreshCopiesAsync(Guid copyAttemptId, CancellationToken token)
    {
        if (copyAttemptId == Guid.Empty) throw new ArgumentException("A fresh copy attempt ID is required.", nameof(copyAttemptId));
        var suffix = copyAttemptId.ToString("N");
        var names = new TopologyRepairCopyTables("topology_edges_observed_copy_" + suffix,
            "topology_span_conflicts_copy_" + suffix,
            "topology_parent_resolution_copy_" + suffix,
            "topology_edge_lifecycle_copy_" + suffix);
        var shadows = new[] { names.Observed, names.Conflicts, names.ParentResolutions, names.Lifecycle };
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        for (var index = 0; index < shadows.Length; index++)
        {
            using var command = connection.CreateCommand();
            // Every identifier is a fixed prefix plus a validated Guid:N.
            command.CommandText = $"CREATE TABLE {shadows[index]} AS {TablePairs[index].Template}";
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            await command.ExecuteNonQueryAsync(token);
        }
        return names;
    }

    public async Task WriteVerifiedBatchAsync(TopologyRepairCopyTables copies, TopologyProjectionBatch batch,
        ulong sequence, CancellationToken token)
    {
        ValidateCopies(copies);
        if (batch.ProjectionVersion < 4)
            throw new InvalidDataException("A pre-v4 publication has no authoritative parent-resolution decisions.");
        var frozen = batch.Edges.Select(edge => TopologyObservedProjector.ReconstructEdgeRow(edge, sequence)).ToArray();
        var digests = frozen.Select(TopologyObservedRowDigest.Compute).ToArray();
        if (frozen.Length != 0)
        {
            var physical = frozen.Select((row, index) =>
            {
                object[] value = [.. row, digests[index]];
                return value;
            }).ToArray();
            await InsertExactAsync(copies.Observed, TopologyObservedProjector.DerivedPhysicalEdgeColumns,
                physical, token);
            var lifecycle = frozen.Select((row, index) =>
                TopologyObservedProjector.ReconstructLifecycleRow(row, digests[index])).ToArray();
            await InsertExactAsync(copies.Lifecycle, TopologyObservedProjector.EdgeLifecycleColumns,
                lifecycle, token);
        }
        if (batch.Conflicts.Count != 0)
        {
            // A legacy marker without admission-captured candidate context
            // cannot be certified healthy by guessing from current inventory.
            if (batch.Conflicts.Any(conflict => !TopologyObservedProjector.HasAttributedConflict(conflict)))
                throw new InvalidDataException("Legacy conflict authority is unattributed.");
            var rows = batch.Conflicts.Select(conflict =>
                TopologyObservedProjector.ReconstructConflictRow(conflict, sequence)).ToArray();
            await InsertExactAsync(copies.Conflicts, TopologyObservedProjector.FrozenConflictColumns, rows, token);
        }
        if (batch.ProjectionVersion >= 4 && batch.ParentResolutions.Count != 0)
        {
            var rows = batch.ParentResolutions.Select(decision =>
                TopologyObservedProjector.ReconstructParentResolutionRow(decision, sequence)).ToArray();
            await InsertExactAsync(copies.ParentResolutions, ParentColumns, rows, token);
        }
    }

    public Task VerifyCopiedBatchAsync(TopologyRepairCopyTables copies, TopologyProjectionBatch batch,
        ulong sequence, CancellationToken token)
    {
        ValidateCopies(copies);
        return VerifyBatchInTablesAsync(copies, batch, sequence, token);
    }

    public Task VerifyCanonicalBatchAsync(TopologyProjectionBatch batch, ulong sequence,
        CancellationToken token) => VerifyBatchInTablesAsync(new(
            "topology_edges_observed", "topology_span_conflicts",
            "topology_parent_resolution", "topology_edge_lifecycle"), batch, sequence, token);

    public async Task VerifyCanonicalTotalsAsync(long edges, long conflicts, long parents,
        CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        var expectations = new (string Table, long Count)[]
        {
            ("topology_edge_lifecycle", edges),
            ("topology_span_conflicts", conflicts),
            ("topology_parent_resolution", parents),
        };
        foreach (var (table, expected) in expectations)
        {
            using var command = connection.CreateCommand();
            // Fixed internal table names only; no user identifier is interpolated.
            command.CommandText = $"SELECT count() FROM {table}";
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            var actual = Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
            if (actual != expected)
                throw new InvalidDataException("Repaired non-TTL authority row count diverged from manifests.");
        }
    }

    private async Task VerifyBatchInTablesAsync(TopologyRepairCopyTables copies,
        TopologyProjectionBatch batch, ulong sequence, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        foreach (var edge in batch.Edges)
        {
            var frozen = TopologyObservedProjector.ReconstructEdgeRow(edge, sequence);
            var expected = TopologyObservedRowDigest.Compute(frozen);
            var lifecycle = await ReadLifecycleAsync(connection, copies.Lifecycle, edge.EdgeId, sequence, token);
            if (lifecycle.Count == 0 || lifecycle.Any(row => row.Digest != expected
                    || row.From != (string)frozen[3] || row.To != (string)frozen[4]
                    || row.ParentOwner != (string)frozen[2] || row.ChildOwner != (string)frozen[1]
                    || row.FirstSeen != (ulong)frozen[27] || row.LastSeen != (ulong)frozen[28]
                    || row.ParentEvent != (ulong)frozen[25] || row.ChildEvent != (ulong)frozen[26]
                    || row.Expiry != (decimal)frozen[35]))
                throw new InvalidDataException("Repaired topology lifecycle row differs from immutable authority.");

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {TopologyObservedRowHydration.SelectList}, physical_row_sha256 "
                + $"FROM {copies.Observed} WHERE edge_id = {{edge:String}} AND publication_seq = {{seq:UInt64}} LIMIT 1025";
            command.AddParameter("edge", edge.EdgeId);
            command.AddParameter("seq", sequence);
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            await using var reader = await command.ExecuteReaderAsync(token);
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                if (++count > 1024) throw new InvalidDataException("Repair physical-row duplicate cap exceeded.");
                var actual = TopologyObservedRowHydration.ReadValues(reader);
                if (TopologyObservedRowDigest.Compute(actual) != expected
                    || reader.GetString(40) != expected)
                    throw new InvalidDataException("Repaired topology physical row differs from immutable authority.");
            }
            var nowNano = checked((decimal)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100m);
            if (count == 0 && (decimal)frozen[35] > nowNano)
                throw new InvalidDataException("Unexpired repaired topology physical row is missing.");
        }
        foreach (var conflict in batch.Conflicts)
        {
            var expected = TopologyObservedProjector.ReconstructConflictRow(conflict, sequence);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT first_fingerprint, conflicting_fingerprint, candidate_context_json "
                + $"FROM {copies.Conflicts} WHERE semantic_anchor = {{anchor:String}} "
                + "AND publication_seq = {seq:UInt64} LIMIT 1025";
            command.AddParameter("anchor", conflict.Anchor);
            command.AddParameter("seq", sequence);
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            await using var reader = await command.ExecuteReaderAsync(token);
            var count = 0;
            while (await reader.ReadAsync(token))
            {
                if (++count > 1024 || reader.GetString(0) != (string)expected[1]
                    || reader.GetString(1) != (string)expected[2]
                    || reader.GetString(2) != (string)expected[3])
                    throw new InvalidDataException("Divergent repaired topology conflict marker.");
            }
            if (count == 0) throw new InvalidDataException("Repaired topology conflict marker is missing.");
        }
        if (batch.ProjectionVersion >= 4)
            foreach (var decision in batch.ParentResolutions)
            {
                var expected = TopologyObservedProjector.ReconstructParentResolutionRow(decision, sequence);
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT reason, captured_context_json FROM {copies.ParentResolutions} "
                    + "WHERE child_anchor = {anchor:String} AND child_fingerprint = {fingerprint:String} "
                    + "AND publication_seq = {seq:UInt64} LIMIT 1025";
                command.AddParameter("anchor", expected[0]);
                command.AddParameter("fingerprint", expected[1]);
                command.AddParameter("seq", sequence);
                command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
                await using var reader = await command.ExecuteReaderAsync(token);
                var count = 0;
                while (await reader.ReadAsync(token))
                {
                    if (++count > 1024 || reader.GetString(0) != (string)expected[2]
                        || reader.GetString(1) != (string)expected[3])
                        throw new InvalidDataException("Divergent repaired parent-resolution decision.");
                }
                if (count == 0) throw new InvalidDataException("Repaired parent-resolution decision is missing.");
            }
    }

    private sealed record LifecycleRow(string From, string To, string ParentOwner, string ChildOwner,
        ulong FirstSeen, ulong LastSeen, ulong ParentEvent, ulong ChildEvent, decimal Expiry, string Digest);

    private async Task<IReadOnlyList<LifecycleRow>> ReadLifecycleAsync(
        global::ClickHouse.Driver.ADO.ClickHouseConnection connection, string table, string edgeId,
        ulong sequence, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT from_node_id, to_node_id, parent_owner_group, child_owner_group, "
            + $"first_seen, last_seen, parent_event_time_nano, child_event_time_nano, "
            + $"expires_nano, physical_row_sha256 FROM {table} "
            + "WHERE edge_id = {edge:String} AND publication_seq = {seq:UInt64} LIMIT 1025";
        command.AddParameter("edge", edgeId);
        command.AddParameter("seq", sequence);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new List<LifecycleRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (result.Count == 1024) throw new InvalidDataException("Repair lifecycle duplicate cap exceeded.");
            result.Add(new LifecycleRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Convert.ToUInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToUInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToUInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToUInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToDecimal(reader.GetValue(8), CultureInfo.InvariantCulture), reader.GetString(9)));
        }
        return result;
    }

    public async Task ExchangeFreshCopiesAsync(TopologyRepairCopyTables copies,
        Func<CancellationToken, Task> requireAdmissionBeforeEachExchange,
        Func<int, CancellationToken, Task> afterEachExchange, CancellationToken token)
    {
        ValidateCopies(copies);
        ArgumentNullException.ThrowIfNull(requireAdmissionBeforeEachExchange);
        ArgumentNullException.ThrowIfNull(afterEachExchange);
        var shadows = new[] { copies.Observed, copies.Conflicts, copies.ParentResolutions, copies.Lifecycle };
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        for (var index = 0; index < shadows.Length; index++)
        {
            await requireAdmissionBeforeEachExchange(token);
            using var command = connection.CreateCommand();
            command.CommandText = $"EXCHANGE TABLES {TablePairs[index].Canonical} AND {shadows[index]}";
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            await command.ExecuteNonQueryAsync(token);
            await afterEachExchange(index, token);
        }
        // The displaced canonical identities remain under the shadow names
        // for operator audit. An interrupted exchange is retried with a new
        // attempt and new shadows, never by exchanging these names backward.
    }

    private static void ValidateCopies(TopologyRepairCopyTables copies)
    {
        var names = new[] { copies.Observed, copies.Conflicts, copies.ParentResolutions, copies.Lifecycle };
        for (var index = 0; index < names.Length; index++)
        {
            var prefix = TablePairs[index].Canonical + "_copy_";
            if (!names[index].StartsWith(prefix, StringComparison.Ordinal)
                || names[index].Length != prefix.Length + 32
                || names[index][prefix.Length..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Invalid topology repair copy identifier.");
        }
    }

    private async Task InsertExactAsync(string table, IReadOnlyList<string> columns,
        object[][] rows, CancellationToken token)
    {
        var written = await context.Client.InsertBinaryAsync(table, columns, rows,
            new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, token);
        if (written != rows.Length) throw new IOException("Incomplete topology repair copy insert.");
    }
}
