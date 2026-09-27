namespace Bizigo.Evidence;

/// <summary>Both review capture paths use the same explicit, versioned vocabulary.</summary>
public static class MissingEvidenceAnswers
{
    private static readonly string[] Kinds = ["Log", "Change", "Metric", "Trace", "Topology"];

    public static string[]? Validate(IReadOnlyList<string>? answer)
    {
        if (answer is null) return null;
        if (answer.Count > Kinds.Length || answer.Distinct(StringComparer.Ordinal).Count() != answer.Count
            || answer.Any(value => !Kinds.Contains(value, StringComparer.Ordinal)))
            throw new ReviewRejectedException("Missing evidence kinds must be distinct Log, Change, Metric, Trace or Topology values.");
        return Kinds.Where(kind => answer.Contains(kind, StringComparer.Ordinal)).ToArray();
    }
}

public sealed record MissingEvidenceQuality(long Total, long Measured, long MissingAny,
    long Log, long Change, long Metric, long Trace, long Topology)
{
    public long Unanswered => Total - Measured;
    public double? MissingAnyRatio => Ratio(MissingAny);
    public double? LogRatio => Ratio(Log);
    public double? ChangeRatio => Ratio(Change);
    public double? MetricRatio => Ratio(Metric);
    public double? TraceRatio => Ratio(Trace);
    public double? TopologyRatio => Ratio(Topology);
    private double? Ratio(long count) => Measured == 0 ? null : (double)count / Measured;
}
