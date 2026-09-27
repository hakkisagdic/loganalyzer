using Bizigo.Contracts;

namespace Bizigo.Query;

public partial interface IScopedQuery
{
    string CreateTelemetryCursor(TelemetryQuery query, AccessScope scope, ulong lastTime, string lastKey, bool summary = false) =>
        throw new NotSupportedException("Telemetry continuation is not configured.");
    Task<TelemetryCount> CountExcludedTelemetryInputsAsync(TelemetryInputWindow window, AccessScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Canonical telemetry input counts are not configured.");
    Task<TelemetryPage> SearchTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryPage> GetMetricPointAsync(string logicalId, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryPage> GetTraceAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> CountTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> CountOutOfScopeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetrySummaryPage> SummarizeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default);
    Task<TelemetryCount> GetTelemetryFeedAsync(TelemetrySignal signal, string? resourceId, AccessScope scope, CancellationToken cancellationToken = default);
}
