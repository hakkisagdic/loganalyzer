using System.Text;
using System.Text.Json.Nodes;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>Real files, WAL framing, archive and atomic result checkpoints; no containers.</summary>
public sealed class SignalDurabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "signal-test-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private readonly InMemoryObjectStore objects = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static byte[] Payload(string source = "missing") => Encoding.UTF8.GetBytes("""
        { "resourceMetrics": [{ "resource": {"attributes": [
          {"key":"bizigo.source_key","value":{"stringValue":"SOURCE"}},
          {"key":"owner_group","value":{"stringValue":"admin"}}]},
          "scopeMetrics": [{"metrics":[
            {"name":"temperature","gauge":{"dataPoints":[{"asInt":"42","timeUnixNano":"123"}]}},
            {"name":"","gauge":{"dataPoints":[{"asInt":"9"}]}}
          ]}]}] }
        """.Replace("SOURCE", source, StringComparison.Ordinal));

    private SignalIngest New(int capacity = 8, long max = 10000000, IWalDurability? durability = null,
        ISignalCheckpoints? checkpoints = null, IRawObjectStore? objectStore = null) => new(new(), new(factory), objectStore ?? objects,
        Options.Create(new SignalOptions { Directory = root, ChannelCapacity = capacity }),
        Options.Create(new WalOptions { Directory = root, MaxTotalBytes = max, FlushToDisk = false, RetryAfterSeconds = 7 }),
        Options.Create(new RawStoreOptions { SegmentRetention = TimeSpan.Zero }),
        NullLogger<WriteAheadLog>.Instance, checkpoints, durability);

    [Fact]
    public async Task Idle_service_reclaims_verified_expired_WAL_and_reopens_admission()
    {
        var options = Options.Create(new SignalOptions
        { Directory = root, ChannelCapacity = 8, RetryInterval = TimeSpan.FromMilliseconds(25) });
        var retention = TimeSpan.FromSeconds(2);
        using var ingest = new SignalIngest(new(), new(factory), objects, options,
            Options.Create(new WalOptions { MaxTotalBytes = 1500 }),
            Options.Create(new RawStoreOptions { SegmentRetention = retention }), NullLogger<WriteAheadLog>.Instance);
        using var service = new SignalIngestService(ingest, options, NullLogger<SignalIngestService>.Instance,
            new NonTopologyDurabilityGate());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        async Task UntilAsync(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(10, timeout.Token);
        }
        await service.StartAsync(timeout.Token);
        try
        {
            await UntilAsync(() => ingest.Ready);
            byte[] body = """{"resourceMetrics":[{"scopeMetrics":[{"metrics":[{"name":"retention","gauge":{"dataPoints":[{"asInt":"42"}]}}]}]}]}"""u8.ToArray();
            var accepted = 0;
            SignalAdmission result;
            do
            {
                result = await ingest.AcceptAsync(TelemetrySignal.Metrics, body, "application/json", timeout.Token);
                if (result.Status == 200) accepted++;
            } while (result.Status == 200 && accepted < 10);
            Assert.Equal(503, result.Status);
            Assert.InRange(accepted, 1, 2);
            await UntilAsync(() => Directory.Exists(Path.Combine(root, "processed")) &&
                Directory.GetFiles(Path.Combine(root, "processed"), "*.json").Length == accepted);
            var manifests = ingest.Archive.Manifests().ToArray();
            Assert.Equal(accepted, manifests.Length);
            Assert.True(ingest.Wal.TotalBytes > 0);
            Assert.True(manifests.All(m => m.VerifiedAt + retention > DateTimeOffset.UtcNow));
            Assert.Equal(503, (await ingest.AcceptAsync(TelemetrySignal.Metrics, body, "application/json", timeout.Token)).Status);
            // No new accepted work and no test call to SweepAsync: the running
            // production service itself must wake and release expired WAL.
            await UntilAsync(() => ingest.Wal.TotalBytes == 0);
            Assert.Equal(accepted, ingest.AcceptedBatches);
            Assert.Equal(accepted, ingest.Archive.Manifests().Count());
            Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, body, "application/json", timeout.Token)).Status);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(stop.Token);
        }
    }

    [Fact]
    public async Task Fsync_gate_blocks_ack_queue_and_counters_even_when_option_is_false()
    {
        var gate = new FlushGate();
        using var ingest = New(durability: gate);
        await ingest.RecoverAsync(Ct);
        var pending = ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        await Task.WhenAny(gate.Entered.Task, pending).WaitAsync(Ct);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, ingest.AcceptedBatches);
        Assert.False(ingest.Reader.TryRead(out _));
        gate.Release.TrySetResult();
        var result = await pending;
        Assert.Equal(200, result.Status);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(1, ingest.AcceptedBatches);
        Assert.True(ingest.Reader.TryRead(out var work));
        Assert.Equal(Payload(), work!.Envelope.Payload);
    }

    [Fact]
    public async Task Flush_failure_never_acknowledges_and_faults_writer()
    {
        using var ingest = New(durability: new BrokenFlush());
        await ingest.RecoverAsync(Ct);
        Assert.Equal(500, (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).Status);
        Assert.Equal(0, ingest.AcceptedBatches);
        Assert.NotNull(ingest.LastFailure);
        Assert.False(ingest.Ready);
        var length = ingest.Wal.TotalBytes;
        Assert.Equal(503, (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).Status);
        Assert.Equal(length, ingest.Wal.TotalBytes);
    }

    [Fact]
    public async Task Channel_full_rejects_before_wal_and_releases_after_consumption()
    {
        using var ingest = New(capacity: 1);
        await ingest.RecoverAsync(Ct);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).Status);
        var length = ingest.Wal.TotalBytes;
        var full = await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        Assert.Equal(503, full.Status);
        Assert.Equal(7, full.RetryAfterSeconds);
        Assert.Equal(length, ingest.Wal.TotalBytes);
        Assert.Equal(1, ingest.AcceptedBatches);
        Assert.Equal(1, ingest.RejectedFull);
        var work = await ingest.Reader.ReadAsync(Ct);
        await ingest.ProcessAsync(work, Ct);
        ingest.CompleteWork();
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).Status);
    }

    [Fact]
    public async Task Wal_full_reservation_does_not_leak_or_create_frames()
    {
        using var ingest = New(capacity: 1, max: 1);
        await ingest.RecoverAsync(Ct);
        for (var i = 0; i < 3; i++)
        {
            var result = await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
            Assert.Equal(503, result.Status);
            Assert.Contains("WAL", result.Error, StringComparison.Ordinal);
        }
        Assert.Equal(0, ingest.Wal.TotalBytes);
        Assert.Equal(3, ingest.RejectedFull);
    }

    [Fact]
    public async Task Cancel_before_append_releases_reservation()
    {
        using var ingest = New(capacity: 1);
        await ingest.RecoverAsync(Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.AcceptAsync(
            TelemetrySignal.Metrics, Payload(), "application/json", cancelled.Token));
        Assert.Equal(0, ingest.Wal.TotalBytes);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).Status);
    }

    [Fact]
    public async Task Restart_and_concurrent_replay_keep_identity_raw_decision_and_owner()
    {
        Guid id;
        using (var ingest = New())
        {
            await ingest.RecoverAsync(Ct);
            id = (await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct)).EnvelopeId!.Value;
        }
        using var reopened = New();
        await reopened.RecoverAsync(Ct);
        var manifest = Assert.Single(reopened.Archive.Manifests());
        var envelope = await reopened.Archive.ReadAsync(manifest, Ct);
        Assert.Equal(id, envelope.EnvelopeId);
        Assert.Equal(Payload(), envelope.Payload);
        Assert.Equal(Payload().Length, envelope.PayloadLength);
        Assert.Equal(RawSignalEnvelope.Hash(Payload()), envelope.PayloadSha256);
        Assert.Equal("application/json", envelope.ContentType);
        Assert.Equal(TimeSpan.Zero, envelope.ReceivedAt.Offset);
        Assert.Equal(1, envelope.RejectedCount);
        Assert.Single(envelope.AcceptedKeys);
        await Task.WhenAll(reopened.ReplayArchiveAsync(Ct), reopened.ReplayArchiveAsync(Ct));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "processed"), "*.json"));
        var output = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!;
        var leaf = Assert.Single(output["leaves"]!.AsArray())!;
        Assert.Equal(id.ToString("N") + "/m/0/0/0/0", leaf["logical_id"]!.GetValue<string>());
        Assert.Equal("_unassigned", leaf["owner_group"]!.GetValue<string>());
        Assert.Equal("42", leaf["metric"]!["gauge"]!["dataPoints"]![0]!["asInt"]!.GetValue<string>());
        Assert.Equal(1, output["rejected_count"]!.GetValue<int>());
        await reopened.SweepAsync(Ct);
        Assert.Empty(reopened.Wal.ListSealedSegments());
        await reopened.ReplayArchiveAsync(Ct);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "processed"), "*.json"));
    }

    [Fact]
    public async Task Identical_http_retries_are_distinct_envelopes()
    {
        using var ingest = New();
        await ingest.RecoverAsync(Ct);
        var a = await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        var b = await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        Assert.NotEqual(a.EnvelopeId, b.EnvelopeId);
        for (var i = 0; i < 2; i++) await ingest.ProcessAsync(await ingest.Reader.ReadAsync(Ct), Ct);
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "processed"), "*.json").Length);
    }

    [Fact]
    public async Task Stored_admission_tamper_cannot_resurrect_rejected_point()
    {
        using var ingest = New();
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        var work = await ingest.Reader.ReadAsync(Ct);
        var tampered = work.Envelope with { AcceptedKeys = [.. work.Envelope.AcceptedKeys, "m/0/0/1/0"], RejectedCount = 0 };
        Assert.Throws<InvalidDataException>(() => new OtlpTelemetryDecoder().Replay(tampered));
        Assert.Throws<InvalidDataException>(() => (work.Envelope with { Version = 20 }).Validate());
        Assert.Throws<InvalidDataException>(() => (work.Envelope with { PayloadLength = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (work.Envelope with { PayloadSha256 = new string('0', 64) }).Validate());
    }

    [Fact]
    public async Task Corrupt_archive_fails_replay_and_retention_then_wal_repairs_it()
    {
        using var ingest = New();
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        var work = await ingest.Reader.ReadAsync(Ct);
        await ingest.ProcessAsync(work, Ct);
        var manifest = Assert.Single(ingest.Archive.Manifests());
        await objects.PutAsync(manifest.ObjectKey, "broken"u8.ToArray(), Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => ingest.ReplayArchiveAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => ingest.SweepAsync(Ct));
        Assert.NotEmpty(ingest.Wal.ListSealedSegments());
        await ingest.ProcessAsync(work, Ct);
        await ingest.ReplayArchiveAsync(Ct);
        await ingest.SweepAsync(Ct);
        Assert.Empty(ingest.Wal.ListSealedSegments());
    }

    [Fact]
    public async Task Inventory_candidates_and_ambiguous_alias_never_trust_payload_owner()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.AddRange(new SourceEntity { SourceId = "one", OwnerGroup = "finance", Hostname = "shared" },
                new SourceEntity { SourceId = "two", OwnerGroup = "sales", Hostname = "shared" });
            await db.SaveChangesAsync(Ct);
        }
        var directory = new SourceDirectory(factory);
        await directory.RefreshAsync(Ct);
        Assert.Equal("finance", directory.ResolveTelemetry(["missing", "one", "two"]).OwnerGroup);
        Assert.Equal("sales", directory.ResolveTelemetry(["two", "one"]).OwnerGroup);
        Assert.Equal("_unassigned", directory.ResolveTelemetry(["shared", "one"]).OwnerGroup);
        using var ingest = New();
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload("one"), "application/json", Ct);
        await ingest.ProcessAsync(await ingest.Reader.ReadAsync(Ct), Ct);
        var output = JsonNode.Parse(File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(root, "processed")))))!;
        // Sprint03 user decision supersedes Sprint02 current-inventory replay:
        // timeUnixNano=123 predates this inventory creation, so it grants no owner.
        Assert.Equal("_unassigned", output["leaves"]![0]!["owner_group"]!.GetValue<string>());
        var now = checked((ulong)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
        var current = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload("one"))
            .Replace("\"123\"", "\"" + now.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal));
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, current, "application/json", Ct)).Status);
        var admitted = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal("finance", Assert.Single(admitted.Envelope.OwnerBindings!).OwnerGroup);
    }

    [Fact]
    public async Task Partially_archived_segment_is_retained_until_every_envelope_is_verified()
    {
        var store = new FailSecondPut(objects);
        using var ingest = New(objectStore: store);
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        var first = await ingest.Reader.ReadAsync(Ct);
        var second = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal(first.Segment, second.Segment);
        await ingest.ProcessAsync(first, Ct);
        await Assert.ThrowsAsync<IOException>(() => ingest.ProcessAsync(second, Ct));
        Assert.NotNull(ingest.LastFailure);
        await ingest.SweepAsync(Ct);
        Assert.Single(ingest.Wal.ListSealedSegments());
        Assert.Single(ingest.Archive.Manifests());
        await ingest.ProcessAsync(second, Ct);
        Assert.Null(ingest.LastFailure);
        await ingest.SweepAsync(Ct);
        Assert.Empty(ingest.Wal.ListSealedSegments());
        Assert.Equal(2, ingest.Archive.Manifests().Count());
    }

    [Fact]
    public async Task Cancelled_replay_reports_failure_preserves_checkpoint_and_can_retry()
    {
        using var ingest = New();
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, Payload(), "application/json", Ct);
        await ingest.ProcessAsync(await ingest.Reader.ReadAsync(Ct), Ct);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "processed")));
        var original = await File.ReadAllBytesAsync(path, Ct);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.ReplayArchiveAsync(cancellation.Token));
        Assert.NotNull(ingest.LastFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Null(ingest.LastFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
    }

    // This metrics-only WAL retention smoke does not certify topology storage.
    // It supplies an explicit test dependency; production has no absent-gate fallback.
    private sealed class NonTopologyDurabilityGate : ITopologyObservedRepairReadiness
    {
        public Task<TopologyRepairReadStamp> RequireReadyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TopologyRepairReadStamp(0, "non-topology-durability-test-only"));
        }
    }

    private sealed class FailSecondPut(IRawObjectStore inner) : IRawObjectStore
    {
        private int puts;
        public Task EnsureBucketAsync(CancellationToken cancellationToken = default) => inner.EnsureBucketAsync(cancellationToken);
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref puts) == 2 ? throw new IOException("second object failed") : inner.PutAsync(key, content, cancellationToken);
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
        public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);
    }

    private sealed class FlushGate : IWalDurability
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            stream.Flush(true);
        }
    }
    private sealed class BrokenFlush : IWalDurability
    {
        public ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken) => throw new IOException("Injected fsync failure");
    }
    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
