namespace Bizigo.Contracts;

/// <summary>The two-store revision captured around one public topology read.</summary>
public sealed record TopologyPublicationRevision(long PostgresEpoch, ulong ClickHouseWatermark);

public interface ITopologyPublicationRevisionSource
{
    Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Serializes one observed projection publication. The callback writes every
/// CH row with the supplied sequence; it must be idempotent for crash replay.
/// </summary>
public interface ITopologyPublicationCoordinator
{
    /// <summary>
    /// The immutable batch identity that must be replayed before any newer
    /// cumulative projection may publish. Null means no unfinished batch.
    /// </summary>
    Task<string?> ReadPendingKeyAsync(CancellationToken cancellationToken);

    Task<ulong> PublishAsync(string publicationKey, Func<ulong, CancellationToken, Task> writeProjection,
        CancellationToken cancellationToken);
}
