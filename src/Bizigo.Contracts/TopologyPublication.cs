namespace Bizigo.Contracts;

/// <summary>Only reads that can consume observed proof require the repaired ClickHouse projection.</summary>
public enum TopologyReadMode { DeclaredOnly, ObservedOrMixed }

/// <summary>Validated repair generation and canonical physical schema certificate.</summary>
public sealed record TopologyRepairReadStamp(long Generation, string CertificateDigest);

/// <summary>
/// The repair owner validates durable Ready phase/certificate and the current
/// canonical table UUIDs, engine and sort keys before returning a stamp.
/// Missing, repairing or downgraded storage must fail closed with a generic
/// observed-unavailable result; declared-only reads never call this gate.
/// </summary>
public interface ITopologyObservedRepairReadiness
{
    Task<TopologyRepairReadStamp> RequireReadyAsync(CancellationToken cancellationToken);
}

/// <summary>Generic public failure; no owner, anchor, table UUID or repair detail escapes.</summary>
public sealed class TopologyObservedRepairUnavailableException : Exception
{
    public TopologyObservedRepairUnavailableException() : base("Topology observed projection is unavailable.") { }
}

/// <summary>The revision captured around one public topology read.</summary>
public sealed record TopologyPublicationRevision(long PostgresEpoch, ulong ClickHouseWatermark)
{
    // Null only for a declared-only read. Record equality includes the stamp
    // so a repair phase/certificate change invalidates an in-flight read.
    public TopologyRepairReadStamp? RepairStamp { get; init; }
}

public interface ITopologyPublicationRevisionSource
{
    Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken);

    // Existing test doubles may keep the observed-only member, but declared
    // mode can never silently fall back to an observed/CH-dependent read.
    Task<TopologyPublicationRevision> ReadAsync(TopologyReadMode mode, CancellationToken cancellationToken) =>
        mode == TopologyReadMode.ObservedOrMixed ? ReadAsync(cancellationToken)
            : throw new NotSupportedException("Declared-only topology revision read is unavailable.");
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
