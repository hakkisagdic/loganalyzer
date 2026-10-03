using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Evidence.Providers;

namespace Bizigo.UnitTests;

public sealed class TopologyFeedScopeStateTests
{
    private static readonly RcaWindow Window = new()
    {
        BaselineFrom = DateTimeOffset.UnixEpoch,
        BaselineTo = DateTimeOffset.UnixEpoch.AddMinutes(1),
        From = DateTimeOffset.UnixEpoch.AddMinutes(1),
        To = DateTimeOffset.UnixEpoch.AddMinutes(2),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_empty_feed_is_evaluated_empty_but_foreign_feed_is_never_fed(bool ancestor)
    {
        var authorized = AccessScope.ForGroups("authorized-A", ["A"]);
        var foreign = AccessScope.ForGroups("foreign-B", ["B"]);
        var seenScopes = new List<string>();
        var query = new RecordingScopedQuery
        {
            TelemetryFeed = (_, _, scope, _) =>
            {
                seenScopes.Add(scope.Subject);
                return Task.FromResult(scope.Subject == authorized.Subject
                    ? new TelemetryCount(TelemetryResultStatus.Empty, 0)
                    : new TelemetryCount(TelemetryResultStatus.NeverFed, null));
            },
        };
        // A foreign onset would be a privacy bug if a provider searched it
        // after the scoped feed gate reported NeverFed.
        query.Onsets.Add(new("A", "foreign-hidden-source", Window.From, 1, 0));
        IEvidenceProvider provider = ancestor ? new TopologyCommonAncestorProvider(query) : new TopologyGraphPathProvider(query);
        var hidden = await provider.GatherAsync(Window, foreign, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.NeverFed, hidden.Status);
        Assert.Equal("NotRun", hidden.Telemetry!.Evaluation);
        Assert.Empty(hidden.Items);
        Assert.Empty(query.CorrelationWindows);

        query.Onsets.Clear();
        var empty = await provider.GatherAsync(Window, authorized, GatherBudget.Default,
            TestContext.Current.CancellationToken);
        Assert.Equal(EvidenceStatus.Empty, empty.Status);
        Assert.Equal("Evaluated", empty.Telemetry!.Evaluation);
        Assert.Empty(empty.Items);
        Assert.Single(query.CorrelationWindows);
        Assert.Equal([foreign.Subject, authorized.Subject], seenScopes);
    }
}
