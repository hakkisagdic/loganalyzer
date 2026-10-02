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
    public async Task<T> ExecuteAsync<T>(
        Func<TopologyPublicationRevision, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var before = await revisions.ReadAsync(cancellationToken);
        Validate(before);
        var result = await operation(before, cancellationToken);
        var after = await revisions.ReadAsync(cancellationToken);
        Validate(after);
        if (before != after)
            throw new TopologyRestartRequiredException("Topology snapshot changed; restart the query.");
        return result;
    }

    private static void Validate(TopologyPublicationRevision revision)
    {
        if (revision.PostgresEpoch < 0)
            throw new InvalidDataException("Invalid topology PostgreSQL epoch.");
    }
}

/// <summary>The revision/expiry subset carried inside the signed opaque cursor.</summary>
public sealed record TopologyCursorRevision(long PostgresEpoch, ulong ClickHouseWatermark,
    DateTimeOffset ValidUntil)
{
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
        if (cursor.PostgresEpoch != current.PostgresEpoch
            || cursor.ClickHouseWatermark != current.ClickHouseWatermark
            || now >= cursor.ValidUntil)
            throw new TopologyRestartRequiredException("Topology cursor revision or expiry changed; restart the query.");
    }

    public static void EnsureCurrent(TopologyCursorRevision cursor,
        TopologyPublicationRevision current, decimal nowUnixNano)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(current);
        if (cursor.ExactValidUntilUnixNano is not decimal deadline || deadline < 0
            || cursor.PostgresEpoch != current.PostgresEpoch
            || cursor.ClickHouseWatermark != current.ClickHouseWatermark
            || nowUnixNano >= deadline)
            throw new TopologyRestartRequiredException("Topology cursor revision or expiry changed; restart the query.");
    }
}
