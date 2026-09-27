using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

public sealed class MetricEvidenceProviderTests
{
    internal static TelemetryRecord Point(string value, ulong time, string kind = "Gauge", ulong start = 0,
        string? count = null, string? id = null)
    {
        var p = new Dictionary<string, object> { ["timeUnixNano"] = time.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (count is null) p["asInt"] = value;
        else { p["sum"] = value; p["count"] = count; }
        p["startTimeUnixNano"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var data = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        { [char.ToLowerInvariant(kind[0]) + kind[1..]] = new { dataPoints = new[] { p } } });
        return new(1, Guid.Empty, id ?? time.ToString(System.Globalization.CultureInfo.InvariantCulture), TelemetrySignal.Metrics,
            "hash", "binding", new("leaf", "source", "A", 1, time, "known"), time, "metric", "service", kind, "ms",
            kind == "Sum" ? 2 : 0, kind == "Sum", "", "", 0, "series", JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }), "", "", data, null);
    }
    private static MetricWindowSummary Summary(string value, int count = 5) =>
        MetricArithmetic.Summarize(Enumerable.Range(0, count).Select(i => Point(value, (ulong)i)).ToArray());

    [Theory]
    [InlineData("10", "19", "Unchanged", "WithinFactor")]
    [InlineData("10", "20", "Changed", "MagnitudeIncrease")]
    [InlineData("10", "5", "Changed", "MagnitudeDecrease")]
    [InlineData("10", "6", "Unchanged", "WithinFactor")]
    [InlineData("0", "0", "Unchanged", "BothZero")]
    [InlineData("0", "3", "Changed", "ZeroBaseline")]
    [InlineData("10", "0", "Changed", "ZeroEvent")]
    [InlineData("-10", "-20", "Changed", "MagnitudeIncrease")]
    [InlineData("-10", "10", "Changed", "SignChange")]
    public void Baseline_factor_inclusive_boundaries(string baseline, string current, string state, string reason)
    {
        var result = MetricArithmetic.Compare(Summary(baseline), Summary(current), ExactMetricNumber.Integer(2));
        Assert.Equal(state, result.State); Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void Each_window_requires_configured_sample_minimum()
    {
        var five = Summary("10"); var four = Summary("20", 4);
        Assert.Equal("InsufficientSamples", MetricArithmetic.Compare(four, five, ExactMetricNumber.Integer(2)).State);
        Assert.Equal("InsufficientSamples", MetricArithmetic.Compare(five, four, ExactMetricNumber.Integer(2)).State);
        Assert.Equal("Unchanged", MetricArithmetic.Compare(five, Summary("30"), ExactMetricNumber.Integer(4)).State);
    }

    [Fact]
    public void Threshold_uses_window_mean_and_effective_unit()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Point(i == 4 ? "50" : "0", (ulong)i)).ToArray();
        var summary = MetricArithmetic.Summarize(rows);
        Assert.Equal(ExactMetricNumber.Integer(10), summary.Mean);
        Assert.False(MetricArithmetic.Threshold(summary.Mean!.Value, ExactMetricNumber.Integer(20), "gte"));
        Assert.True(MetricArithmetic.Threshold(ExactMetricNumber.Integer(10), ExactMetricNumber.Integer(10), "gte"));
        Assert.False(MetricArithmetic.Threshold(ExactMetricNumber.Integer(10), ExactMetricNumber.Integer(10), "gt"));
    }

    [Fact]
    public void Ratio_and_threshold_preserve_large_integer_precision()
    {
        var exact = Summary("9007199254740993").Mean!.Value;
        Assert.Equal("9007199254740993", exact.NumeratorText);
        Assert.True(MetricArithmetic.Threshold(exact, ExactMetricNumber.Parse("9007199254740992"), "gt"));
        Assert.Equal("9223372036854775807", Summary("9223372036854775807").Mean!.Value.NumeratorText);
    }

    [Fact]
    public void Metric_projection_numeric_oracles_counter()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Point((i * 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (ulong)i * 1_000_000_000, "Sum")).ToArray();
        var s = MetricArithmetic.Summarize(rows);
        Assert.Equal(5, s.SampleCount); Assert.Equal(ExactMetricNumber.Integer(10), s.Mean); Assert.Equal("ms/s", s.Unit);
        Assert.Equal(s.Mean, MetricArithmetic.Summarize([.. rows, rows[2] with { LogicalId = "duplicate" }]).Mean);
        var conflict = rows[2] with { LogicalId = "conflict", Metric = Point("21", 2_000_000_000, "Sum").Metric };
        Assert.Equal("DuplicateTimestampConflict", MetricArithmetic.Summarize([.. rows, conflict]).Reason);
        var reset = rows.ToArray(); reset[3] = Point("30", 3_000_000_000, "Sum", 1);
        Assert.Equal("CounterReset", MetricArithmetic.Summarize(reset).Reason);
        var times = new ulong[] { 0, 1, 3, 4, 6, 7 };
        var irregular = rows.Select((r, i) => r with { TimeUnixNano = times[i] * 1_000_000_000 }).ToArray();
        Assert.Equal(ExactMetricNumber.Integer(8), MetricArithmetic.Summarize(irregular).Mean);
        Assert.Equal("InsufficientSamples", MetricArithmetic.Summarize(rows[..5]).State);
    }

    [Theory]
    [InlineData("Histogram")]
    [InlineData("ExponentialHistogram")]
    [InlineData("Summary")]
    public void Histogram_population_weight_is_not_mean_of_point_means(string kind)
    {
        var rows = Enumerable.Range(0, 5).Select(i => Point(i == 4 ? "500" : "0", (ulong)i, kind,
            count: i == 4 ? "100" : "1")).ToArray();
        var result = MetricArithmetic.Summarize(rows);
        Assert.Equal(new ExactMetricNumber(500, 104), result.Mean);
        Assert.Equal("104", result.PopulationCount); Assert.Equal(5, result.SampleCount);
        rows[0] = Point("0", 0, kind, count: "0");
        Assert.Equal("EmptyDistribution", MetricArithmetic.Summarize(rows).Reason);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Invalid_sample_cannot_be_discarded_to_produce_mean(string bad)
    {
        var rows = Enumerable.Range(0, 6).Select(i => Point(i == 5 ? bad : "10", (ulong)i)).ToArray();
        var result = MetricArithmetic.Summarize(rows);
        Assert.Equal("NotComparable", result.State); Assert.Equal("NonFinite", result.Reason); Assert.Null(result.Mean);
    }
}
