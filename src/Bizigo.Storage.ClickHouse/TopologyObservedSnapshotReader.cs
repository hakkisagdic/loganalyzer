using System.Globalization;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>
/// Diagnostic SQL shape. The optional in-process EXPLAIN replays the exact
/// bound command without exposing owner/anchor values to the observer or a
/// durable artifact; consumers may persist only aggregate plan facts.
/// </summary>
public sealed record TopologySqlPlan(string Route, string Sql, IReadOnlyList<string> BoundParameterNames)
{
    [JsonIgnore] public Func<CancellationToken, Task<string>>? ExplainAsync { get; init; }
}

/// <summary>Physical candidate boundary, not an authorization decision.</summary>
public sealed record TopologyObservedReadScope(AccessScope Scope, decimal WindowFromUnixNano,
    decimal WindowToUnixNano, decimal ExpiryReadClockUnixNano, int MaxCandidates,
    string? NodeId = null, string? EdgeId = null);

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
/// Reads only published observed projections. The non-TTL lifecycle ledger
/// chooses the latest committed edge version before owner/window/expiry tests;
/// an unacknowledged retry cannot hide the older committed version, and TTL
/// cleanup cannot resurrect an older physical proof.
/// </summary>
public sealed class TopologyObservedSnapshotReader(ClickHouseContext context)
{
    // LIMIT bounds materialized rows, not MergeTree work (DISTINCT/argMax can
    // scan many versions first). A server-side read cap makes that work fail
    // closed instead of silently consuming an unbounded physical snapshot.
    private const string ScopedReadSettings =
        " SETTINGS max_rows_to_read = 131072, max_bytes_to_read = 134217728, read_overflow_mode = 'throw'";

    private sealed record LifecycleVersion(string EdgeId, ulong Sequence, string FromNodeId,
        string ToNodeId, string ChildOwnerGroup, string ParentOwnerGroup, ulong FirstSeen,
        ulong LastSeen, ulong ParentEventTimeNano, ulong ChildEventTimeNano,
        decimal ExpiresNano, string PhysicalRowSha256);

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
        => await ReadSnapshotCoreAsync(watermark, null,
            checked((decimal)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100m),
            cancellationToken);

    /// <summary>Internal diagnostic read at an explicit server-side expiry clock.
    /// This never supplies a clock to the public scoped query API.</summary>
    public Task<TopologyObservedSnapshot> ReadSnapshotAsync(ulong watermark,
        decimal expiryReadClockUnixNano, CancellationToken cancellationToken)
    {
        if (expiryReadClockUnixNano < 0 || expiryReadClockUnixNano != decimal.Truncate(expiryReadClockUnixNano))
            throw new ArgumentOutOfRangeException(nameof(expiryReadClockUnixNano));
        return ReadSnapshotCoreAsync(watermark, null, expiryReadClockUnixNano, cancellationToken);
    }

    public Task<TopologyObservedSnapshot> ReadScopedSnapshotAsync(ulong watermark,
        TopologyObservedReadScope scope, CancellationToken cancellationToken = default) =>
        ReadSnapshotCoreAsync(watermark, scope ?? throw new ArgumentNullException(nameof(scope)),
            scope.ExpiryReadClockUnixNano, cancellationToken);

    private async Task<TopologyObservedSnapshot> ReadSnapshotCoreAsync(ulong watermark,
        TopologyObservedReadScope? scope, decimal expiryClock, CancellationToken cancellationToken)
    {
        // The non-TTL ledger is authoritative even when ClickHouse has already
        // removed the latest physical row. Discovery from the TTL table would
        // silently resurrect an older version or produce a false Empty.
        var candidateIds = scope is null ? null : await ReadCandidateIdsAsync(watermark, scope, cancellationToken);
        var eligible = await ReadEligibleLifecycleAsync(watermark, scope, candidateIds,
            expiryClock, cancellationToken);
        var rows = await ReadPhysicalRowsAsync(watermark, scope, eligible, cancellationToken);
        var anchors = rows.SelectMany(static row => new[] { row.ParentSemanticAnchor, row.ChildSemanticAnchor })
            .Distinct(StringComparer.Ordinal).ToArray();
        var conflicts = scope is null
            ? await ReadConflictsAsync(watermark, cancellationToken)
            : await ReadScopedConflictsAsync(watermark, scope, anchors, cancellationToken);
        var conflictedAnchors = conflicts.Select(static conflict => conflict.Anchor).ToHashSet(StringComparer.Ordinal);
        var markedRows = rows.Select(row => row with
        {
            HasPublishedConflict = conflictedAnchors.Contains(row.ParentSemanticAnchor)
                || conflictedAnchors.Contains(row.ChildSemanticAnchor),
        }).ToArray();
        return new(markedRows, conflicts)
        {
            ParentResolutions = scope is null
                ? await ReadParentResolutionsAsync(watermark, cancellationToken)
                : await ReadScopedParentResolutionsAsync(watermark, scope, cancellationToken),
        };
    }

