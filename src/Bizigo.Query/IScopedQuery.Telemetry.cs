using Bizigo.Contracts;

namespace Bizigo.Query;

public partial interface IScopedQuery
{
    Task<TelemetryPage> SearchTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryPage> GetMetricPointAsync(string logicalId, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryPage> GetTraceAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> CountTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> CountOutOfScopeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetrySummaryPage> SummarizeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> GetTelemetryFeedAsync(TelemetrySignal signal, string? resourceId, AccessScope scope, CancellationToken cancellationToken = default);
}
