using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// Crash-repairable PG/CH publication protocol. CH projection rows are written
/// first and remain invisible. PG then records the publication under one
/// advisory lock; only after that commit does the contiguous CH watermark make
/// the rows public. A crash in the final gap is repaired before new work.
/// </summary>
public sealed class TopologyPublicationCoordinator(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyPublicationWatermarkReader watermarkReader,
    TopologyPublicationWatermarkWriter watermarkWriter) : ITopologyPublicationCoordinator
{
    public async Task<ulong> PublishAsync(Func<ulong, CancellationToken, Task> writeProjection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeProjection);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(735032)", cancellationToken);

        var state = await db.TopologyReadState.SingleOrDefaultAsync(s => s.Id == 1, cancellationToken);
        if (state is null)
        {
            state = new() { Id = 1 };
            db.TopologyReadState.Add(state);
        }

        var committed = await watermarkReader.ReadAsync(cancellationToken);
        var published = checked((ulong)state.PublishedSequence);
        if (published == checked(committed + 1))
        {
            // The prior writer committed PG and crashed before its CH ACK.
            await watermarkWriter.CommitAsync(published, cancellationToken);
            committed = published;
        }
        else if (published != committed)
            throw new InvalidDataException("PostgreSQL and ClickHouse topology publication state diverged.");

        var next = checked(committed + 1);
        await writeProjection(next, cancellationToken);
        state.PublishedSequence = checked((long)next);
        state.Epoch = checked(state.Epoch + 1);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        // If this throws, PG is intentionally ahead by exactly one. The next
        // call repairs that ACK before allocating another sequence.
        await watermarkWriter.CommitAsync(next, cancellationToken);
        return next;
    }
}
