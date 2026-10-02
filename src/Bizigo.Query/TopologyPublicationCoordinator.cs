using System.Data.Common;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// Serializes CH publication with a session advisory lock and a durable PG
/// pending identity. A CH insert may outlive a crashed writer; its sequence is
/// never reassigned to a different identity before the original is replayed.
/// </summary>
public sealed class TopologyPublicationCoordinator(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyPublicationWatermarkReader watermarkReader,
    TopologyPublicationWatermarkWriter watermarkWriter) : ITopologyPublicationCoordinator
{
    private const long AdvisoryLockId = 735032;

    public async Task<ulong> PublishAsync(string publicationKey,
        Func<ulong, CancellationToken, Task> writeProjection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publicationKey);
        if (publicationKey.Length != 64 || publicationKey.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Publication key must be a lowercase SHA-256 hex digest.", nameof(publicationKey));
        ArgumentNullException.ThrowIfNull(writeProjection);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!db.Database.IsNpgsql())
            throw new NotSupportedException("Topology publication requires the PostgreSQL advisory-lock protocol.");
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await AdvisoryLockAsync(db.Database.GetDbConnection(), true, cancellationToken);
            try
            {
                return await PublishLockedAsync(db, publicationKey, writeProjection, cancellationToken);
            }
            finally
            {
                // Cancellation cannot release a pooled session lock. Always
                // unlock with an independent token before returning it to pool.
                await AdvisoryLockAsync(db.Database.GetDbConnection(), false, CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<ulong> PublishLockedAsync(ControlPlaneDbContext db, string key,
        Func<ulong, CancellationToken, Task> writeProjection, CancellationToken cancellationToken)
    {
        var committed = await watermarkReader.ReadAsync(cancellationToken);
        var state = await db.TopologyReadState.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => s.PublishedSequence).SingleOrDefaultAsync(cancellationToken);
        var published = checked((ulong)state);
        var pending = await ReadPendingAsync(db.Database.GetDbConnection(), cancellationToken);

        if (published == checked(committed + 1))
        {
            // PG committed but the CH acknowledgement was interrupted.
            await watermarkWriter.CommitAsync(published, cancellationToken);
            committed = published;
        }
        else if (published != committed)
            throw new InvalidDataException("PostgreSQL and ClickHouse topology publication state diverged.");

        var receipt = await ReadReceiptAsync(db.Database.GetDbConnection(), key, cancellationToken);
        if (receipt is not null)
        {
            if (receipt.Value > committed)
                throw new InvalidDataException("A topology publication receipt is ahead of the committed watermark.");
            if (pending is not null && pending.Key == key && pending.Sequence != receipt.Value)
                throw new InvalidDataException("Pending and committed topology publication identities disagree.");
            if (pending is not null && pending.Key == key)
                await DeletePendingAsync(db, pending, cancellationToken);
            return receipt.Value;
        }

        if (pending is not null && pending.Sequence <= committed)
        {
            await DeletePendingAsync(db, pending, cancellationToken);
            if (pending.Sequence == committed && pending.Key == key) return committed;
            pending = null;
        }

        ulong next;
        if (pending is not null)
        {
            if (pending.Sequence != checked(committed + 1))
                throw new InvalidDataException("Pending topology publication sequence is not contiguous.");
            if (pending.Key != key)
                throw new TopologyRestartRequiredException(
                    "A different topology publication is pending replay; retry after recovery.");
            next = pending.Sequence;
        }
        else
        {
            next = checked(committed + 1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bizigo.topology_publication_pending (id, publication_key, publication_sequence)
                VALUES (1, {key}, {checked((long)next)})
                """, cancellationToken);
        }

        // This callback must be idempotent for the same key and sequence.
        // Pending remains durable if it fails or the process exits here.
        await writeProjection(next, cancellationToken);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO bizigo.topology_read_state (id, epoch, published_sequence)
                VALUES (1, 0, 0) ON CONFLICT (id) DO NOTHING
                """, cancellationToken);
            var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE bizigo.topology_read_state
                SET epoch = epoch + 1, published_sequence = {checked((long)next)}
                WHERE id = 1 AND published_sequence = {checked((long)committed)}
                """, cancellationToken);
            if (updated != 1)
                throw new TopologyRestartRequiredException("Topology publication state changed during commit.");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bizigo.topology_publication_receipts (publication_key, publication_sequence)
                VALUES ({key}, {checked((long)next)})
                """, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // If ACK fails, the next call repairs it while holding the same lock.
        await watermarkWriter.CommitAsync(next, cancellationToken);
        await DeletePendingAsync(db, new(key, next), cancellationToken);
        return next;
    }

    private static async Task<PendingPublication?> ReadPendingAsync(DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT publication_key, publication_sequence FROM bizigo.topology_publication_pending WHERE id=1";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = new PendingPublication(reader.GetString(0), checked((ulong)reader.GetInt64(1)));
        if (await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("Ambiguous pending topology publication.");
        return result;
    }

    private static async Task<ulong?> ReadReceiptAsync(DbConnection connection, string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT publication_sequence FROM bizigo.topology_publication_receipts WHERE publication_key=@key";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : checked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task DeletePendingAsync(ControlPlaneDbContext db,
        PendingPublication pending, CancellationToken cancellationToken)
    {
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM bizigo.topology_publication_pending
            WHERE id=1 AND publication_key={pending.Key} AND publication_sequence={checked((long)pending.Sequence)}
            """, cancellationToken);
        if (deleted != 1)
            throw new InvalidDataException("Pending topology publication changed unexpectedly.");
    }

    private static async Task AdvisoryLockAsync(DbConnection connection, bool acquire,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = acquire
            ? $"SELECT pg_advisory_lock({AdvisoryLockId.ToString(CultureInfo.InvariantCulture)})"
            : $"SELECT pg_advisory_unlock({AdvisoryLockId.ToString(CultureInfo.InvariantCulture)})";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed record PendingPublication(string Key, ulong Sequence);
}