    private async Task<IReadOnlyList<LifecycleVersion>> ReadEligibleLifecycleAsync(ulong watermark,
        TopologyObservedReadScope? scope, IReadOnlyList<string>? candidateIds,
        decimal expiryClock, CancellationToken token)
    {
        if (scope is not null && candidateIds!.Count == 0) return [];
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT child_owner_group, parent_owner_group, from_node_id, to_node_id,
                first_seen, last_seen, parent_event_time_nano, child_event_time_nano,
                edge_id, publication_seq, expires_nano, physical_row_sha256
            FROM topology_edge_lifecycle
            WHERE publication_seq <= {watermark:UInt64}
            """ + (scope is null ? string.Empty : "\n AND edge_id IN ({candidate_ids:Array(String)})")
                + "\n ORDER BY edge_id, publication_seq"
                + (scope is null ? string.Empty : " LIMIT {read_limit:UInt32}" + ScopedReadSettings);
        command.AddParameter("watermark", watermark);
        if (scope is not null)
        {
            command.AddParameter("candidate_ids", candidateIds!.ToArray());
            command.AddParameter("read_limit", checked((uint)scope.MaxCandidates + 1));
        }
        Observe("observed-lifecycle", command,
            scope is null ? ["watermark"] : ["watermark", "candidate_ids", "read_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var versions = new Dictionary<(string EdgeId, ulong Sequence), LifecycleVersion>();
        var latest = new Dictionary<string, LifecycleVersion>(StringComparer.Ordinal);
        var physicalRows = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (scope is not null && ++physicalRows > scope.MaxCandidates)
                throw new IOException("Observed lifecycle read exceeds its physical capacity.");
            LifecycleVersion version;
            try
            {
                version = new LifecycleVersion(
                    ReadExactField(reader.GetValue(8)),
                    Convert.ToUInt64(reader.GetValue(9), CultureInfo.InvariantCulture),
                    ReadExactField(reader.GetValue(2)), ReadExactField(reader.GetValue(3)),
                    ReadExactField(reader.GetValue(0)), ReadExactField(reader.GetValue(1)),
                    Convert.ToUInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    Convert.ToUInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    Convert.ToUInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                    Convert.ToUInt64(reader.GetValue(7), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(reader.GetValue(10), CultureInfo.InvariantCulture),
                    ReadExactField(reader.GetValue(11)));
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException
                or InvalidCastException or OverflowException)
            {
                throw new TopologyObservedRepairUnavailableException();
            }
            if (versions.TryGetValue((version.EdgeId, version.Sequence), out var previous))
            {
                if (previous != version) throw new TopologyObservedRepairUnavailableException();
                continue;
            }
            versions.Add((version.EdgeId, version.Sequence), version);
            if (!latest.TryGetValue(version.EdgeId, out var current)
                || version.Sequence > current.Sequence)
                latest[version.EdgeId] = version;
        }
        return latest.Values.Where(version => expiryClock < version.ExpiresNano
                && (scope is null || (version.LastSeen >= scope.WindowFromUnixNano
                    && version.LastSeen < scope.WindowToUnixNano
                    && (scope.Scope.IsUnrestricted
                        || (scope.Scope.OwnerGroups.Contains(version.ParentOwnerGroup)
                            && version.ParentOwnerGroup != "_unassigned")
                        || (scope.Scope.OwnerGroups.Contains(version.ChildOwnerGroup)
                            && version.ChildOwnerGroup != "_unassigned"))
                    && (scope.NodeId is null || version.FromNodeId == scope.NodeId
                        || version.ToNodeId == scope.NodeId)
                    && (scope.EdgeId is null || version.EdgeId == scope.EdgeId)))
            .OrderBy(static version => version.EdgeId, StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyList<TopologyObservedSnapshotRow>> ReadPhysicalRowsAsync(ulong watermark,
        TopologyObservedReadScope? scope, IReadOnlyList<LifecycleVersion> eligible,
        CancellationToken token)
    {
        if (eligible.Count == 0) return [];
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + TopologyObservedRowHydration.SelectList
            + ", physical_row_sha256 FROM topology_edges_observed "
            + "WHERE publication_seq <= {watermark:UInt64} "
            + "AND edge_id IN ({candidate_ids:Array(String)}) "
            + "ORDER BY edge_id, publication_seq"
            + (scope is null ? string.Empty : " LIMIT {read_limit:UInt32}" + ScopedReadSettings);
        command.AddParameter("watermark", watermark);
        command.AddParameter("candidate_ids", eligible.Select(static row => row.EdgeId).ToArray());
        if (scope is not null)
            command.AddParameter("read_limit", checked((uint)scope.MaxCandidates + 1));
        Observe("observed-edges", command, scope is null
            ? ["watermark", "candidate_ids"] : ["watermark", "candidate_ids", "read_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var required = eligible.ToDictionary(static row => row.EdgeId, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TopologyObservedSnapshotRow>(eligible.Count);
        var physicalRows = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (scope is not null && ++physicalRows > scope.MaxCandidates)
                throw new IOException("Observed physical read exceeds its physical capacity.");
            string edgeId;
            ulong sequence;
            try
            {
                edgeId = ReadExactField(reader.GetValue(9));
                sequence = Convert.ToUInt64(reader.GetValue(37), CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException
                or InvalidCastException or OverflowException)
            {
                throw new TopologyObservedRepairUnavailableException();
            }
            if (!required.TryGetValue(edgeId, out var authority) || sequence != authority.Sequence)
                continue;
            object?[] values;
            string storedHash;
            string computedHash;
            try
            {
                values = TopologyObservedRowHydration.ReadValues(reader);
                storedHash = ReadExactField(reader.GetValue(40));
                computedHash = TopologyObservedRowDigest.Compute(values);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException
                or FormatException or InvalidCastException or OverflowException)
            {
                throw new TopologyObservedRepairUnavailableException();
            }
            if (storedHash != computedHash || storedHash != authority.PhysicalRowSha256
                || !PhysicalMatchesLifecycle(values, authority))
                throw new TopologyObservedRepairUnavailableException();
            if (found.Add(edgeId)) rows.Add(ProjectPhysical(values));
        }
        if (found.Count != required.Count)
            throw new TopologyObservedRepairUnavailableException();
        return rows;
    }

    private static bool PhysicalMatchesLifecycle(object?[] values, LifecycleVersion authority) =>
        (string)values[9]! == authority.EdgeId
        && (ulong)values[37]! == authority.Sequence
        && (string)values[3]! == authority.FromNodeId
        && (string)values[4]! == authority.ToNodeId
        && (string)values[1]! == authority.ChildOwnerGroup
        && (string)values[2]! == authority.ParentOwnerGroup
        && (ulong)values[27]! == authority.FirstSeen
        && (ulong)values[28]! == authority.LastSeen
        && (ulong)values[25]! == authority.ParentEventTimeNano
        && (ulong)values[26]! == authority.ChildEventTimeNano
        && (decimal)values[35]! == authority.ExpiresNano;

    private static TopologyObservedSnapshotRow ProjectPhysical(object?[] values) => new(
        (string)values[9]!, (string)values[3]!, (string)values[4]!,
        (string)values[5]!, (string)values[7]!, (byte)values[6]! != 0,
        (decimal)(float)values[8]!,
        (string)values[2]!, (string)values[1]!,
        (ulong)values[27]!, (ulong)values[28]!, (decimal)values[35]!,
        (ulong)values[37]!, (string)values[13]!, (string)values[14]!,
        (string[])values[31]!, (string)values[10]!, (string)values[12]!,
        (ulong)values[26]!, (string)values[11]!, (ulong)values[25]!,
        (string[])values[29]!, (string[])values[30]!);

    private async Task<IReadOnlyList<string>> ReadCandidateIdsAsync(ulong watermark,
        TopologyObservedReadScope scope, CancellationToken token)
    {
        if (scope.MaxCandidates < 1 || scope.WindowFromUnixNano >= scope.WindowToUnixNano)
            throw new ArgumentOutOfRangeException(nameof(scope));
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        var restricted = !scope.Scope.IsUnrestricted;
        command.CommandText = """
            SELECT DISTINCT edge_id FROM topology_edge_lifecycle
            WHERE publication_seq <= {watermark:UInt64}
                AND last_seen >= {window_from:Decimal(21,0)}
                AND last_seen < {window_to:Decimal(21,0)}
            """ + (restricted ? "\n" + """
                AND (parent_owner_group IN ({scope_groups:Array(String)})
                    OR child_owner_group IN ({scope_groups:Array(String)}))
            """ : string.Empty)
                + (scope.NodeId is null ? string.Empty : "\n" + """
                AND (from_node_id = {node_id:String} OR to_node_id = {node_id:String})
            """) + (scope.EdgeId is null ? string.Empty : "\n" + """
                AND edge_id = {edge_id:String}
            """) + "\n" + """
            ORDER BY edge_id LIMIT {candidate_limit:UInt32}
            """ + ScopedReadSettings;
        command.AddParameter("watermark", watermark);
        command.AddParameter("window_from", scope.WindowFromUnixNano);
        command.AddParameter("window_to", scope.WindowToUnixNano);
        if (restricted) command.AddParameter("scope_groups",
            scope.Scope.OwnerGroups.Where(static group => group != "_unassigned").ToArray());
        if (scope.NodeId is not null) command.AddParameter("node_id", scope.NodeId);
        if (scope.EdgeId is not null) command.AddParameter("edge_id", scope.EdgeId);
        command.AddParameter("candidate_limit", checked((uint)scope.MaxCandidates + 1));
        var bound = new List<string> { "watermark", "window_from", "window_to", "candidate_limit" };
        if (restricted) bound.Add("scope_groups");
        if (scope.NodeId is not null) bound.Add("node_id");
        if (scope.EdgeId is not null) bound.Add("edge_id");
        Observe("observed-candidates", command, bound);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            ids.Add(ReadString(reader.GetValue(0)));
            if (ids.Count > scope.MaxCandidates)
                throw new IOException("Observed topology candidate read exceeds its physical capacity.");
        }
        return ids;
    }

    private async Task<IReadOnlyList<TopologyParentResolution>> ReadScopedParentResolutionsAsync(
        ulong watermark, TopologyObservedReadScope scope, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        var restricted = !scope.Scope.IsUnrestricted;
        command.CommandText = """
            SELECT DISTINCT child_anchor FROM topology_parent_resolution
            WHERE publication_seq <= {watermark:UInt64}
                AND JSONExtractUInt(captured_context_json, 'child_event_time_nano')
                    >= {window_from:Decimal(21,0)}
                AND JSONExtractUInt(captured_context_json, 'child_event_time_nano')
                    < {window_to:Decimal(21,0)}
            """ + (restricted ? "\n AND JSONExtractString(captured_context_json, 'owner_group') IN ({scope_groups:Array(String)})"
                : string.Empty) + "\n" + """
            ORDER BY child_anchor LIMIT {candidate_limit:UInt32}
            """ + ScopedReadSettings;
        command.AddParameter("watermark", watermark);
        command.AddParameter("window_from", scope.WindowFromUnixNano);
        command.AddParameter("window_to", scope.WindowToUnixNano);
        if (restricted) command.AddParameter("scope_groups",
            scope.Scope.OwnerGroups.Where(static group => group != "_unassigned").ToArray());
        command.AddParameter("candidate_limit", checked((uint)scope.MaxCandidates + 1));
        Observe("parent-candidates", command,
            restricted ? ["watermark", "window_from", "window_to", "scope_groups", "candidate_limit"]
                : ["watermark", "window_from", "window_to", "candidate_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var anchors = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                anchors.Add(ReadString(reader.GetValue(0)));
                if (anchors.Count > scope.MaxCandidates)
                    throw new IOException("Parent decision candidate read exceeds its physical capacity.");
            }
        var decisions = await ReadParentResolutionsAsync(watermark, token, anchors, scope.MaxCandidates);
        return decisions.Where(decision =>
            (scope.Scope.IsUnrestricted || scope.Scope.OwnerGroups.Contains(decision.OwnerGroup)
                && decision.OwnerGroup != "_unassigned")
            && decision.ChildEventTimeNano >= scope.WindowFromUnixNano
            && decision.ChildEventTimeNano < scope.WindowToUnixNano
            && scope.ExpiryReadClockUnixNano < decision.ChildExpiryNano).ToArray();
    }

    private async Task<IReadOnlyList<TopologyParentResolution>> ReadParentResolutionsAsync(
        ulong watermark, CancellationToken token, IReadOnlyList<string>? anchors = null,
        int? maxRows = null)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT child_anchor, child_fingerprint, publication_seq,
                reason, captured_context_json
            FROM topology_parent_resolution
            WHERE publication_seq <= {watermark:UInt64}
            """ + (anchors is null ? string.Empty : "\n AND child_anchor IN ({anchors:Array(String)})")
            + "\n" + """
            ORDER BY child_anchor, child_fingerprint, publication_seq
            """ + (maxRows is null ? string.Empty : " LIMIT {read_limit:UInt32}" + ScopedReadSettings);
        command.AddParameter("watermark", watermark);
        if (anchors is not null) command.AddParameter("anchors", anchors.ToArray());
        if (maxRows is not null) command.AddParameter("read_limit", checked((uint)maxRows.Value + 1));
        Observe("parent-resolution", command,
            anchors is null ? ["watermark"] : ["watermark", "anchors", "read_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var seenVersions = new Dictionary<(string Anchor, string Fingerprint, ulong Sequence),
            (string Reason, string Context)>();
        var latest = new Dictionary<(string Anchor, string Fingerprint),
            (ulong Sequence, string Reason, string Context)>();
        var physicalRows = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (maxRows is not null && ++physicalRows > maxRows.Value)
                throw new IOException("Parent decision read exceeds its physical capacity.");
            var childAnchor = ReadExactField(reader.GetValue(0));
            var fingerprint = ReadExactField(reader.GetValue(1));
            var sequence = Convert.ToUInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            var version = (Reason: ReadExactField(reader.GetValue(3)),
                Context: ReadExactField(reader.GetValue(4)));
            if (seenVersions.TryGetValue((childAnchor, fingerprint, sequence), out var previous))
            {
                if (previous != version)
                    throw new TopologyObservedRepairUnavailableException();
                continue;
            }
            seenVersions.Add((childAnchor, fingerprint, sequence), version);
            if (!latest.TryGetValue((childAnchor, fingerprint), out var current)
                || sequence > current.Sequence)
                latest[(childAnchor, fingerprint)] = (sequence, version.Reason, version.Context);
        }
        var result = new List<TopologyParentResolution>(latest.Count);
        foreach (var row in latest.OrderBy(static item => item.Key.Anchor, StringComparer.Ordinal)
                     .ThenBy(static item => item.Key.Fingerprint, StringComparer.Ordinal))
        {
            var (childAnchor, fingerprint) = row.Key;
            var (_, reason, context) = row.Value;
            var captured = JsonSerializer.Deserialize<TopologyParentResolution>(
                context, RawSignalCodec.Json)
                ?? throw new InvalidDataException("Published parent decision lacks captured context.");
            if (captured.ChildAnchor != childAnchor || captured.ChildFingerprint != fingerprint
                || captured.Reason != reason || string.IsNullOrWhiteSpace(captured.SourceId)
                || string.IsNullOrWhiteSpace(captured.OwnerGroup))
                throw new InvalidDataException("Published parent decision context is invalid.");
            result.Add(captured);
        }
        return result;
    }

    private async Task<IReadOnlyList<TopologyObservedConflictRow>> ReadScopedConflictsAsync(
        ulong watermark, TopologyObservedReadScope scope, IReadOnlyList<string> edgeAnchors,
        CancellationToken token)
    {
        // Operational readiness is global: an unattributed published marker
        // cannot be assigned to a scope by guessing current inventory. A
        // bounded existence probe does not materialize unrelated contexts.
        var legacy = await HasLegacyConflictAsync(watermark, token);
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        var restricted = !scope.Scope.IsUnrestricted;
        command.CommandText = """
            SELECT DISTINCT semantic_anchor FROM topology_span_conflicts
            WHERE publication_seq <= {watermark:UInt64}
                AND (semantic_anchor IN ({edge_anchors:Array(String)})
                    OR (candidate_context_json != ''
            """ + (restricted ? "\n" + """
                        AND arrayExists(candidate ->
                            JSONExtractString(candidate, 'owner_group') IN ({scope_groups:Array(String)}),
                            JSONExtractArrayRaw(candidate_context_json))
            """ : string.Empty) + "\n" + """
                        AND arrayExists(candidate ->
                            JSONExtractUInt(candidate, 'event_time_nano') >= {window_from:Decimal(21,0)}
                            AND JSONExtractUInt(candidate, 'event_time_nano') < {window_to:Decimal(21,0)},
                            JSONExtractArrayRaw(candidate_context_json))))
            ORDER BY semantic_anchor LIMIT {candidate_limit:UInt32}
            """ + ScopedReadSettings;
        command.AddParameter("watermark", watermark);
        command.AddParameter("edge_anchors", edgeAnchors.ToArray());
        if (restricted) command.AddParameter("scope_groups",
            scope.Scope.OwnerGroups.Where(static group => group != "_unassigned").ToArray());
        command.AddParameter("window_from", scope.WindowFromUnixNano);
        command.AddParameter("window_to", scope.WindowToUnixNano);
        command.AddParameter("candidate_limit", checked((uint)scope.MaxCandidates + 1));
        Observe("conflict-candidates", command,
            restricted ? ["watermark", "edge_anchors", "scope_groups", "window_from", "window_to", "candidate_limit"]
                : ["watermark", "edge_anchors", "window_from", "window_to", "candidate_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var anchors = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                anchors.Add(ReadString(reader.GetValue(0)));
                if (anchors.Count > scope.MaxCandidates)
                    throw new IOException("Topology conflict candidate read exceeds its physical capacity.");
            }
        var conflicts = await ReadConflictsAsync(watermark, token, anchors, scope.MaxCandidates);
        return legacy && !conflicts.Any(static row => row.Unattributed)
            ? [.. conflicts, new TopologyObservedConflictRow(string.Empty, [], true)] : conflicts;
    }

    private async Task<bool> HasLegacyConflictAsync(ulong watermark, CancellationToken token)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM topology_span_conflicts
            WHERE publication_seq <= {watermark:UInt64}
                AND (candidate_context_json = '' OR NOT isValidJSON(candidate_context_json)
                    OR length(JSONExtractArrayRaw(candidate_context_json)) = 0
                    OR arrayExists(candidate -> JSONExtractInt(candidate, 'context_version') != 3
                        OR trimBoth(JSONExtractString(candidate, 'anchor')) = ''
                        OR trimBoth(JSONExtractString(candidate, 'resolution_reason')) = ''
                        OR trimBoth(JSONExtractString(candidate, 'owner_group')) = ''
                        OR trimBoth(JSONExtractString(candidate, 'source_id')) = '',
                        JSONExtractArrayRaw(candidate_context_json)))
            LIMIT 1
            """ + ScopedReadSettings;
        command.AddParameter("watermark", watermark);
        Observe("conflict-readiness", command, ["watermark"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private async Task<IReadOnlyList<TopologyObservedConflictRow>> ReadConflictsAsync(ulong watermark,
        CancellationToken token, IReadOnlyList<string>? anchors = null, int? maxRows = null)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT semantic_anchor, publication_seq, first_fingerprint,
                conflicting_fingerprint, candidate_context_json
            FROM topology_span_conflicts
            WHERE publication_seq <= {watermark:UInt64}
            """ + (anchors is null ? string.Empty : "\n AND semantic_anchor IN ({anchors:Array(String)})")
            + "\n" + """
            ORDER BY semantic_anchor, publication_seq
            """ + (maxRows is null ? string.Empty : " LIMIT {read_limit:UInt32}" + ScopedReadSettings);
        command.AddParameter("watermark", watermark);
        if (anchors is not null) command.AddParameter("anchors", anchors.ToArray());
        if (maxRows is not null) command.AddParameter("read_limit", checked((uint)maxRows.Value + 1));
        Observe("conflicts", command, anchors is null ? ["watermark"]
            : ["watermark", "anchors", "read_limit"]);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new Dictionary<string, List<TopologyConflictCandidate>>(StringComparer.Ordinal);
        var seenVersions = new Dictionary<(string Anchor, ulong Sequence),
            (string First, string Conflicting, string Context)>();
        var legacy = new HashSet<string>(StringComparer.Ordinal);
        var rows = 0;
        var expandedCandidates = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (maxRows is not null && ++rows > maxRows.Value)
                throw new IOException("Topology conflict read exceeds its physical capacity.");
            var anchor = ReadString(reader.GetValue(0));
            var sequence = Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            var version = (First: ReadExactField(reader.GetValue(2)),
                Conflicting: ReadExactField(reader.GetValue(3)),
                Context: ReadExactField(reader.GetValue(4)));
            if (seenVersions.TryGetValue((anchor, sequence), out var previous))
            {
                if (previous != version)
                    throw new TopologyObservedRepairUnavailableException();
                // Physical duplicate retries still count against the bounded
                // read, but contribute only one logical marker/context.
                continue;
            }
            seenVersions.Add((anchor, sequence), version);
            if (!result.TryGetValue(anchor, out var candidates)) result.Add(anchor, candidates = []);
            var context = version.Context;
            if (context.Length == 0) { legacy.Add(anchor); continue; }
            var parsed = JsonSerializer.Deserialize<TopologyConflictCandidate[]>(context, RawSignalCodec.Json)
                ?? throw new InvalidDataException("Published topology conflict context is missing.");
            if (parsed.Length == 0 || parsed.Any(candidate => candidate.ContextVersion != 3
                    || string.IsNullOrWhiteSpace(candidate.Anchor)
                    || string.IsNullOrWhiteSpace(candidate.ResolutionReason)
                    || string.IsNullOrWhiteSpace(candidate.OwnerGroup)
                    || string.IsNullOrWhiteSpace(candidate.SourceId)))
                legacy.Add(anchor);
            expandedCandidates = checked(expandedCandidates + parsed.Length);
            if (maxRows is not null && expandedCandidates > maxRows.Value)
                throw new IOException("Topology conflict context exceeds its physical capacity.");
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

    private void Observe(string route, DbCommand command, IReadOnlyList<string> names)
    {
        if (ObserveQuery is null) return;
        var sql = command.CommandText;
        var bound = names.Select(name =>
        {
            var parameter = command.Parameters.Cast<DbParameter>()
                .Single(candidate => candidate.ParameterName == name);
            return (Name: name, Value: parameter.Value
                ?? throw new InvalidDataException("Topology SQL parameter is missing."));
        }).ToArray();
        ObserveQuery(new TopologySqlPlan(route, sql, names)
        {
            ExplainAsync = async cancellationToken =>
            {
                await using var connection = context.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                using var explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN indexes=1 " + sql;
                explain.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
                foreach (var parameter in bound) explain.AddParameter(parameter.Name, parameter.Value);
                var output = new StringBuilder();
                await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    output.AppendLine(ReadString(reader.GetValue(0)));
                return output.ToString();
            },
        });
    }

    private static string ReadString(object value) => value switch
    {
        string text => text.TrimEnd('\0'),
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.TrimEnd('\0')
            ?? throw new InvalidDataException("Missing topology projection string."),
    };

    private static string ReadExactField(object value) => value switch
    {
        string text => text,
        byte[] bytes => new UTF8Encoding(false, true).GetString(bytes),
        _ => throw new InvalidDataException("Invalid topology conflict field type."),
    };
}
