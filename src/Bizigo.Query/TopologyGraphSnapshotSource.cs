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
    TopologyPublicationFence fence) : ITopologyGraphSnapshotSource
{
    public Task<TopologyGraphSnapshot> ReadAsync(long? publishedSequence, CancellationToken cancellationToken) =>
        fence.ExecuteAsync(async (revision, token) =>
        {
            if (revision.ClickHouseWatermark > long.MaxValue)
                throw new InvalidDataException("Topology publication sequence exceeds the graph projection range.");
            var committed = checked((long)revision.ClickHouseWatermark);
            if (publishedSequence is not null && publishedSequence.Value != committed)
                throw new TopologySnapshotUnavailableException(publishedSequence.Value);

            await using var db = await factory.CreateDbContextAsync(token);
            var currentNodes = await db.TopologyNodes.AsNoTracking().ToDictionaryAsync(n => n.Id, token);
            var nodeHistory = await db.TopologyNodeHistory.AsNoTracking().ToArrayAsync(token);
            var declaredEdges = await ReadDeclaredHistoryAsync(db, committed, token);
            var observedRows = await observed.ReadAsync(revision.ClickHouseWatermark, token);

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

            var evidence = new List<TopologyEvidenceReference>();
            foreach (var row in observedRows)
            {
                if (row.PublicationSequence > revision.ClickHouseWatermark)
                    throw new InvalidDataException("Uncommitted topology projection entered the public snapshot.");
                edges.Add(new(row.Id, row.FromNodeId, row.ToNodeId, Relation(row.Relation),
                    Provenance(row.Provenance), row.Directed, row.Confidence, row.FromOwnerGroup, row.ToOwnerGroup,
                    Visibility(row.FromOwnerGroup, row.ToOwnerGroup), row.FirstSeenUnixNano, row.LastSeenUnixNano,
                    row.ExpiresUnixNano, checked((long)row.PublicationSequence),
                    checked((long)row.PublicationSequence), false));
                foreach (var occurrence in row.ParentOccurrenceIds.Distinct(StringComparer.Ordinal))
                    evidence.Add(new(row.Id, occurrence, row.TraceLogicalId, row.ParentSpanLogicalId,
                        row.ParentEventTimeUnixNano));
                foreach (var occurrence in row.ChildOccurrenceIds.Distinct(StringComparer.Ordinal))
                    evidence.Add(new(row.Id, occurrence, row.TraceLogicalId, row.SpanLogicalId,
                        row.EventTimeUnixNano));
            }

            return new TopologyGraphSnapshot(committed, edges)
            {
                Nodes = nodes,
                Evidence = evidence,
            };
        }, cancellationToken);

    private static async Task<IReadOnlyList<TopologyEdgeProjection>> ReadDeclaredHistoryAsync(
        ControlPlaneDbContext db, long committed, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT edge_id, from_node_id, to_node_id, relation, directed, provenance,
                    confidence, from_owner_group, to_owner_group, from_nano, to_nano,
                    edge_version, deleted_at
                FROM bizigo.topology_edge_declared_history
                ORDER BY edge_id, from_nano
                """;
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
            }
            return result;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static TopologyRelation Relation(string value) => value switch
    {
        "depends_on" => TopologyRelation.DependsOn,
        "contains" => TopologyRelation.Contains,
        "connects_to" => TopologyRelation.ConnectsTo,
        _ => throw new InvalidDataException("Unknown topology relation in persisted graph."),
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
