using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>Combines the authoritative PG epoch with the committed CH watermark.</summary>
public sealed class TopologyPublicationRevisionSource(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyPublicationWatermarkReader watermark) : ITopologyPublicationRevisionSource
{
    public async Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var state = await db.TopologyReadState.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => new { s.Epoch, s.PublishedSequence }).SingleOrDefaultAsync(cancellationToken);
        var committed = await watermark.ReadAsync(cancellationToken);
        var published = checked((ulong)(state?.PublishedSequence ?? 0));
        if (published != committed)
            throw new TopologyRestartRequiredException("Topology publication acknowledgement is incomplete; restart the query.");
        return new(state?.Epoch ?? 0, committed);
    }
}
