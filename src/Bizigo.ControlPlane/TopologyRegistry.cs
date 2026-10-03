using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bizigo.ControlPlane;

/// <summary>Authoritative node/alias writes share the inventory lock, transaction and audit commit.</summary>
public sealed class TopologyRegistry(IDbContextFactory<ControlPlaneDbContext> factory, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public Task<TopologyRegistryResult> CreateAsync(AccessScope scope, bool admin, TopologyNodeInput input, CancellationToken token) =>
        WriteAsync(scope, admin, null, input, null, token);

    public Task<TopologyRegistryResult> UpdateAsync(AccessScope scope, bool admin, string id, TopologyNodeInput input, CancellationToken token) =>
        WriteAsync(scope, admin, id, input, null, token);

    public Task<TopologyRegistryResult> DeleteAsync(AccessScope scope, bool admin, string id, long version, CancellationToken token) =>
        WriteAsync(scope, admin, id, null, version, token);

    public static string? Validate(TopologyNodeInput input)
    {
        if (!Enum.IsDefined(input.Kind) || string.IsNullOrWhiteSpace(input.DisplayName) || input.DisplayName.Length > 256
            || string.IsNullOrWhiteSpace(input.OwnerGroup) || input.OwnerGroup.Length > 64
            || input.OwnerGroup == OwnerGroups.Unassigned || input.Version is <= 0
            || input.Bindings is null || input.Bindings.Length > 64) return "Invalid topology node.";
        if (input.Kind == TopologyNodeKind.Source)
        {
            if (string.IsNullOrWhiteSpace(input.SourceId) || input.SourceId.Length > 128 || input.Bindings.Length != 0)
                return "Source nodes require an inventory key and cannot carry service aliases.";
        }
        else if (input.SourceId is not null) return "Only source nodes carry an inventory key.";
        if (input.Kind is TopologyNodeKind.Source or TopologyNodeKind.Network && input.Bindings.Length != 0)
            return "Only service and instance nodes carry aliases.";
        foreach (var alias in input.Bindings)
        {
            if (alias is null || string.IsNullOrWhiteSpace(alias.SourceId) || alias.SourceId.Length > 128
                || alias.ServiceNamespace is null || alias.ServiceNamespace.Length > 256
                || string.IsNullOrWhiteSpace(alias.ServiceName) || alias.ServiceName.Length > 256)
                return "Invalid authoritative alias.";
            if (input.Kind == TopologyNodeKind.Service && (alias.InstanceId is not null || alias.ServiceNodeId is not null))
                return "Service aliases cannot name an instance or another service.";
            if (input.Kind == TopologyNodeKind.ServiceInstance)
            {
                if (string.IsNullOrWhiteSpace(alias.InstanceId) || alias.InstanceId.Length > 256) return "Instance alias required.";
                try { if (TopologyIdentity.Kind(alias.ServiceNodeId!) != TopologyNodeKind.Service) return "Service identity required."; }
                catch (ArgumentException) { return "Service identity required."; }
            }
        }
        if (input.Bindings.Distinct().Count() != input.Bindings.Length) return "Duplicate alias in request.";
        if (input.Kind == TopologyNodeKind.ServiceInstance && input.Bindings
            .Select(a => (a.SourceId, a.ServiceNodeId, a.InstanceId)).Distinct().Count() != input.Bindings.Length)
            return "Duplicate instance key in request.";
        return null;
    }

    private async Task<TopologyRegistryResult> WriteAsync(AccessScope scope, bool admin, string? id,
        TopologyNodeInput? input, long? deleteVersion, CancellationToken token)
    {
        if (!admin || scope.IsEmpty || string.IsNullOrWhiteSpace(scope.Subject)) return new(403, Error: "Topology administration requires authorized scope.");
        var deleting = deleteVersion is not null;
        if (input is not null && Validate(input) is { } error) return new(400, Error: error);
        if (input is null && (!deleting || deleteVersion <= 0)) return new(400, Error: "Expected version required.");
        if (id is null && input!.Version is not null || id is not null && !deleting && input!.Version is null)
            return new(400, Error: "Create has no version; update requires the current version.");
        if (id is not null)
        {
            try { _ = TopologyIdentity.Kind(id); }
            catch (ArgumentException) { return new(400, Error: "Invalid node identity."); }
        }
        if (input is not null && !scope.Allows(input.OwnerGroup)) return new(403, Error: "Owner outside authorized scope.");
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(735031)", token);
            var node = id is null ? null : await db.TopologyNodes.SingleOrDefaultAsync(n => n.Id == id, token);
            if (id is not null && node is null) return new(404, Error: "Node not found.");
            if (node is not null && !scope.Allows(node.OwnerGroup)) return new(403, Error: "Node outside authorized scope.");
            if (node is not null && (node.Deleted || node.Version != (deleteVersion ?? input!.Version)))
                return new(409, Error: "Node version changed or node deleted.");
            if (node is not null && input is not null && (node.Kind != input.Kind || node.SourceId != input.SourceId))
                return new(400, Error: "Node type and inventory identity are immutable.");
            var aliases = input?.Bindings ?? [];
            var sourceIds = aliases.Select(a => a.SourceId).Append(input?.SourceId ?? node?.SourceId ?? "")
                .Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
            var sources = await db.Sources.Where(s => sourceIds.Contains(s.SourceId)).ToDictionaryAsync(s => s.SourceId, token);
            var sourceHistory = await db.SourceOwnershipHistory.Where(h => sourceIds.Contains(h.SourceId) && h.EffectiveToNano == null)
                .ToDictionaryAsync(h => h.SourceId, token);
            foreach (var sourceId in sourceIds)
            {
                if (!sources.TryGetValue(sourceId, out var source) || !sourceHistory.TryGetValue(sourceId, out var history))
                    return new(409, Error: "Authoritative source history is unavailable.");
                if (!scope.Allows(source.OwnerGroup)) return new(403, Error: "Binding source outside authorized scope.");
                if (history.OwnerGroup != source.OwnerGroup || source.Enabled != history.Enabled
                    || !source.Enabled && (aliases.Any(a => a.SourceId == sourceId) || input?.Enabled == true)
                    || input is not null && source.OwnerGroup != input.OwnerGroup)
                    return new(409, Error: "Binding source ownership or enabled state changed.");
            }
            if (id is null && input!.Kind == TopologyNodeKind.Source
                && await db.TopologyNodes.AnyAsync(n => n.SourceId == input.SourceId, token))
                return new(409, Error: "Inventory already has an immutable source node.");
            var created = node is null;
            node ??= new() { Id = TopologyIdentity.Node(input!.Kind, Guid.NewGuid()), Kind = input.Kind,
                SourceId = input.SourceId, DisplayName = input.DisplayName, OwnerGroup = input.OwnerGroup };
            var oldOwner = created ? null : node.OwnerGroup;
            var prior = created ? null : await db.TopologyNodeHistory.SingleOrDefaultAsync(h => h.NodeId == node.Id && h.ToNano == null, token);
            if (!created && prior is null) return new(503, Error: "Authoritative node history is unavailable.");
            var oldBindings = await db.TopologyBindings.Where(b => b.TargetNodeId == node.Id && b.ToNano == null).ToArrayAsync(token);
            // Removing an alias needs authority over its source too, not just its target.
            var oldSources = oldBindings.Select(b => b.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            var oldOwners = await db.Sources.Where(s => oldSources.Contains(s.SourceId)).ToArrayAsync(token);
            if (oldOwners.Length != oldSources.Length) return new(409, Error: "Binding source disappeared.");
            if (oldOwners.Any(s => !scope.Allows(s.OwnerGroup))) return new(403, Error: "Existing alias source outside authorized scope.");
            if (!deleting && node is { Version: long.MaxValue })
                return new(409, Error: "Node version is exhausted.");
            var now = decimal.Floor(TopologyIdentity.Nano(time.GetUtcNow()) / 1000) * 1000;
            if (prior is not null) now = Math.Max(now, prior.FromNano + 1000);
            if (oldBindings.Length != 0) now = Math.Max(now, oldBindings.Max(b => b.FromNano) + 1000);
            if (sourceHistory.Count != 0) now = Math.Max(now, sourceHistory.Values.Max(h => h.EffectiveFromNano) + 1000);
            foreach (var alias in aliases)
            {
                var service = alias.ServiceNodeId ?? node.Id;
                if (node.Kind == TopologyNodeKind.ServiceInstance)
                {
                    var parent = await db.TopologyNodes.SingleOrDefaultAsync(n => n.Id == service, token);
                    if (parent is null || parent.Deleted || !parent.Enabled) return new(409, Error: "Service unavailable.");
                    if (!scope.Allows(parent.OwnerGroup)) return new(403, Error: "Service outside authorized scope.");
                    if (parent.OwnerGroup != input!.OwnerGroup) return new(409, Error: "Service ownership differs.");
                    var parentHistory = await db.TopologyNodeHistory.SingleOrDefaultAsync(h => h.NodeId == service && h.ToNano == null, token);
                    if (parentHistory is null || parentHistory.OwnerGroup != parent.OwnerGroup || !parentHistory.Enabled)
                        return new(503, Error: "Authoritative service history unavailable.");
                    now = Math.Max(now, parentHistory.FromNano + 1000);
                    var serviceAliases = await db.TopologyBindings.Where(b => b.SourceId == alias.SourceId && b.ServiceNamespace == alias.ServiceNamespace
                        && b.ServiceName == alias.ServiceName && b.InstanceId == "" && b.ServiceNodeId == service && b.ToNano == null).ToArrayAsync(token);
                    if (serviceAliases.Length != 1) return new(409, Error: "Exactly one authoritative service alias required.");
                    now = Math.Max(now, serviceAliases[0].FromNano + 1000);
                }
                var instance = alias.InstanceId ?? "";
                var conflict = await db.TopologyBindings.AnyAsync(b => b.SourceId == alias.SourceId
                    && (instance == "" ? b.ServiceNamespace == alias.ServiceNamespace && b.ServiceName == alias.ServiceName : b.ServiceNodeId == service)
                    && b.InstanceId == instance && b.TargetNodeId != node.Id && b.ToNano == null, token);
                if (conflict) return new(409, Error: "Alias already identifies another node.");
            }
            // All validation precedes writes. Close old intervals before inserts;
            // EF does not know about filtered-unique-index ordering.
            if (prior is not null)
            {
                if (db.Database.IsNpgsql())
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bizigo.topology_node_history SET to_nano={now} WHERE revision={prior.Revision}", token);
                    db.Entry(prior).State = EntityState.Detached;
                }
                else prior.ToNano = now;
            }
            foreach (var binding in oldBindings)
            {
                if (!deleting && aliases.Any(a => SameAlias(a, binding))) continue;
                binding.ToNano = now;
            }
            foreach (var alias in aliases)
            {
                if (oldBindings.Any(b => SameAlias(alias, b))) continue;
                db.TopologyBindings.Add(new() { BindingId = Guid.NewGuid(), SourceId = alias.SourceId,
                    ServiceNamespace = alias.ServiceNamespace, ServiceName = alias.ServiceName,
                    ServiceNodeId = alias.ServiceNodeId ?? node.Id, InstanceId = alias.InstanceId ?? "", TargetNodeId = node.Id,
                    SourceHistoryRevision = sourceHistory[alias.SourceId].Revision, FromNano = now });
            }
            if (created) db.TopologyNodes.Add(node);
            // A terminal tombstone may retain MaxValue; no representable next
            // version exists, and a deleted node cannot be mutated again.
            if (node.Version < long.MaxValue) node.Version++;
            node.DisplayName = input?.DisplayName ?? node.DisplayName;
            node.OwnerGroup = input?.OwnerGroup ?? node.OwnerGroup;
            node.Enabled = !deleting && input!.Enabled;
            node.Deleted = deleting;
            if (oldOwner is not null && oldOwner != node.OwnerGroup)
                db.TopologyOwnerHistory.Add(new()
                {
                    NodeId = node.Id, OldOwner = oldOwner, NewOwner = node.OwnerGroup,
                    NodeVersion = node.Version, ChangedBy = scope.Subject, ChangedAt = time.GetUtcNow(),
                });
            db.TopologyNodeHistory.Add(new() { NodeId = node.Id, OwnerGroup = node.OwnerGroup, DisplayName = node.DisplayName,
                NodeVersion = node.Version, Enabled = node.Enabled, FromNano = now,
                SourceHistoryRevision = node.SourceId is not null ? sourceHistory[node.SourceId].Revision : null });
            db.AuditLog.Add(new() { At = time.GetUtcNow(), Subject = scope.Subject,
                Action = deleting ? "topology.node.delete" : created ? "topology.node.create" : "topology.node.update",
                Resource = node.Id, Scope = scope.IsUnrestricted ? "*" : string.Join(',', scope.OwnerGroups.Order(StringComparer.Ordinal)),
                Details = JsonSerializer.Serialize(new { node.Version, node.Kind, BindingCount = aliases.Length }), RowCount = 1 });
            await db.AdvanceTopologyEpochAsync(token);
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(deleting ? 204 : created ? 201 : 200, new(node.Id, node.Kind, node.DisplayName, node.OwnerGroup,
                node.Enabled, node.Deleted, node.Version, now.ToString(CultureInfo.InvariantCulture)));
        }
        catch (DbUpdateConcurrencyException) { return new(409, Error: "Registry changed concurrently."); }
        catch (DbUpdateException) { return new(503, Error: "Registry or audit write unavailable."); }
        catch (NpgsqlException ex) when (ex.InnerException is TimeoutException) { return new(504, Error: "Registry request timed out."); }
        catch (NpgsqlException) { return new(503, Error: "Registry store unavailable."); }
        catch (TimeoutException) { return new(504, Error: "Registry request timed out."); }
    }

    private static bool SameAlias(TopologyAliasInput input, TopologyBindingEntity binding) =>
        input.SourceId == binding.SourceId && (input.InstanceId is not null || input.ServiceNamespace == binding.ServiceNamespace && input.ServiceName == binding.ServiceName)
        && (input.ServiceNodeId ?? binding.TargetNodeId) == binding.ServiceNodeId && (input.InstanceId ?? "") == binding.InstanceId;
}
