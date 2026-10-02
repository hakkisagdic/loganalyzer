using Bizigo.Contracts;
using Bizigo.Replay;

namespace Bizigo.UnitTests;

public sealed class TopologyRecoveryTests
{
    [Fact]
    public void Replay_is_order_duplicate_and_restart_deterministic()
    {
        var records = new[] { Record("2", Edge("b", 2)), Record("1", Edge("a", 1)), Record("1", Edge("a", 1)) };
        var expected = TopologyRecovery.Replay(records);
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var actual = TopologyRecovery.Replay(records.OrderBy(_ => random.Next()));
            Assert.Equal(expected, actual);
        }

        var archive = TopologyRecovery.CreateArchive("registry-7", 2, records.Take(1));
        var resumed = TopologyRecovery.Resume(archive, records);
        Assert.Equal(TopologyRecoveryStatus.Restored, resumed.Status);
        Assert.Equal(expected, resumed.Snapshot!.Edges);
    }

    [Fact]
    public void Duplicate_conflicts_fail_closed()
    {
        Assert.Throws<InvalidDataException>(() => TopologyRecovery.Replay(
            [Record("same", Edge("a", 1)), Record("same", Edge("b", 2))]));
        Assert.Throws<InvalidDataException>(() => TopologyRecovery.Replay(
            [Record("one", Edge("a", 1)), Record("two", Edge("a", 2))]));
    }

    private static TopologyRecoveryRecord Record(string occurrence, TopologyEdgeProjection edge) => new(occurrence, edge);
    private static TopologyEdgeProjection Edge(string id, long sequence) => new(id, $"from-{id}", $"to-{id}",
        TopologyRelation.DependsOn, TopologyProvenance.Observed, true, 0.7m, "A", "A",
        TopologyEdgeVisibility.SameOwner, 10, 20, 30, sequence, 1, false);
}
