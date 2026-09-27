using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Evidence;

/// <summary>Exact rational decisions; display rounding never feeds policy comparison.</summary>
public readonly record struct ExactMetricNumber : IComparable<ExactMetricNumber>
{
    public BigInteger Numerator { get; }
    public BigInteger Denominator { get; }
    public ExactMetricNumber(BigInteger numerator, BigInteger denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (denominator < 0) { numerator = -numerator; denominator = -denominator; }
        var common = BigInteger.GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / common; Denominator = denominator / common;
    }
    public static ExactMetricNumber Zero => new(0, 1);
    public static ExactMetricNumber Integer(BigInteger number) => new(number, 1);
    public static ExactMetricNumber Parse(string text)
    {
        if (text.Length > 400) throw new FormatException("Metric numeric representation is unbounded.");
        var parts = text.Split(['e', 'E']);
        if (parts.Length > 2) throw new FormatException("Invalid number.");
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        if (exponent is < -324 or > 308) throw new FormatException("Nonfinite or unbounded exponent.");
        var mantissa = parts[0];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0) { exponent -= mantissa.Length - dot - 1; mantissa = mantissa.Remove(dot, 1); }
        var number = BigInteger.Parse(mantissa, CultureInfo.InvariantCulture);
        return exponent >= 0 ? new(number * BigInteger.Pow(10, exponent), 1)
            : new(number, BigInteger.Pow(10, -exponent));
    }
    public static ExactMetricNumber Json(JsonElement value) => Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
    public ExactMetricNumber Abs() => new(BigInteger.Abs(Numerator), Denominator);
    public int CompareTo(ExactMetricNumber other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);
    public static ExactMetricNumber operator +(ExactMetricNumber a, ExactMetricNumber b) => new(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static ExactMetricNumber operator -(ExactMetricNumber a, ExactMetricNumber b) => new(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator);
    public static ExactMetricNumber operator /(ExactMetricNumber a, ExactMetricNumber b) => new(a.Numerator * b.Denominator, a.Denominator * b.Numerator);
    public static ExactMetricNumber operator *(ExactMetricNumber a, ExactMetricNumber b) => new(a.Numerator * b.Numerator, a.Denominator * b.Denominator);
    public string NumeratorText => Numerator.ToString(CultureInfo.InvariantCulture);
    public string DenominatorText => Denominator.ToString(CultureInfo.InvariantCulture);
}

public sealed record MetricWindowSummary(string State, string Reason, string Unit, int RawCount,
    int SampleCount, ExactMetricNumber? Mean, string? PopulationCount = null);
public sealed record MetricChange(string State, string Reason, ExactMetricNumber? Ratio);

public static class MetricArithmetic
{
    private sealed record Point(ulong Time, ulong Start, ExactMetricNumber Value);

