using System.Data.Common;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// The production graph source. PostgreSQL supplies immutable node/declared
/// history; ClickHouse supplies only rows at the committed publication
/// watermark. The same two-store fence surrounds the entire assembly for REST
/// and RCA consumers of IScopedQuery.
/// </summary>
public sealed class TopologyGraphSnapshotSource(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyObservedSnapshotReader observed,
    TopologyPublicationFence fence) : ITopologyScopedGraphSnapshotSource
{
    private const int MaxPhysicalRows = 4096;
    public Action<TopologySqlPlan>? ObserveQuery { get; set; }

    public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
        ReadCoreAsync(publishedSequence, null, cancellationToken);

    public Task<TopologyGraphSnapshot> ReadScopedAsync(long? publishedSequence,
        TopologyGraphSnapshotReadRequest request, CancellationToken cancellationToken) =>
        ReadCoreAsync(publishedSequence, request ?? throw new ArgumentNullException(nameof(request)), cancellationToken);

    private Task<TopologyGraphSnapshot> ReadCoreAsync(long? publishedSequence,
        TopologyGraphSnapshotReadRequest? request, CancellationToken cancellationToken) =>
        fence.ExecuteAsync(async (revision, token) =>
        {
            if (revision.ClickHouseWatermark > long.MaxValue)
                throw new InvalidDataException("Topology publication sequence exceeds the graph projection range.");
            var committed = checked((long)revision.ClickHouseWatermark);
            if (publishedSequence is not null && publishedSequence.Value != committed)
                throw new TopologySnapshotUnavailableException(publishedSequence.Value);

            await using var db = await factory.CreateDbContextAsync(token);
            var nodeHistory = request is null
                ? await db.TopologyNodeHistory.AsNoTracking().ToArrayAsync(token)
                : await ReadScopedNodeHistoryAsync(db, request, token);
            var nodeIds = nodeHistory.Select(static history => history.NodeId).Distinct(StringComparer.Ordinal).ToArray();
            var currentNodes = request is null
                ? await db.TopologyNodes.AsNoTracking().ToDictionaryAsync(n => n.Id, token)
                : await db.TopologyNodes.AsNoTracking().Where(node => nodeIds.Contains(node.Id))
                    .ToDictionaryAsync(n => n.Id, token);
            IReadOnlyList<TopologyEdgeProjection> declaredEdges = request is not null
                && (request.IncludeNodes || request.Provenance == TopologyProvenance.Observed)
                ? [] : await ReadDeclaredHistoryAsync(db, committed, request, token);
            var observedSnapshot = request is not null && !request.IncludeObserved
                ? new TopologyObservedSnapshot([], [])
                : request is null
                    ? await observed.ReadSnapshotAsync(revision.ClickHouseWatermark, token)
                    : await observed.ReadScopedSnapshotAsync(revision.ClickHouseWatermark,
                        new TopologyObservedReadScope(request.Scope, request.WindowFromUnixNano,
                            request.WindowToUnixNano, request.ExpiryReadClockUnixNano,
                            MaxPhysicalRows, request.NodeId, request.EdgeId), token);
            var observedRows = observedSnapshot.Rows;

            var nodes = new List<TopologyNodeProjection>(nodeHistory.Length);
            foreach (var history in nodeHistory)
            {
                if (!currentNodes.TryGetValue(history.NodeId, out var identity))
                    throw new InvalidDataException("Topology node history has no stable identity.");
                nodes.Add(new(history.NodeId, identity.Kind, history.DisplayName, history.OwnerGroup,
                    history.Enabled, !history.Enabled, history.NodeVersion, history.FromNano, history.ToNano));
            }

            var edges = new List<TopologyEdgeProjection>(declaredEdges.Count + observedRows.Count);
            edges.AddRange(declaredEdges);
            var conflictedEdges = new List<TopologyEdgeProjection>();

            var evidence = new List<TopologyEvidenceReference>();
            foreach (var row in observedRows)
            {
                if (row.PublicationSequence > revision.ClickHouseWatermark)
                    throw new InvalidDataException("Uncommitted topology projection entered the public snapshot.");
                var projected = new TopologyEdgeProjection(row.Id, row.FromNodeId, row.ToNodeId, Relation(row.Relation),
                    Provenance(row.Provenance), row.Directed, row.Confidence, row.FromOwnerGroup, row.ToOwnerGroup,
                    Visibility(row.FromOwnerGroup, row.ToOwnerGroup), row.FirstSeenUnixNano, row.LastSeenUnixNano,
                    row.ExpiresUnixNano, checked((long)row.PublicationSequence),
                    checked((long)row.PublicationSequence), false);
                if (row.HasPublishedConflict) { conflictedEdges.Add(projected); continue; }
                edges.Add(projected);
                foreach (var occurrence in row.ParentOccurrenceIds.Distinct(StringComparer.Ordinal))
                    evidence.Add(new(row.Id, occurrence, row.TraceLogicalId, row.ParentSpanLogicalId,
                        row.ParentEventTimeUnixNano));
                foreach (var occurrence in row.ChildOccurrenceIds.Distinct(StringComparer.Ordinal))
                    evidence.Add(new(row.Id, occurrence, row.TraceLogicalId, row.SpanLogicalId,
                        row.EventTimeUnixNano));
            }

            var conflictCandidates = observedSnapshot.Conflicts.SelectMany(marker => marker.Candidates)
                .Where(static candidate => candidate.IsConflictedAnchor)
                .Select(candidate => new TopologyConflictProjection(candidate.OwnerGroup, candidate.NodeId,
                    candidate.EventTimeNano, candidate.TraceExpiryNano)
                {
                    SourceId = candidate.SourceId,
                    ResolutionReason = candidate.ResolutionReason,
                })
                .Distinct().ToArray();
            var conflictArcs = observedSnapshot.Conflicts.SelectMany(marker =>
            {
                var captured = marker.Candidates;
                return from child in captured
                       where child.ParentAnchor.Length != 0 && child.NodeId is not null
                           && child.ResolutionReason == "Resolved"
                       from parent in captured
                       where parent.Anchor == child.ParentAnchor && parent.NodeId is not null
                           && parent.ResolutionReason == "Resolved"
                       select new TopologyConflictArc(parent.NodeId!, child.NodeId!,
                           parent.OwnerGroup, child.OwnerGroup, child.EventTimeNano,
                           decimal.Min(parent.TraceExpiryNano,
                               decimal.Min(child.TraceExpiryNano, child.ObservedExpiryNano)));
            }).Distinct().ToArray();
            // A historical edge can identify its own published endpoint, but
            // cannot identify every alternative fingerprint/owner behind an
            // old context-free marker. No scoped readiness is claimed for it.
            var unattributed = observedSnapshot.Conflicts.Any(static marker => marker.Unattributed);
            return new TopologyGraphSnapshot(committed, edges)
            {
                Nodes = nodes,
                Evidence = evidence,
                ConflictedEdges = conflictedEdges,
                ConflictCandidates = conflictCandidates,
                ConflictArcs = conflictArcs,
                UnresolvedParents = observedSnapshot.ParentResolutions
                    .Where(static item => item.Reason != "Resolved")
                    .Select(item => new TopologyUnresolvedParentProjection(item.OwnerGroup, item.SourceId,
                        item.NodeId, item.Reason, item.ChildEventTimeNano, item.ChildExpiryNano))
                    .ToArray(),
                ObservedMigrationRequired = unattributed,
            };
        }, cancellationToken);

    private async Task<IReadOnlyList<TopologyEdgeProjection>> ReadDeclaredHistoryAsync(
        ControlPlaneDbContext db, long committed, TopologyGraphSnapshotReadRequest? request,
        CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            var scoped = request is not null;
            var restricted = scoped && !request!.Scope.IsUnrestricted;
            command.CommandText = """
                SELECT edge_id, from_node_id, to_node_id, relation, directed, provenance,
                    confidence, from_owner_group, to_owner_group, from_nano, to_nano,
                    edge_version, deleted_at
                FROM bizigo.topology_edge_declared_history
                """ + (scoped ? "\n" + """
                WHERE from_nano <= @state_clock
                    AND (to_nano IS NULL OR @state_clock < to_nano)
                """ : string.Empty)
                + (restricted ? "\n" + """
                    AND (from_owner_group = ANY(@scope_groups) OR to_owner_group = ANY(@scope_groups))
                """ : string.Empty)
                + (request?.Relation is null ? string.Empty : "\n AND relation = @relation")
                + (request?.NodeId is null ? string.Empty : "\n AND (from_node_id = @node_id OR to_node_id = @node_id)")
                + (request?.EdgeId is null ? string.Empty : Guid.TryParse(request.EdgeId, out _)
                    ? "\n AND edge_id = @edge_id" : "\n AND FALSE")
                + "\n" + """
                ORDER BY edge_id, from_nano
                """ + (scoped ? " LIMIT @read_limit" : string.Empty);
            if (request is not null)
            {
                if (restricted) AddParameter("scope_groups",
                    request.Scope.OwnerGroups.Where(static group => group != "_unassigned").ToArray());
                AddParameter("state_clock", request.DeclaredStateClockUnixNano ?? request.AsOfUnixNano);
                AddParameter("read_limit", MaxPhysicalRows + 1);
                if (request.Relation is not null) AddParameter("relation", RelationWire(request.Relation.Value));
                if (request.NodeId is not null) AddParameter("node_id", request.NodeId);
                if (request.EdgeId is not null && Guid.TryParse(request.EdgeId, out var edgeId))
                    AddParameter("edge_id", edgeId);
            }
            var bound = new List<string>();
            if (request is not null)
            {
                bound.AddRange(["state_clock", "read_limit"]);
                if (restricted) bound.Add("scope_groups");
                if (request.Relation is not null) bound.Add("relation");
                if (request.NodeId is not null) bound.Add("node_id");
                if (request.EdgeId is not null && Guid.TryParse(request.EdgeId, out _)) bound.Add("edge_id");
            }
            ObserveDeclared(command, bound);
            var result = new List<TopologyEdgeProjection>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var fromOwner = reader.GetString(7);
                var toOwner = reader.GetString(8);
                var fromNano = reader.GetDecimal(9);
                result.Add(new(reader.GetGuid(0).ToString("D"), reader.GetString(1), reader.GetString(2),
                    Relation(reader.GetString(3)), Provenance(reader.GetString(5)), reader.GetBoolean(4),
                    reader.GetDecimal(6), fromOwner, toOwner, Visibility(fromOwner, toOwner),
                    fromNano, fromNano, reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    committed, reader.GetInt64(11), !reader.IsDBNull(12)));
                if (scoped && result.Count > MaxPhysicalRows)
                    throw new IOException("Topology declared read exceeds its physical capacity.");
            }
            return result;

            void AddParameter(string name, object value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private void ObserveDeclared(DbCommand command, IReadOnlyList<string> names)
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
        ObserveQuery(new TopologySqlPlan("declared-edges", sql, names)
        {
            ExplainAsync = async cancellationToken =>
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    using var explain = db.Database.GetDbConnection().CreateCommand();
                    explain.CommandText = "EXPLAIN (FORMAT TEXT) " + sql;
                    foreach (var parameter in bound)
                    {
                        var replay = explain.CreateParameter();
                        replay.ParameterName = parameter.Name;
                        replay.Value = parameter.Value;
                        explain.Parameters.Add(replay);
                    }
                    var output = new StringBuilder();
                    await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        output.AppendLine(reader.GetString(0));
                    return output.ToString();
                }
                finally { await db.Database.CloseConnectionAsync(); }
            },
        });
    }

    private static async Task<TopologyNodeHistoryEntity[]> ReadScopedNodeHistoryAsync(
        ControlPlaneDbContext db, TopologyGraphSnapshotReadRequest request, CancellationToken token)
    {
        if (!request.IncludeNodes) return [];
        var query = db.TopologyNodeHistory.AsNoTracking().Where(history =>
            history.FromNano <= request.AsOfUnixNano
            && (history.ToNano == null || request.AsOfUnixNano < history.ToNano));
        if (!request.Scope.IsUnrestricted)
        {
            var owners = request.Scope.OwnerGroups.Where(static owner => owner != "_unassigned").ToArray();
            query = query.Where(history => owners.Contains(history.OwnerGroup));
        }
        if (request.NodeId is not null) query = query.Where(history => history.NodeId == request.NodeId);
        var rows = await query.OrderBy(history => history.NodeId).Take(MaxPhysicalRows + 1).ToArrayAsync(token);
        if (rows.Length > MaxPhysicalRows)
            throw new IOException("Topology node read exceeds its physical capacity.");
        return rows;
    }

    private static TopologyRelation Relation(string value) => value switch
    {
        "depends_on" => TopologyRelation.DependsOn,
        "contains" => TopologyRelation.Contains,
        "connects_to" => TopologyRelation.ConnectsTo,
        _ => throw new InvalidDataException("Unknown topology relation in persisted graph."),
    };

    private static string RelationWire(TopologyRelation relation) => relation switch
    {
        TopologyRelation.DependsOn => "depends_on",
        TopologyRelation.Contains => "contains",
        TopologyRelation.ConnectsTo => "connects_to",
        _ => throw new InvalidDataException("Unknown topology relation filter."),
    };

    private static TopologyProvenance Provenance(string value) => value switch
    {
        "declared" => TopologyProvenance.Declared,
        "observed" => TopologyProvenance.Observed,
        _ => throw new InvalidDataException("Unknown topology provenance in persisted graph."),
    };

    private static TopologyEdgeVisibility Visibility(string fromOwner, string toOwner) =>
        string.Equals(fromOwner, toOwner, StringComparison.Ordinal)
            ? TopologyEdgeVisibility.SameOwner : TopologyEdgeVisibility.CrossOwner;
}
