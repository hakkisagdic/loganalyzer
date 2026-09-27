using System.Globalization;

namespace Bizigo.Evidence;

public sealed class MetricThresholdRule
{
    public string StableId { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string MetricName { get; set; } = "";
    public string[] OwnerGroups { get; set; } = [];
    public string? ResourceId { get; set; }
    public string Unit { get; set; } = "";
    public string Statistic { get; set; } = "windowMean";
    public string Operator { get; set; } = "gte";
    // String configuration preserves thresholds above 2^53 exactly.
    public string Threshold { get; set; } = "0";
}

public sealed class MetricEvidenceOptions
{
    public int MinimumSamples { get; set; } = 5;
    public double ChangeFactor { get; set; } = 2;
    public MetricThresholdRule[] ThresholdRules { get; set; } = [];
    public ExactMetricNumber Factor => ExactMetricNumber.Parse(ChangeFactor.ToString("R", CultureInfo.InvariantCulture));
    public bool Valid()
    {
        if (MinimumSamples < 1 || !double.IsFinite(ChangeFactor) || ChangeFactor <= 1 || ThresholdRules is null
            || ThresholdRules.Any(r => r is null)
            || ThresholdRules.Select(r => r.StableId).Distinct(StringComparer.Ordinal).Count() != ThresholdRules.Length) return false;
        foreach (var rule in ThresholdRules)
        {
            if (string.IsNullOrWhiteSpace(rule.StableId) || rule.Version < 1 || string.IsNullOrWhiteSpace(rule.MetricName)
                || rule.OwnerGroups is not { Length: > 0 } || rule.OwnerGroups.Any(string.IsNullOrWhiteSpace)
                || rule.Statistic != "windowMean" || rule.Operator is not ("gt" or "gte" or "lt" or "lte")) return false;
            try { _ = ExactMetricNumber.Parse(rule.Threshold); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentNullException) { return false; }
        }
        return true;
    }
}
