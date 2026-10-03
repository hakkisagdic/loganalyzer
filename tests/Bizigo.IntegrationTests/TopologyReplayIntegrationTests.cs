using Bizigo.Contracts;
using Bizigo.Replay;

namespace Bizigo.IntegrationTests;

public sealed class TopologyReplayIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Archive_only_restore()
    {
        var edge = Edge();
        var archive = TopologyRecovery.CreateArchive("registry-owner-snapshot", 41,
            [new("occurrence-1", edge)]);
        var result = TopologyRecovery.Restore(archive);
        Assert.Equal(TopologyRecoveryStatus.Restored, result.Status);
        Assert.Equal("registry-owner-snapshot", result.Snapshot!.RegistryRevision);
        Assert.Equal(41, result.Snapshot.PublishedSequence);
        Assert.Equal(edge, Assert.Single(result.Snapshot.Edges));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Snapshot_corruption_and_legacy()
    {
        var archive = TopologyRecovery.CreateArchive("registry-owner-snapshot", 41,
            [new("occurrence-1", Edge())]);
        Assert.Equal(TopologyRecoveryStatus.Missing, TopologyRecovery.Restore(null).Status);
        Assert.Equal(TopologyRecoveryStatus.Unsupported,
            TopologyRecovery.Restore(archive with { SchemaVersion = 1 }).Status);
        Assert.Equal(TopologyRecoveryStatus.Corrupt,
            TopologyRecovery.Restore(archive with { Checksum = new string('0', 64) }).Status);
    }

    private static TopologyEdgeProjection Edge() => new("edge-stable", "service:00000000-0000-0000-0000-000000000001",
        "service:00000000-0000-0000-0000-000000000002", TopologyRelation.DependsOn,
        TopologyProvenance.Observed, true, 0.7m, "owner-a", "owner-a", TopologyEdgeVisibility.SameOwner,
        10, 20, 30, 41, 1, false);
}
