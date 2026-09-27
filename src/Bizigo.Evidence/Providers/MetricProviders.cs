using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.Options;

namespace Bizigo.Evidence.Providers;

public sealed class MetricBaselineProvider(IScopedQuery query, IOptions<MetricEvidenceOptions> policy,
    IOptions<TelemetryEvidenceOptions> limits) : IEvidenceProvider
{
    public string Id => "metrics.baseline";
    public EvidenceKind Kind => EvidenceKind.Metric;
    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.Value.TimeoutSeconds));
        var current = await TelemetryEvidenceReader.ReadAsync(query, TelemetrySignal.Metrics, window, scope, limits.Value, false, timeout.Token);
        if (current.Status == TelemetryResultStatus.Failed || current.Partial) return MetricProviderOutput.Incomplete(Id, current);
        if (window.To - window.From != window.BaselineTo - window.BaselineFrom)
            return MetricProviderOutput.Unavailable(Id, current.Status, "NotComparable", "WindowMismatch", policy.Value);
        var baseline = await TelemetryEvidenceReader.ReadAsync(query, TelemetrySignal.Metrics, window, scope, limits.Value, true, timeout.Token);
        if (baseline.Status == TelemetryResultStatus.Failed || baseline.Partial) return MetricProviderOutput.Incomplete(Id, baseline);
        var before = baseline.Records.GroupBy(TelemetryEvidenceReader.Series).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var after = current.Records.GroupBy(TelemetryEvidenceReader.Series).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var decisions = new List<TelemetryEvaluation>(); var items = new List<EvidenceItem>();
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(key) ?? [];
            var e = after.GetValueOrDefault(key) ?? [];
            var bm = MetricArithmetic.Summarize(b, policy.Value.MinimumSamples);
            var em = MetricArithmetic.Summarize(e, policy.Value.MinimumSamples);
            var change = MetricArithmetic.Compare(bm, em, policy.Value.Factor);
            var values = MetricProviderOutput.Values(window, policy.Value, em, bm);
            values["ratio_numerator"] = change.Ratio?.NumeratorText; values["ratio_denominator"] = change.Ratio?.DenominatorText;
            var decision = new TelemetryEvaluation(key, change.State, change.Reason, values); decisions.Add(decision);
            if (change.State == "Changed") items.Add(MetricProviderOutput.Item(Id, key, e[0], window, decision));
        }
        return MetricProviderOutput.Complete(Id, current.Status, decisions, items, budget.MaxItems, policy.Value);
    }
}

public sealed class MetricThresholdProvider(IScopedQuery query, IOptions<MetricEvidenceOptions> policy,
    IOptions<TelemetryEvidenceOptions> limits) : IEvidenceProvider
{
    public string Id => "metrics.threshold";
    public EvidenceKind Kind => EvidenceKind.Metric;
    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.Value.TimeoutSeconds));
        var current = await TelemetryEvidenceReader.ReadAsync(query, TelemetrySignal.Metrics, window, scope, limits.Value, false, timeout.Token);
        if (current.Status == TelemetryResultStatus.Failed || current.Partial) return MetricProviderOutput.Incomplete(Id, current);
        var narrowed = TelemetryEvidenceReader.Narrow(scope, window.OwnerGroups);
        var rules = policy.Value.ThresholdRules.Where(r => r.Enabled && r.OwnerGroups.Any(narrowed.Allows))
            .OrderBy(r => r.StableId, StringComparer.Ordinal).ToArray();
        if (rules.Length == 0) return MetricProviderOutput.Unavailable(Id, current.Status, "NoRule", "NoRule", policy.Value);
        var decisions = new List<TelemetryEvaluation>(); var items = new List<EvidenceItem>();
        foreach (var series in current.Records.GroupBy(TelemetryEvidenceReader.Series).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = series.ToArray(); var first = rows[0];
            var summary = MetricArithmetic.Summarize(rows, policy.Value.MinimumSamples);
            var matched = rules.Where(r => r.MetricName == first.Name && r.OwnerGroups.Contains(first.Owner.OwnerGroup, StringComparer.Ordinal)
                && (r.ResourceId is null || r.ResourceId == first.Owner.SourceId)).ToArray();
            if (matched.Length == 0)
            { decisions.Add(new(series.Key, "NoRule", "NoRule", new Dictionary<string, string?>())); continue; }
            foreach (var rule in matched)
            {
                var state = summary.State; var reason = summary.Reason;
                if (rule.Unit != summary.Unit) { state = "NotComparable"; reason = "UnitMismatch"; }
                var breach = state == "Evaluated" && MetricArithmetic.Threshold(summary.Mean!.Value, ExactMetricNumber.Parse(rule.Threshold), rule.Operator);
                var values = MetricProviderOutput.Values(window, policy.Value, summary);
                values["rule_id"] = rule.StableId; values["rule_version"] = rule.Version.ToString(CultureInfo.InvariantCulture);
                values["operator"] = rule.Operator; values["threshold"] = rule.Threshold;
                var decision = new TelemetryEvaluation(series.Key + "/" + rule.StableId, breach ? "Violated" : state, breach ? "ThresholdExceeded" : reason, values);
                decisions.Add(decision);
                if (breach) items.Add(MetricProviderOutput.Item(Id, decision.Key, first, window, decision));
            }
        }
        return MetricProviderOutput.Complete(Id, current.Status, decisions, items, budget.MaxItems, policy.Value);
    }
}

