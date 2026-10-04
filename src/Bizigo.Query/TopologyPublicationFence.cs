using Bizigo.Contracts;

namespace Bizigo.Query;

/// <summary>Mapped by the topology HTTP surface to 409/restart.</summary>
public sealed class TopologyRestartRequiredException(string message) : Exception(message);

/// <summary>
/// The single isolation fence for mixed PostgreSQL/ClickHouse graph reads. The
/// operation must read CH rows at or below the supplied committed watermark.
/// A concurrent owner/declared mutation or observed publication invalidates the
/// whole result instead of returning a mixed 200 response.
/// </summary>
public sealed class TopologyPublicationFence(ITopologyPublicationRevisionSource revisions)
{
    public Task<T> ExecuteAsync<T>(
        Func<TopologyPublicationRevision, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(operation, TopologyReadMode.ObservedOrMixed, cancellationToken);

    public async Task<T> ExecuteAsync<T>(
        Func<TopologyPublicationRevision, CancellationToken, Task<T>> operation,
        TopologyReadMode mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var before = await revisions.ReadAsync(mode, cancellationToken);
        Validate(before, mode);
        var result = await operation(before, cancellationToken);
        TopologyPublicationRevision after;
        try
        {
            after = await revisions.ReadAsync(mode, cancellationToken);
            Validate(after, mode);
        }
        catch (TopologyObservedRepairUnavailableException)
        {
            // This query began with a Ready certificate. Losing it while the
            // operation ran is a revision change, not a successful stale read.
            throw new TopologyRestartRequiredException("Topology repair state changed; restart the query.");
        }
        if (before != after)
            throw new TopologyRestartRequiredException("Topology snapshot changed; restart the query.");
        return result;
    }

    private static void Validate(TopologyPublicationRevision revision, TopologyReadMode mode)
    {
        if (revision.PostgresEpoch < 0)
            throw new InvalidDataException("Invalid topology PostgreSQL epoch.");
        if (mode == TopologyReadMode.ObservedOrMixed)
        {
            if (revision.RepairStamp is null || revision.RepairStamp.Generation < 0
                || string.IsNullOrWhiteSpace(revision.RepairStamp.CertificateDigest))
                throw new TopologyObservedRepairUnavailableException();
        }
        else if (mode != TopologyReadMode.DeclaredOnly || revision.RepairStamp is not null)
            throw new InvalidDataException("Invalid declared-only topology revision.");
    }
}

/// <summary>The revision/expiry subset carried inside the signed opaque cursor.</summary>
public sealed record TopologyCursorRevision(long PostgresEpoch, ulong ClickHouseWatermark,
    DateTimeOffset ValidUntil)
{
    public TopologyReadMode ReadMode { get; init; } = TopologyReadMode.ObservedOrMixed;
    public TopologyRepairReadStamp? RepairStamp { get; init; }

    // Decimal nanoseconds retain the exact TTL boundary when evidence expiry
    // is finer than DateTimeOffset's 100 ns tick resolution.
    public decimal? ExactValidUntilUnixNano { get; init; }
}

public static class TopologyCursorFence
{
    public static void EnsureCurrent(TopologyCursorRevision cursor,
        TopologyPublicationRevision current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(current);
        if (!SameRevision(cursor, current)
            || now >= cursor.ValidUntil)
            throw new TopologyRestartRequiredException("Topology cursor revision or expiry changed; restart the query.");
    }

    public static void EnsureCurrent(TopologyCursorRevision cursor,
        TopologyPublicationRevision current, decimal nowUnixNano)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(current);
        if (cursor.ExactValidUntilUnixNano is not decimal deadline || deadline < 0
            || !SameRevision(cursor, current)
            || nowUnixNano >= deadline)
            throw new TopologyRestartRequiredException("Topology cursor revision or expiry changed; restart the query.");
    }

    private static bool SameRevision(TopologyCursorRevision cursor, TopologyPublicationRevision current) =>
        cursor.PostgresEpoch == current.PostgresEpoch
        && cursor.ClickHouseWatermark == current.ClickHouseWatermark
        && cursor.RepairStamp == current.RepairStamp
        && (cursor.ReadMode == TopologyReadMode.DeclaredOnly
            ? cursor.RepairStamp is null
            : cursor.ReadMode == TopologyReadMode.ObservedOrMixed && cursor.RepairStamp is not null);
}
