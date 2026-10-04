using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// Combines the authoritative PG epoch with the committed CH watermark only
/// when observed proof is eligible. Declared-only reads remain PG-only while
/// the observed tables are under repair.
/// </summary>
public sealed class TopologyPublicationRevisionSource(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyPublicationWatermarkReader watermark,
    ITopologyObservedRepairReadiness? readiness = null) : ITopologyPublicationRevisionSource
{
    public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken) =>
        ReadAsync(TopologyReadMode.ObservedOrMixed, cancellationToken);

    public async Task<TopologyPublicationRevision> ReadAsync(
        TopologyReadMode mode, CancellationToken cancellationToken)
    {
        if (mode is not TopologyReadMode.DeclaredOnly and not TopologyReadMode.ObservedOrMixed)
            throw new ArgumentOutOfRangeException(nameof(mode));

        // The certificate check happens on both sides of the public fence.
        // A missing E0014 reader is never a silent readiness bypass.
        TopologyRepairReadStamp? stamp = null;
        if (mode == TopologyReadMode.ObservedOrMixed)
        {
            if (readiness is null) throw new TopologyObservedRepairUnavailableException();
            stamp = await readiness.RequireReadyAsync(cancellationToken);
            if (stamp is null || stamp.Generation < 0 || string.IsNullOrWhiteSpace(stamp.CertificateDigest))
                throw new TopologyObservedRepairUnavailableException();
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.TopologyReadState.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => new { s.Epoch, s.PublishedSequence }).SingleOrDefaultAsync(cancellationToken);
        var published = checked((ulong)(state?.PublishedSequence ?? 0));
        if (mode == TopologyReadMode.DeclaredOnly)
            return new(state?.Epoch ?? 0, published);

        var committed = await watermark.ReadAsync(cancellationToken);
        if (published != committed)
            throw new TopologyRestartRequiredException("Topology publication acknowledgement is incomplete; restart the query.");
        return new(state?.Epoch ?? 0, committed) { RepairStamp = stamp };
    }
}
