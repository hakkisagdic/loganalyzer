using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

[Table("source_ownership_history")]
public sealed class SourceOwnershipHistoryEntity
{
    [Key] public long Revision { get; set; }
    [MaxLength(128)] public required string SourceId { get; set; }
    [MaxLength(64)] public required string OwnerGroup { get; set; }
    [MaxLength(256)] public string? PeerAddress { get; set; }
    [MaxLength(256)] public string? Hostname { get; set; }
    public bool Enabled { get; set; }
    public decimal EffectiveFromNano { get; set; }
    public decimal? EffectiveToNano { get; set; }
}

[Table("telemetry_owner_claims")]
public sealed class TelemetryOwnerClaimEntity
{
    [Key] public Guid EnvelopeId { get; set; }
    [MaxLength(64)] public required string BindingHash { get; set; }
}

/// <summary>Reads a committed history snapshot, never treats database failure as no match.</summary>
public sealed class HistoricalTelemetryOwners(IDbContextFactory<ControlPlaneDbContext> factory)
    : ITelemetryOwnerResolver, ITelemetryBindingRegistry
{
    public async Task<TelemetryOwnerBinding[]> ResolveAsync(IReadOnlyList<TelemetryOwnershipRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lower = (decimal)requests.Min(r => r.EventTimeUnixNano);
        var upper = (decimal)requests.Max(r => r.EventTimeUnixNano);
        // One statement observes one committed revision set. No mutable cache can
        // turn a failed refresh into a permanent, falsely unassigned admission.
        var history = await db.SourceOwnershipHistory.AsNoTracking()
            .Where(h => h.EffectiveFromNano <= upper && (h.EffectiveToNano == null || h.EffectiveToNano > lower))
            .ToArrayAsync(cancellationToken);
        return requests.Select(request => Resolve(request, history)).ToArray();
    }

    public static TelemetryOwnerBinding Resolve(TelemetryOwnershipRequest request,
        IReadOnlyList<SourceOwnershipHistoryEntity> history)
    {
        var time = (decimal)request.EventTimeUnixNano;
        var relevant = history.Where(h => h.EffectiveFromNano <= time
            && (h.EffectiveToNano is null || h.EffectiveToNano > time)).ToArray();
        var (match, reason) = SourceDirectory.SelectTelemetry(request.Candidates, candidate =>
            relevant.Where(h => string.Equals(h.SourceId, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(h.PeerAddress, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(h.Hostname, candidate, StringComparison.OrdinalIgnoreCase)).ToArray(), h => h.Enabled);
        return match is null ? Unknown(request, reason)
            : new(request.LeafKey, match.SourceId, match.OwnerGroup, match.Revision, request.EventTimeUnixNano, reason);
    }

    private static TelemetryOwnerBinding Unknown(TelemetryOwnershipRequest request, string reason) =>
        new(request.LeafKey, request.Candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? "_unknown",
            OwnerGroups.Unassigned, 0, request.EventTimeUnixNano, reason);

    public async Task ClaimAsync(Guid envelopeId, string bindingHash, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (db.Database.IsNpgsql())
        {
            // The unique key arbitrates between processes before either can
            // publish a conflicting owner to ClickHouse. Claims have no TTL.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bizigo.telemetry_owner_claims (envelope_id,binding_hash)
                VALUES ({envelopeId},{bindingHash}) ON CONFLICT (envelope_id) DO NOTHING
                """, cancellationToken);
        }
        else if (!await db.TelemetryOwnerClaims.AnyAsync(c => c.EnvelopeId == envelopeId, cancellationToken))
        {
            db.TelemetryOwnerClaims.Add(new() { EnvelopeId = envelopeId, BindingHash = bindingHash });
            await db.SaveChangesAsync(cancellationToken);
        }
        var saved = await db.TelemetryOwnerClaims.AsNoTracking().SingleAsync(c => c.EnvelopeId == envelopeId, cancellationToken);
        if (!string.Equals(saved.BindingHash, bindingHash, StringComparison.Ordinal))
            throw new InvalidDataException("Conflicting immutable telemetry ownership binding.");
    }
}
