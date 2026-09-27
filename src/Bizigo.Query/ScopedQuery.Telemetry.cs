using System.Diagnostics;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Query;

public sealed partial class ScopedQuery
{
    private TelemetryReader Telemetry => telemetry ?? throw new InvalidOperationException("Telemetry data plane is not configured.");

    public Task<TelemetryPage> SearchTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("search", query.Signal, scope, query.OwnerGroups, QuerySummary(query),
            p => Telemetry.SearchAsync(query, p, cancellationToken));

    public Task<TelemetryPage> GetMetricPointAsync(string logicalId, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("detail", TelemetrySignal.Metrics, scope, null, "logical-id",
            p => Telemetry.MetricDetailAsync(logicalId, p, cancellationToken));

    public Task<TelemetryPage> GetTraceAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("detail", TelemetrySignal.Traces, scope, query.OwnerGroups, QuerySummary(query), p =>
        {
            if (query.Signal != TelemetrySignal.Traces || query.TraceId is null)
                throw new ArgumentException("Trace detail requires a trace ID and bounded trace query.", nameof(query));
            return Telemetry.SearchAsync(query, p, cancellationToken);
        });

    public Task<TelemetryCount> CountTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("count", query.Signal, scope, query.OwnerGroups, QuerySummary(query),
            p => Telemetry.CountAsync(query, p, token: cancellationToken));

    public Task<TelemetryCount> CountOutOfScopeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("outside-count", query.Signal, scope, null, QuerySummary(query),
            p => Telemetry.CountAsync(query, p, outside: true, token: cancellationToken));

    public Task<TelemetrySummaryPage> SummarizeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("summary", query.Signal, scope, query.OwnerGroups, QuerySummary(query),
            p => Telemetry.SummaryAsync(query, p, cancellationToken));

    public Task<TelemetryCount> GetTelemetryFeedAsync(TelemetrySignal signal, string? resourceId, AccessScope scope, CancellationToken cancellationToken = default) =>
        AuditedTelemetryAsync("feed", signal, scope, null, "resource-filter=" + (resourceId is not null),
            p => Telemetry.FeedAsync(signal, resourceId, p, cancellationToken));

    // Only types, range and filter presence enter audit, never user-provided
    // attribute text, raw leaves, credentials or another group's identifier.
    private static string QuerySummary(TelemetryQuery q) => JsonSerializer.Serialize(new
    {
        q.FromNano, q.ToNano, q.Limit, continuation = q.Cursor is not null,
        resource = q.ResourceId is not null, name = q.Name is not null, service = q.ServiceName is not null,
        trace = q.TraceId is not null, span = q.SpanId is not null, kind = q.Kind is not null, status = q.Status is not null,
    });

    private async Task<T> AuditedTelemetryAsync<T>(string action, TelemetrySignal signal, AccessScope scope,
        IReadOnlyList<string>? narrow, string summary, Func<ScopePredicate, Task<T>> operation) where T : ITelemetryResult
    {
        ArgumentNullException.ThrowIfNull(scope);
        var predicate = ScopePredicate.From(scope, narrow);
        var watch = Stopwatch.StartNew();
        var succeeded = false; long count = 0; var partial = false; var outcome = "failure";
        try
        {
            var result = await operation(predicate);
            succeeded = result.Status != TelemetryResultStatus.Failed;
            count = result.RowCount; partial = result.Partial; outcome = result.Status.ToString();
            return result;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            // A cancelled query still leaves one bounded audit attempt. Failure
            // here propagates, so the public call cannot succeed without audit.
            using var auditBudget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _audit.RecordAsync(new AuditRecord(scope.Subject, "telemetry." + signal + "." + action,
                signal.ToString(), Describe(scope, predicate), summary + ";outcome=" + outcome + ";partial=" + partial,
                count, (int)Math.Min(watch.ElapsedMilliseconds, int.MaxValue), succeeded), auditBudget.Token);
        }
    }
}
