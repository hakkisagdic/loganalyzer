using System.Text.Json;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;

namespace Bizigo.Api.Anomaly;

public sealed record AnomalyEvaluationResult(
    string Status,
    string? Reason,
    double? ObservedValue,
    double? BaselineValue,
    double? Deviation
);

public interface IAnomalyEvaluator
{
    Task<AnomalyEvaluationResult> EvaluateAsync(
        AnomalyPolicyEntity policy,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}

public sealed class AnomalyEvaluator(IScopedQuery query) : IAnomalyEvaluator
{
    private readonly IScopedQuery _query = query ?? throw new ArgumentNullException(nameof(query));

    public async Task<AnomalyEvaluationResult> EvaluateAsync(
        AnomalyPolicyEntity policy,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!AnomalySignals.IsValid(policy.Signal))
        {
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.Failed,
                "unsupported_signal",
                null, null, null);
        }

        var scope = AccessScope.ForGroups($"anomaly-evaluator:{policy.Id}", [policy.OwnerGroup]);
        var baselineStart = windowStart.AddSeconds(-policy.BaselineWindowSeconds);
        var baselineEnd = windowStart;

        try
        {
            return policy.Signal switch
            {
                AnomalySignals.LogEventCount => await EvaluateLogEventCountAsync(
                    policy, scope, baselineStart, baselineEnd, windowStart, windowEnd, cancellationToken),
                AnomalySignals.MetricSeriesSum => await EvaluateMetricSeriesSumAsync(
                    policy, scope, baselineStart, baselineEnd, windowStart, windowEnd, cancellationToken),
                AnomalySignals.TraceErrorRate => await EvaluateTraceErrorRateAsync(
                    policy, scope, baselineStart, baselineEnd, windowStart, windowEnd, cancellationToken),
                _ => new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, "unsupported_signal", null, null, null)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, "cancelled", null, null, null);
        }
        catch (TimeoutException)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, "timeout", null, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, "scope_error", null, null, null);
        }
        catch (Exception ex)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, $"query_error:{ex.GetType().Name}", null, null, null);
        }
    }

    private async Task<AnomalyEvaluationResult> EvaluateLogEventCountAsync(
        AnomalyPolicyEntity policy,
        AccessScope scope,
        DateTimeOffset baselineStart,
        DateTimeOffset baselineEnd,
        DateTimeOffset eventStart,
        DateTimeOffset eventEnd,
        CancellationToken cancellationToken)
    {
        var targetFilter = string.IsNullOrWhiteSpace(policy.Target)
            ? Array.Empty<FieldFilter>()
            : [FieldFilter.Eq("source_id", policy.Target)];

        // Check if ever fed
        var allTimeQuery = new EventQuery
        {
            From = DateTimeOffset.UnixEpoch,
            To = eventEnd,
            Filters = targetFilter
        };
        var allTimeCount = await _query.CountEventsAsync(allTimeQuery, scope, cancellationToken);
        if (allTimeCount == 0)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.NoSignal, "never_fed", 0, 0, 0);
        }

        // Count baseline
        var baseQuery = new EventQuery
        {
            From = baselineStart,
            To = baselineEnd,
            Filters = targetFilter
        };
        var baseCount = await _query.CountEventsAsync(baseQuery, scope, cancellationToken);

        // S22: min_samples check
        if (baseCount < policy.MinSamples)
        {
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.NoSignal,
                "insufficient_baseline",
                null,
                baseCount,
                null);
        }

        // Count event window
        var eventQuery = new EventQuery
        {
            From = eventStart,
            To = eventEnd,
            Filters = targetFilter
        };
        var eventCount = await _query.CountEventsAsync(eventQuery, scope, cancellationToken);

        return ComputeDecision(policy, eventCount, baseCount);
    }

    private async Task<AnomalyEvaluationResult> EvaluateMetricSeriesSumAsync(
        AnomalyPolicyEntity policy,
        AccessScope scope,
        DateTimeOffset baselineStart,
        DateTimeOffset baselineEnd,
        DateTimeOffset eventStart,
        DateTimeOffset eventEnd,
        CancellationToken cancellationToken)
    {
        var baseFromNano = (decimal)(baselineStart.ToUnixTimeMilliseconds() * 1_000_000L);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);
        var eventFromNano = (decimal)(eventStart.ToUnixTimeMilliseconds() * 1_000_000L);
        var eventToNano = (decimal)(eventEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var baseTelemetryQuery = new TelemetryQuery
        {
            Signal = TelemetrySignal.Metrics,
            FromNano = baseFromNano,
            ToNano = baseToNano,
            Name = string.IsNullOrWhiteSpace(policy.Target) ? null : policy.Target,
            Limit = 1000
        };

        var basePage = await _query.SearchTelemetryAsync(baseTelemetryQuery, scope, cancellationToken);
        if (basePage.Status == TelemetryResultStatus.NeverFed)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.NoSignal, "never_fed", 0, 0, 0);
        }

        var baseRecords = basePage.Records;
        if (baseRecords.Count < policy.MinSamples)
        {
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.NoSignal,
                "insufficient_baseline",
                null,
                baseRecords.Count,
                null);
        }

        double baseSum = 0;
        foreach (var rec in baseRecords)
        {
            baseSum += ExtractMetricValue(rec);
        }

        var eventTelemetryQuery = new TelemetryQuery
        {
            Signal = TelemetrySignal.Metrics,
            FromNano = eventFromNano,
            ToNano = eventToNano,
            Name = string.IsNullOrWhiteSpace(policy.Target) ? null : policy.Target,
            Limit = 1000
        };
        var eventPage = await _query.SearchTelemetryAsync(eventTelemetryQuery, scope, cancellationToken);
        double eventSum = 0;
        foreach (var rec in eventPage.Records)
        {
            eventSum += ExtractMetricValue(rec);
        }

        return ComputeDecision(policy, eventSum, baseSum);
    }

    private async Task<AnomalyEvaluationResult> EvaluateTraceErrorRateAsync(
        AnomalyPolicyEntity policy,
        AccessScope scope,
        DateTimeOffset baselineStart,
        DateTimeOffset baselineEnd,
        DateTimeOffset eventStart,
        DateTimeOffset eventEnd,
        CancellationToken cancellationToken)
    {
        var baseFromNano = (decimal)(baselineStart.ToUnixTimeMilliseconds() * 1_000_000L);
        var baseToNano = (decimal)(baselineEnd.ToUnixTimeMilliseconds() * 1_000_000L);
        var eventFromNano = (decimal)(eventStart.ToUnixTimeMilliseconds() * 1_000_000L);
        var eventToNano = (decimal)(eventEnd.ToUnixTimeMilliseconds() * 1_000_000L);

        var baseTelemetryQuery = new TelemetryQuery
        {
            Signal = TelemetrySignal.Traces,
            FromNano = baseFromNano,
            ToNano = baseToNano,
            ServiceName = string.IsNullOrWhiteSpace(policy.Target) ? null : policy.Target,
            Limit = 1000
        };

        var basePage = await _query.SearchTelemetryAsync(baseTelemetryQuery, scope, cancellationToken);
        if (basePage.Status == TelemetryResultStatus.NeverFed)
        {
            return new AnomalyEvaluationResult(AnomalyRunStatuses.NoSignal, "never_fed", 0, 0, 0);
        }

        var baseRecords = basePage.Records;
        if (baseRecords.Count < policy.MinSamples)
        {
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.NoSignal,
                "insufficient_baseline",
                null,
                baseRecords.Count,
                null);
        }

        int baseErrors = baseRecords.Count(r => r.Status == 2);
        double baseRate = baseRecords.Count == 0 ? 0 : (double)baseErrors / baseRecords.Count;

        var eventTelemetryQuery = new TelemetryQuery
        {
            Signal = TelemetrySignal.Traces,
            FromNano = eventFromNano,
            ToNano = eventToNano,
            ServiceName = string.IsNullOrWhiteSpace(policy.Target) ? null : policy.Target,
            Limit = 1000
        };
        var eventPage = await _query.SearchTelemetryAsync(eventTelemetryQuery, scope, cancellationToken);
        int eventErrors = eventPage.Records.Count(r => r.Status == 2);
        double eventRate = eventPage.Records.Count == 0 ? 0 : (double)eventErrors / eventPage.Records.Count;

        return ComputeDecision(policy, eventRate, baseRate);
    }

    private static AnomalyEvaluationResult ComputeDecision(
        AnomalyPolicyEntity policy,
        double observedValue,
        double baselineValue)
    {
        // S22: Zero baseline check
        if (baselineValue == 0)
        {
            if (policy.ZeroBaselineMinAbsolute.HasValue)
            {
                if (observedValue >= policy.ZeroBaselineMinAbsolute.Value)
                {
                    return new AnomalyEvaluationResult(
                        AnomalyRunStatuses.Triggered,
                        null,
                        observedValue,
                        baselineValue,
                        observedValue);
                }
            }
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.NoSignal,
                "zero_baseline",
                observedValue,
                baselineValue,
                0);
        }

        // S20: normalized baseline comparison
        double windowRatio = (double)policy.EventWindowSeconds / policy.BaselineWindowSeconds;
        double normalizedBaseline = baselineValue * windowRatio;
        if (normalizedBaseline == 0)
        {
            normalizedBaseline = baselineValue;
        }

        double deviation = observedValue / normalizedBaseline;

        if (deviation >= policy.Sensitivity)
        {
            return new AnomalyEvaluationResult(
                AnomalyRunStatuses.Triggered,
                null,
                observedValue,
                normalizedBaseline,
                deviation);
        }

        return new AnomalyEvaluationResult(
            AnomalyRunStatuses.NoSignal,
            "within_threshold",
            observedValue,
            normalizedBaseline,
            deviation);
    }

    private static double ExtractMetricValue(TelemetryRecord rec)
    {
        if (!rec.Metric.HasValue) return 0;
        var el = rec.Metric.Value;

        if (TryReadNumeric(el, out var directVal)) return directVal;

        if (!string.IsNullOrWhiteSpace(rec.Kind))
        {
            var field = char.ToLowerInvariant(rec.Kind[0]) + rec.Kind[1..];
            if (el.TryGetProperty(field, out var shape) && shape.TryGetProperty("dataPoints", out var data))
            {
                if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
                {
                    if (TryReadNumeric(data[0], out var ptVal)) return ptVal;
                }
            }
        }

        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("dataPoints", out var dp))
                {
                    if (dp.ValueKind == JsonValueKind.Array && dp.GetArrayLength() > 0)
                    {
                        if (TryReadNumeric(dp[0], out var dpVal)) return dpVal;
                    }
                }
            }
        }

        return 0;
    }

    private static bool TryReadNumeric(JsonElement el, out double val)
    {
        val = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (el.TryGetProperty("as_double", out var d) || el.TryGetProperty("asDouble", out d))
        {
            if (d.ValueKind == JsonValueKind.Number) { val = d.GetDouble(); return true; }
            if (d.ValueKind == JsonValueKind.String && double.TryParse(d.GetString(), System.Globalization.CultureInfo.InvariantCulture, out val)) return true;
        }
        if (el.TryGetProperty("as_int", out var i) || el.TryGetProperty("asInt", out i))
        {
            if (i.ValueKind == JsonValueKind.Number) { val = i.GetDouble(); return true; }
            if (i.ValueKind == JsonValueKind.String && double.TryParse(i.GetString(), System.Globalization.CultureInfo.InvariantCulture, out val)) return true;
        }
        if (el.TryGetProperty("value", out var v))
        {
            if (v.ValueKind == JsonValueKind.Number) { val = v.GetDouble(); return true; }
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out val)) return true;
        }
        return false;
    }

}
