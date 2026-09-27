namespace Bizigo.Contracts;

/// <summary>Captured at durable admission, so later configuration changes cannot reset replay expiry.</summary>
public sealed record TelemetryRetentionPolicy(int Days = 90);

/// <summary>The server's immutable event-time ownership decision for one admitted leaf.</summary>
public sealed record TelemetryOwnerBinding(string LeafKey, string SourceId, string OwnerGroup,
    long HistoryRevision, ulong EventTimeUnixNano, string Reason);

public sealed record TelemetryOwnershipRequest(string LeafKey, ulong EventTimeUnixNano, string[] Candidates);

public interface ITelemetryOwnerResolver
{
    Task<TelemetryOwnerBinding[]> ResolveAsync(IReadOnlyList<TelemetryOwnershipRequest> requests,
        CancellationToken cancellationToken);
}

/// <summary>Claims are immutable across writers and independent of ClickHouse background merges.</summary>
public interface ITelemetryBindingRegistry
{
    Task ClaimAsync(Guid envelopeId, string bindingHash, CancellationToken cancellationToken);
}