internal static class MetricProviderOutput
{
    internal static EvidenceSlice Unavailable(string id, TelemetryResultStatus feed, string state, string reason, MetricEvidenceOptions options) => new()
    {
        ProviderId = id, Kind = EvidenceKind.Metric, Status = EvidenceStatus.Unavailable, Detail = reason,
        Telemetry = new(feed, state, [new("window", state, reason, new Dictionary<string, string?>())], PolicyHash(options)),
    };
    internal static EvidenceSlice Incomplete(string id, TelemetryInputs inputs) => new()
    {
        ProviderId = id, Kind = EvidenceKind.Metric,
        Status = inputs.Status == TelemetryResultStatus.Failed ? EvidenceStatus.Failed : EvidenceStatus.Unavailable,
        Detail = inputs.Reason ?? "IncompleteWindow", Truncated = inputs.Partial,
        Telemetry = new(inputs.Status, inputs.Status == TelemetryResultStatus.Failed ? "Failed" : "NotComparable", []),
    };
    internal static EvidenceSlice Complete(string id, TelemetryResultStatus feed, List<TelemetryEvaluation> decisions,
        List<EvidenceItem> items, int limit, MetricEvidenceOptions options)
    {
        var unavailable = decisions.Any(d => d.State is "NoRule" or "NotComparable" or "InsufficientSamples");
        var state = items.Count > 0 ? EvidenceStatus.Gathered : unavailable ? EvidenceStatus.Unavailable
            : feed == TelemetryResultStatus.NeverFed ? EvidenceStatus.NeverFed : EvidenceStatus.Empty;
        var evaluation = decisions.Count == 0 ? feed == TelemetryResultStatus.NeverFed ? "NotRun" : "Evaluated"
            : decisions.All(d => d.State == "NoRule") ? "NoRule"
            : decisions.All(d => d.State == "InsufficientSamples") ? "InsufficientSamples"
            : unavailable ? "NotComparable" : "Evaluated";
        return new()
        {
            ProviderId = id, Kind = EvidenceKind.Metric, Status = state, Items = items.Take(limit).ToArray(),
            Truncated = items.Count > limit || unavailable, Detail = string.Join(",", decisions.Select(d => d.Reason).Distinct(StringComparer.Ordinal)),
            Telemetry = new(feed, evaluation, decisions, PolicyHash(options)),
        };
    }
    private static string PolicyHash(MetricEvidenceOptions options) => RawSignalEnvelope.Hash(JsonSerializer.SerializeToUtf8Bytes(options));
    internal static Dictionary<string, string?> Values(RcaWindow window, MetricEvidenceOptions options,
        MetricWindowSummary current, MetricWindowSummary? baseline = null) => new(StringComparer.Ordinal)
    {
        ["event_from"] = window.From.ToString("O", CultureInfo.InvariantCulture), ["event_to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
        ["baseline_from"] = window.BaselineFrom.ToString("O", CultureInfo.InvariantCulture), ["baseline_to"] = window.BaselineTo.ToString("O", CultureInfo.InvariantCulture),
        ["event_numerator"] = current.Mean?.NumeratorText, ["event_denominator"] = current.Mean?.DenominatorText,
        ["baseline_numerator"] = baseline?.Mean?.NumeratorText, ["baseline_denominator"] = baseline?.Mean?.DenominatorText,
        ["event_samples"] = current.SampleCount.ToString(CultureInfo.InvariantCulture), ["event_raw_count"] = current.RawCount.ToString(CultureInfo.InvariantCulture),
        ["baseline_samples"] = baseline?.SampleCount.ToString(CultureInfo.InvariantCulture), ["population_count"] = current.PopulationCount,
        ["unit"] = current.Unit, ["minimum_samples"] = options.MinimumSamples.ToString(CultureInfo.InvariantCulture),
        ["change_factor"] = options.ChangeFactor.ToString("R", CultureInfo.InvariantCulture),
    };
    internal static EvidenceItem Item(string id, string key, TelemetryRecord point, RcaWindow window, TelemetryEvaluation decision) => new(
        RawSignalEnvelope.Hash(System.Text.Encoding.UTF8.GetBytes(id + key)), id, EvidenceKind.Metric, window.From, 1,
        point.Name + ": " + decision.Reason,
        new Dictionary<string, string> { ["decision"] = JsonSerializer.Serialize(decision, BundleSerializer.Options),
            ["source_id"] = point.Owner.SourceId, ["owner_group"] = point.Owner.OwnerGroup,
            ["drilldown"] = "/v1/metrics?from_nano=" + TelemetryEvidenceReader.Nano(window.From).ToString(CultureInfo.InvariantCulture)
                + "&to_nano=" + TelemetryEvidenceReader.Nano(window.To).ToString(CultureInfo.InvariantCulture) + "&name=" + Uri.EscapeDataString(point.Name)
                + "&resource_id=" + Uri.EscapeDataString(point.Owner.SourceId) });
}