    public static MetricWindowSummary Summarize(IReadOnlyList<TelemetryRecord> records, int minimumSamples = 5)
    {
        if (minimumSamples < 1) throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        if (records.Count == 0) return new("InsufficientSamples", "NoSamples", "", 0, 0, null);
        var first = records[0];
        var counter = first.Kind == "Sum" && first.Monotonic && first.Temporality == 2;
        var unit = counter ? first.Unit + "/s" : first.Unit;
        MetricWindowSummary Invalid(string reason) => new("NotComparable", reason, unit, records.Count, 0, null);
        if (records.Any(r => r.Signal != TelemetrySignal.Metrics || r.SeriesKey != first.SeriesKey
            || r.Kind != first.Kind || r.Unit != first.Unit || r.Temporality != first.Temporality || r.Monotonic != first.Monotonic))
            return Invalid("MixedSeries");
        var distribution = first.Kind is "Histogram" or "ExponentialHistogram" or "Summary";
        var sum = ExactMetricNumber.Zero;
        var population = BigInteger.Zero;
        var count = 0;
        var points = new List<Point>();
        try
        {
            foreach (var row in records.DistinctBy(r => r.LogicalId).OrderBy(r => r.TimeUnixNano).ThenBy(r => r.LogicalId, StringComparer.Ordinal))
            {
                if (row.Metric is not { } metric) return Invalid("MissingMetric");
                var field = char.ToLowerInvariant(row.Kind[0]) + row.Kind[1..];
                if (!metric.TryGetProperty(field, out var shape) || !shape.TryGetProperty("dataPoints", out var data)
                    || data.GetArrayLength() != 1) return Invalid("MissingPoint");
                var point = data[0];
                if (distribution)
                {
                    if (!point.TryGetProperty("sum", out var total)) return Invalid("MissingSum");
                    if (!point.TryGetProperty("count", out var n)) return Invalid("EmptyDistribution");
                    var amount = ExactMetricNumber.Json(n);
                    if (amount.Denominator != 1 || amount.Numerator <= 0) return Invalid("EmptyDistribution");
                    sum += ExactMetricNumber.Json(total); population += amount.Numerator; count++;
                }
                else
                {
                    if (!point.TryGetProperty("asInt", out var v) && !point.TryGetProperty("asDouble", out v)) return Invalid("MissingValue");
                    var value = ExactMetricNumber.Json(v);
                    if (counter)
                    {
                        var start = point.TryGetProperty("startTimeUnixNano", out var s)
                            ? ulong.Parse(s.ValueKind == JsonValueKind.String ? s.GetString()! : s.GetRawText(), CultureInfo.InvariantCulture) : 0;
                        points.Add(new(row.TimeUnixNano, start, value));
                    }
                    else { sum += value; count++; }
                }
            }
            if (counter)
            {
                Point? previous = null;
                foreach (var point in points)
                {
                    if (previous is { } before)
                    {
                        if (point.Time == before.Time)
                        {
                            if (point.Start != before.Start || point.Value != before.Value) return Invalid("DuplicateTimestampConflict");
                            continue;
                        }
                        if (point.Start != before.Start || point.Value.CompareTo(before.Value) < 0) return Invalid("CounterReset");
                        sum += (point.Value - before.Value) * ExactMetricNumber.Integer(1_000_000_000)
                            / ExactMetricNumber.Integer(point.Time - before.Time);
                        count++;
                    }
                    previous = point;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or KeyNotFoundException)
        { return Invalid("NonFinite"); }
        if (count < minimumSamples) return new("InsufficientSamples", "MinimumSamples", unit, records.Count, count, null);
        return new("Evaluated", "WindowMean", unit, records.Count, count,
            sum / ExactMetricNumber.Integer(distribution ? population : count), distribution ? population.ToString(CultureInfo.InvariantCulture) : null);
    }

    public static MetricChange Compare(MetricWindowSummary baseline, MetricWindowSummary current, ExactMetricNumber factor)
    {
        if (factor.CompareTo(ExactMetricNumber.Integer(1)) <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        if (baseline.Mean is not { } b) return new(baseline.State, baseline.Reason, null);
        if (current.Mean is not { } e) return new(current.State, current.Reason, null);
        if (baseline.Unit != current.Unit) return new("NotComparable", "UnitMismatch", null);
        if (b.Numerator == 0) return new(e.Numerator == 0 ? "Unchanged" : "Changed", e.Numerator == 0 ? "BothZero" : "ZeroBaseline", null);
        if (e.Numerator == 0) return new("Changed", "ZeroEvent", ExactMetricNumber.Zero);
        if (b.Numerator.Sign != e.Numerator.Sign) return new("Changed", "SignChange", null);
        var ratio = (e / b).Abs();
        if (ratio.CompareTo(factor) >= 0) return new("Changed", "MagnitudeIncrease", ratio);
        if (ratio.CompareTo(ExactMetricNumber.Integer(1) / factor) <= 0) return new("Changed", "MagnitudeDecrease", ratio);
        return new("Unchanged", "WithinFactor", ratio);
    }

    public static bool Threshold(ExactMetricNumber mean, ExactMetricNumber threshold, string comparison) => comparison switch
    {
        "gt" => mean.CompareTo(threshold) > 0, "gte" => mean.CompareTo(threshold) >= 0,
        "lt" => mean.CompareTo(threshold) < 0, "lte" => mean.CompareTo(threshold) <= 0,
        _ => throw new ArgumentException("Invalid metric operator.", nameof(comparison)),
    };
}
