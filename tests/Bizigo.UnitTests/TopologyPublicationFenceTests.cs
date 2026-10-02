using Bizigo.Contracts;
using Bizigo.Query;
using System.Globalization;

namespace Bizigo.UnitTests;

public sealed class TopologyPublicationFenceTests
{
    [Fact]
    public async Task Stable_two_store_revision_returns_the_operation_result()
    {
        var revision = new TopologyPublicationRevision(7, 11);
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
        var source = new RevisionSource(new(7, 11), new(epoch, watermark));
        await Assert.ThrowsAsync<TopologyRestartRequiredException>(() =>
            new TopologyPublicationFence(source).ExecuteAsync((_, _) => Task.FromResult("mixed"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Cursor_revision_and_ttl_are_both_required()
    {
        var now = DateTimeOffset.Parse("2026-10-03T00:00:00Z", CultureInfo.InvariantCulture);
        var cursor = new TopologyCursorRevision(7, 11, now.AddSeconds(1));
        TopologyCursorFence.EnsureCurrent(cursor, new(7, 11), now);

        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, new(8, 11), now));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, new(7, 12), now));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(cursor, new(7, 11), cursor.ValidUntil));

        var nano = cursor with { ExactValidUntilUnixNano = 1001 };
        TopologyCursorFence.EnsureCurrent(nano, new(7, 11), 1000);
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, new(7, 11), 1001));
        Assert.Throws<TopologyRestartRequiredException>(() =>
            TopologyCursorFence.EnsureCurrent(nano, new(7, 12), 1000));
    }

    [Fact]
    public async Task Invalid_epoch_fails_closed_before_query_work()
    {
        var called = false;
        var source = new RevisionSource(new TopologyPublicationRevision(-1, 0));
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
}
