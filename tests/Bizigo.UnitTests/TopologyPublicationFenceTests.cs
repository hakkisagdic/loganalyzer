using Bizigo.Contracts;
using Bizigo.Query;
using System.Globalization;

namespace Bizigo.UnitTests;

public sealed class TopologyPublicationFenceTests
{
    [Fact]
    public async Task Stable_two_store_revision_returns_the_operation_result()
    {
        var revision = Ready(7, 11);
        var source = new RevisionSource(revision, revision);
        var seen = default(TopologyPublicationRevision);

        var result = await new TopologyPublicationFence(source).ExecuteAsync((snapshot, _) =>
        {
            seen = snapshot;
            return Task.FromResult("stable");
        }, TestContext.Current.CancellationToken);

        Assert.Equal("stable", result);
        Assert.Equal(revision, seen);
        Assert.Equal(2, source.Reads);
    }

    [Theory]
    [InlineData(8, 11)]
    [InlineData(7, 12)]
    public async Task Pg_or_clickhouse_change_rejects_a_mixed_result(long epoch, ulong watermark)
    {
        var source = new RevisionSource(Ready(7, 11), Ready(epoch, watermark));
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() =>
            new TopologyPublicationFence(source).ExecuteAsync((_, _) => Task.FromResult("mixed"),
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(3, "ready-a")]
    [InlineData(2, "ready-b")]
    public async Task Repair_stamp_change_restarts_despite_stable_pg_epoch_and_ch_watermark(
        long generation, string certificate)
    {
        var source = new RevisionSource(Ready(7, 11), Ready(7, 11, generation, certificate));
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() =>
            new TopologyPublicationFence(source).ExecuteAsync((_, _) => Task.FromResult("stale"),
                TestContext.Current.CancellationToken));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void Cursor_revision_and_ttl_are_both_required()
    {
        var now = DateTimeOffset.Parse("2026-10-03T00:00:00Z", CultureInfo.InvariantCulture);
        var cursor = new TopologyCursorRevision(7, 11, now.AddSeconds(1))
        {
            RepairStamp = new(2, "ready-a"),
        };
        TopologyCursorFence.EnsureCurrent(cursor, Ready(7, 11), now);

        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, Ready(8, 11), now));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, Ready(7, 12), now));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, Ready(7, 11), cursor.ValidUntil));

        var nano = cursor with { ExactValidUntilUnixNano = 1001 };
        TopologyCursorFence.EnsureCurrent(nano, Ready(7, 11), 1000);
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, Ready(7, 11), 1001));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, Ready(7, 12), 1000));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, Ready(7, 11, 3), 1000));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, Ready(7, 11, 2, "ready-b"), 1000));
    }

    [Fact]
    public async Task Initial_repair_unavailable_is_503_class_but_midflight_loss_restarts()
    {
        var initial = new FaultingRevisionSource(false);
        await Assert.ThrowsAsync<TopologyObservedRepairUnavailableException>(() =>
            new TopologyPublicationFence(initial).ExecuteAsync((_, _) => Task.FromResult("unreachable"),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, initial.Reads);

        var midflight = new FaultingRevisionSource(true);
        var executed = false;
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() =>
            new TopologyPublicationFence(midflight).ExecuteAsync((_, _) =>
            {
                executed = true;
                return Task.FromResult("stale");
            }, TestContext.Current.CancellationToken));
        Assert.True(executed);
        Assert.Equal(2, midflight.Reads);
    }

    [Fact]
    public async Task Declared_only_fence_uses_pg_revision_without_observed_stamp()
    {
        var source = new DeclaredRevisionSource();
        var result = await new TopologyPublicationFence(source).ExecuteAsync(
            (revision, _) => Task.FromResult(revision), TopologyReadMode.DeclaredOnly,
            TestContext.Current.CancellationToken);
        Assert.Equal(new TopologyPublicationRevision(7, 11), result);
        Assert.Equal(2, source.DeclaredReads);
    }

    [Fact]
    public async Task Invalid_epoch_fails_closed_before_query_work()
    {
        var called = false;
        var source = new RevisionSource(Ready(-1, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new TopologyPublicationFence(source).ExecuteAsync((_, _) =>
            {
                called = true;
                return Task.FromResult(0);
            }, TestContext.Current.CancellationToken));
        Assert.False(called);
    }

    private sealed class RevisionSource(params TopologyPublicationRevision[] values)
        : ITopologyPublicationRevisionSource
    {
        private readonly Queue<TopologyPublicationRevision> remaining = new Queue<TopologyPublicationRevision>(values);
        public int Reads { get; private set; }

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (remaining.Count == 0) throw new InvalidOperationException("Unexpected revision read.");
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private static TopologyPublicationRevision Ready(long epoch, ulong watermark,
        long generation = 2, string certificate = "ready-a") =>
        new(epoch, watermark) { RepairStamp = new(generation, certificate) };

    private sealed class FaultingRevisionSource(bool readyFirst) : ITopologyPublicationRevisionSource
    {
        public int Reads { get; private set; }

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return readyFirst && Reads == 1 ? Task.FromResult(Ready(7, 11))
                : throw new TopologyObservedRepairUnavailableException();
        }
    }

    private sealed class DeclaredRevisionSource : ITopologyPublicationRevisionSource
    {
        public int DeclaredReads { get; private set; }

        public Task<TopologyPublicationRevision> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Observed revision must not be read.");

        public Task<TopologyPublicationRevision> ReadAsync(TopologyReadMode mode,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TopologyReadMode.DeclaredOnly, mode);
            DeclaredReads++;
            return Task.FromResult(new TopologyPublicationRevision(7, 11));
        }
    }
}
