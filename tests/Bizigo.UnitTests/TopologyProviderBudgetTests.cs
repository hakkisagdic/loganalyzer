using Bizigo.Evidence;

namespace Bizigo.UnitTests;

public sealed class TopologyProviderBudgetTests
{
    public static IEnumerable<object[]> NumericCases()
    {
        foreach (var provider in new[] { "topology.graph-path", "topology.common-ancestor" })
        foreach (var dimension in new[] { "node", "edge", "page", "byte" })
        foreach (var delta in new[] { -1, 0, 1 }) yield return [provider, dimension, delta];
    }

    [Theory, MemberData(nameof(NumericCases))]
    public void All_numeric_boundaries(string provider, string dimension, int delta)
    {
        Assert.Contains(provider, EvidenceRanking.RankedProviders);
        const int limit = 10;
        var budget = new TopologyProviderBudget(limit, limit, limit, limit);
        var values = new Dictionary<string, int> { ["node"] = limit, ["edge"] = limit, ["page"] = limit, ["byte"] = limit };
        values[dimension] += delta;
        var result = budget.Measure(values["node"], values["edge"], values["page"], values["byte"]);
        Assert.Equal(delta > 0 ? TopologyProviderCompleteness.Partial : TopologyProviderCompleteness.Complete, result);
    }

    public static IEnumerable<object[]> FailureCases()
    {
        foreach (var provider in new[] { "topology.graph-path", "topology.common-ancestor" })
        foreach (var outcome in new[] { "partial", "timeout", "exception", "caller-cancel" }) yield return [provider, outcome];
    }

    [Theory, MemberData(nameof(FailureCases))]
    public void Failure_and_cancel_truth_table(string provider, string outcome)
    {
        Assert.Contains(provider, EvidenceRanking.RankedProviders);
        if (outcome == "caller-cancel")
        {
            Assert.Throws<OperationCanceledException>(() => TopologyProviderFailure.Classify(outcome));
            return;
        }
        var result = TopologyProviderFailure.Classify(outcome);
        Assert.Equal(outcome == "partial" ? TopologyProviderCompleteness.Partial : TopologyProviderCompleteness.Failed, result);
        Assert.NotEqual(TopologyProviderCompleteness.Complete, result);
    }
}
