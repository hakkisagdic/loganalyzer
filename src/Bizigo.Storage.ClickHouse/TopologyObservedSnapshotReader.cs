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
/// Reads only published observed projections. The watermark predicate is
/// applied before argMax, so a newer unacknowledged retry cannot hide an older
/// committed row with the same logical edge ID.
/// </summary>
public sealed class TopologyObservedSnapshotReader(ClickHouseContext context)
{
    // LIMIT bounds materialized rows, not MergeTree work (DISTINCT/argMax can
    // scan many versions first). A server-side read cap makes that work fail
    // closed instead of silently consuming an unbounded physical snapshot.
    private const string ScopedReadSettings =
        " SETTINGS max_rows_to_read = 131072, max_bytes_to_read = 134217728, read_overflow_mode = 'throw'";

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
        => await ReadSnapshotCoreAsync(watermark, null, cancellationToken);

    public Task<TopologyObservedSnapshot> ReadScopedSnapshotAsync(ulong watermark,
        TopologyObservedReadScope scope, CancellationToken cancellationToken = default) =>
        ReadSnapshotCoreAsync(watermark, scope ?? throw new ArgumentNullException(nameof(scope)), cancellationToken);

    private async Task<TopologyObservedSnapshot> ReadSnapshotCoreAsync(ulong watermark,
        TopologyObservedReadScope? scope, CancellationToken cancellationToken)
    {
        // Reduce every committed version of each physically selected edge ID
        // before applying the final owner/window/expiry eligibility predicate.
        var candidateIds = scope is null ? null : await ReadCandidateIdsAsync(watermark, scope, cancellationToken);
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
            """ + (scope is null ? string.Empty : "\n" + """
                AND edge_id IN ({candidate_ids:Array(String)})
            """) + "\n" + """
            GROUP BY edge_id
            """ + (scope is not null && !scope.Scope.IsUnrestricted ? "\n" + """
            HAVING (argMax(parent_owner_group, publication_seq) IN ({scope_groups:Array(String)})
                OR argMax(child_owner_group, publication_seq) IN ({scope_groups:Array(String)}))
            """ : scope is null ? string.Empty : "\n HAVING 1")
                + (scope is null ? string.Empty : "\n" + """
                AND argMax(last_seen, publication_seq) >= {window_from:Decimal(21,0)}
                AND argMax(last_seen, publication_seq) < {window_to:Decimal(21,0)}
                AND argMax(expires_nano, publication_seq) > {expiry_clock:Decimal(21,0)}
            """) + (scope?.NodeId is null ? string.Empty : "\n" + """
                AND (argMax(from_node_id, publication_seq) = {node_id:String}
                    OR argMax(to_node_id, publication_seq) = {node_id:String})
            """) + (scope?.EdgeId is null ? string.Empty : "\n" + """
                AND edge_id = {edge_id:String}
            """) + "\n" + """
            ORDER BY edge_id
            """ + (scope is null ? string.Empty : " LIMIT {read_limit:UInt32}" + ScopedReadSettings);
        command.AddParameter("watermark", watermark);
        if (scope is not null)
        {
            command.AddParameter("candidate_ids", candidateIds!.ToArray());
            if (!scope.Scope.IsUnrestricted)
                command.AddParameter("scope_groups", scope.Scope.OwnerGroups
                    .Where(static group => group != "_unassigned").ToArray());
            command.AddParameter("window_from", scope.WindowFromUnixNano);
            command.AddParameter("window_to", scope.WindowToUnixNano);
            command.AddParameter("expiry_clock", scope.ExpiryReadClockUnixNano);
            command.AddParameter("read_limit", checked((uint)scope.MaxCandidates + 1));
            if (scope.NodeId is not null) command.AddParameter("node_id", scope.NodeId);
            if (scope.EdgeId is not null) command.AddParameter("edge_id", scope.EdgeId);
        }
        var bound = new List<string> { "watermark" };
        if (scope is not null)
        {
            bound.AddRange(["candidate_ids", "window_from", "window_to", "expiry_clock", "read_limit"]);
            if (!scope.Scope.IsUnrestricted) bound.Add("scope_groups");
            if (scope.NodeId is not null) bound.Add("node_id");
            if (scope.EdgeId is not null) bound.Add("edge_id");
        }
        Observe("observed-edges", command, bound);
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var rows = new List<TopologyObservedSnapshotRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var parent = ReadString(reader.GetValue(13));
            var child = ReadString(reader.GetValue(14));
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
            if (scope is not null && rows.Count > scope.MaxCandidates)
                throw new IOException("Observed topology read exceeds its physical capacity.");
        }
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
            SELECT DISTINCT edge_id FROM topology_edges_observed
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
