using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bizigo.ControlPlane;

/// <summary>Declared edge changes share the node inventory lock, history interval, epoch and audit transaction.</summary>
public sealed class TopologyEdgeRegistry(IDbContextFactory<ControlPlaneDbContext> factory, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public Task<TopologyDeclaredEdgeResult> CreateAsync(AccessScope scope, bool admin, TopologyDeclaredEdgeInput input,
        CancellationToken token) => WriteAsync(scope, admin, null, input, null, token);

    public Task<TopologyDeclaredEdgeResult> UpdateAsync(AccessScope scope, bool admin, Guid id,
        TopologyDeclaredEdgeInput input, CancellationToken token) => WriteAsync(scope, admin, id, input, null, token);

    public Task<TopologyDeclaredEdgeResult> DeleteAsync(AccessScope scope, bool admin, Guid id,
        long version, CancellationToken token) => WriteAsync(scope, admin, id, null, version, token);

    public static string? Validate(TopologyDeclaredEdgeInput input)
    {
        if (input is null || !TopologyEdgeRelations.Valid(input.Relation)
            || input.Provenance is not null and not "declared") return "Invalid topology relation or provenance.";
        try
        {
            _ = TopologyIdentity.Kind(input.FromNodeId);
            _ = TopologyIdentity.Kind(input.ToNodeId);
        }
        catch (ArgumentException) { return "Invalid topology endpoint."; }
        if (!TopologyEdgeRelations.ValidEndpoints(input.Relation,
                TopologyIdentity.Kind(input.FromNodeId), TopologyIdentity.Kind(input.ToNodeId)))
            return "Invalid contains hierarchy.";
        return null;
    }

    private async Task<TopologyDeclaredEdgeResult> WriteAsync(AccessScope scope, bool admin, Guid? id,
        TopologyDeclaredEdgeInput? input, long? deleteVersion, CancellationToken token)
    {
        if (!admin || scope.IsEmpty || string.IsNullOrWhiteSpace(scope.Subject))
            return new(403, Error: "Topology administration requires authorized scope.");
        if (id == Guid.Empty) return new(400, Error: "Invalid edge identity.");
        if (input is not null && Validate(input) is { } validation) return new(400, Error: validation);
        if (id is null && (input is null || input.Version is not null)
            || id is not null && input is null && deleteVersion is null)
            return new(400, Error: "Invalid edge mutation version.");
        long expected = 0;
        if (id is not null && (input is not null
                ? !long.TryParse(input.Version, NumberStyles.None, CultureInfo.InvariantCulture, out expected) || expected <= 0
                : deleteVersion is null or <= 0))
            return new(400, Error: "Expected positive edge version required.");
        if (input is null) expected = deleteVersion ?? 0;

        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(735031)", token);
            var edge = id is null ? null : await db.TopologyDeclaredEdges.SingleOrDefaultAsync(e => e.Id == id, token);
            if (id is not null && edge is null) return new(404, Error: "Edge not found.");
            if (edge is not null && edge.DeletedAt is not null) return new(409, Error: "Edge was deleted.");
            if (edge is not null && edge.Version != expected) return new(409, Error: "Edge version changed.");

            var involved = new[] { input?.FromNodeId, input?.ToNodeId, edge?.FromNodeId, edge?.ToNodeId }
                .Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
            var nodes = await db.TopologyNodes.Where(n => involved.Contains(n.Id)).ToDictionaryAsync(n => n.Id, token);
            if (nodes.Count != involved.Length || nodes.Values.Any(n => n.Deleted || !n.Enabled))
                return new(409, Error: "Topology endpoint unavailable.");
            if (nodes.Values.Any(n => !scope.Allows(n.OwnerGroup))
                || edge is not null && (!scope.Allows(edge.FromOwnerGroup) || !scope.Allows(edge.ToOwnerGroup)))
                return new(403, Error: "Endpoint outside authorized scope.");
            if (input is not null && (nodes[input.FromNodeId].Kind != TopologyIdentity.Kind(input.FromNodeId)
                || nodes[input.ToNodeId].Kind != TopologyIdentity.Kind(input.ToNodeId)))
                return new(409, Error: "Endpoint type changed.");
            if (input is not null && await db.TopologyDeclaredEdges.AnyAsync(e => e.Id != id && e.DeletedAt == null
                && e.FromNodeId == input.FromNodeId && e.ToNodeId == input.ToNodeId && e.Relation == input.Relation, token))
                return new(409, Error: "Declared edge already exists.");

            var created = edge is null;
            var nowTime = time.GetUtcNow();
            var now = decimal.Floor(TopologyIdentity.Nano(nowTime) / 1000) * 1000;
            TopologyDeclaredEdgeHistoryEntity? prior = null;
            if (edge is not null)
            {
                prior = await db.TopologyDeclaredEdgeHistory.SingleOrDefaultAsync(h => h.EdgeId == edge.Id && h.ToNano == null, token);
                if (prior is null || prior.EdgeVersion != edge.Version)
                    return new(503, Error: "Authoritative edge history unavailable.");
                now = Math.Max(now, prior.FromNano + 1000);
            }
            if (prior is not null)
            {
                if (db.Database.IsNpgsql())
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE bizigo.topology_edge_declared_history SET to_nano={now} WHERE revision={prior.Revision}", token);
                    db.Entry(prior).State = EntityState.Detached;
                }
                else prior.ToNano = now;
            }
            edge ??= new TopologyDeclaredEdgeEntity
            {
                Id = Guid.NewGuid(), FromNodeId = input!.FromNodeId, ToNodeId = input.ToNodeId,
                Relation = input.Relation, FromOwnerGroup = nodes[input.FromNodeId].OwnerGroup,
                ToOwnerGroup = nodes[input.ToNodeId].OwnerGroup, CreatedAt = nowTime, CreatedBy = scope.Subject,
                UpdatedAt = nowTime, UpdatedBy = scope.Subject,
            };
            if (created) db.TopologyDeclaredEdges.Add(edge);
            if (input is not null)
            {
                edge.FromNodeId = input.FromNodeId; edge.ToNodeId = input.ToNodeId; edge.Relation = input.Relation;
                edge.FromOwnerGroup = nodes[input.FromNodeId].OwnerGroup;
                edge.ToOwnerGroup = nodes[input.ToNodeId].OwnerGroup;
            }
            edge.Version++;
            edge.UpdatedAt = nowTime; edge.UpdatedBy = scope.Subject;
            if (input is null) edge.DeletedAt = nowTime;
            db.TopologyDeclaredEdgeHistory.Add(new()
            {
                EdgeId = edge.Id, FromNodeId = edge.FromNodeId, ToNodeId = edge.ToNodeId,
                Relation = edge.Relation, FromOwnerGroup = edge.FromOwnerGroup, ToOwnerGroup = edge.ToOwnerGroup,
                EdgeVersion = edge.Version, Directed = edge.Directed, Provenance = edge.Provenance,
                Confidence = edge.Confidence, DeletedAt = edge.DeletedAt, FromNano = now,
            });
            db.AuditLog.Add(new()
            {
                At = nowTime, Subject = scope.Subject, Resource = edge.Id.ToString("D", CultureInfo.InvariantCulture),
                Action = input is null ? "topology.edge.delete" : created ? "topology.edge.create" : "topology.edge.update",
                Scope = scope.IsUnrestricted ? "*" : string.Join(',', scope.OwnerGroups.Order(StringComparer.Ordinal)),
                Details = JsonSerializer.Serialize(new { edge.Version, edge.FromNodeId, edge.ToNodeId, edge.Relation }), RowCount = 1,
            });
            await db.AdvanceTopologyEpochAsync(token);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(input is null ? 204 : created ? 201 : 200, new(edge.Id, edge.FromNodeId, edge.ToNodeId,
                edge.Relation, edge.FromOwnerGroup, edge.ToOwnerGroup, edge.Directed, edge.Provenance,
                edge.Confidence, edge.Version.ToString(CultureInfo.InvariantCulture), edge.DeletedAt,
                now.ToString(CultureInfo.InvariantCulture), edge.CreatedAt, edge.UpdatedAt, edge.CreatedBy, edge.UpdatedBy));
        }
        catch (DbUpdateConcurrencyException) { return new(409, Error: "Edge version changed concurrently."); }
        catch (DbUpdateException) { return new(503, Error: "Registry or audit write unavailable."); }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException) { return new(504, Error: "Registry request timed out."); }
        catch (NpgsqlException) { return new(503, Error: "Registry store unavailable."); }
        catch (TimeoutException) { return new(504, Error: "Registry request timed out."); }
    }
}
