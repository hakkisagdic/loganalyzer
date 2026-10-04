using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class SignalReplayReadinessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "replay-readiness-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private readonly InMemoryObjectStore objects = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SignalIngest Open(IRawObjectStore store) => new(new(), new(factory), store,
        Options.Create(new SignalOptions { Directory = root }), Options.Create(new WalOptions()),
        Options.Create(new RawStoreOptions()), NullLogger<WriteAheadLog>.Instance);

    private async Task<SignalArchiveManifest> Seed()
    {
        var payload = SignalDurabilityTests.Payload();
        var decoded = new OtlpTelemetryDecoder().Decode(TelemetrySignal.Metrics, payload, "application/json");
        var envelope = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Metrics, "application/json",
            DateTimeOffset.UtcNow, RawSignalEnvelope.Hash(payload), payload, 1,
            decoded.Accepted.Select(x => x.Key).ToArray(), decoded.RejectedCount);
        return await new SignalArchive(objects, root).ArchiveAsync(envelope, "archive-no-wal", Ct);
    }

    [Fact]
    public async Task Corrupt_archive_blocks_admission_until_full_repaired_replay_and_keeps_legacy_unknown()
    {
        var manifest = await Seed();
        var original = await objects.GetAsync(manifest.ObjectKey, Ct);
        Assert.NotNull(original);
        using var ingest = Open(objects);
        await ingest.RecoverAsync(Ct);
        Assert.True(ingest.Ready);
        await objects.PutAsync(manifest.ObjectKey, "bad-checksum"u8.ToArray(), Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => ingest.ReplayArchiveAsync(Ct));
        Assert.False(ingest.Ready);
        Assert.NotNull(ingest.LastFailure);
        var before = ingest.Wal.TotalBytes;
        Assert.Equal(503, (await ingest.AcceptAsync(TelemetrySignal.Metrics,
            SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
        Assert.Equal(before, ingest.Wal.TotalBytes);
        Assert.Equal(0, ingest.AcceptedBatches);
        Assert.False(ingest.Reader.TryRead(out _));
        // Recovery must verify retained archives even with an empty WAL.
        await Assert.ThrowsAsync<InvalidDataException>(() => ingest.RecoverAsync(Ct));
        Assert.False(ingest.Ready);
        Assert.NotNull(ingest.LastFailure);
        await objects.PutAsync(manifest.ObjectKey, original, Ct);
        await ingest.ReplayArchiveAsync(Ct);
        await ingest.RecoverAsync(Ct);
        Assert.True(ingest.Ready);
        Assert.Null(ingest.LastFailure);
        var restored = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        Assert.All(restored.TopologyBindings!, binding =>
        {
            Assert.Equal("LegacyTopologyUnknown", binding.Reason);
            Assert.Null(binding.NodeId);
        });
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics,
            SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
        _ = await ingest.Reader.ReadAsync(Ct);
        ingest.CompleteWork();
    }

    [Fact]
    public async Task Corrupt_archive_remains_closed_across_new_instances_until_verified_repair()
    {
        var manifest = await Seed();
        var original = await objects.GetAsync(manifest.ObjectKey, Ct);
        Assert.NotNull(original);
        await objects.PutAsync(manifest.ObjectKey, "bad-checksum"u8.ToArray(), Ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var fresh = Open(objects);
            await Assert.ThrowsAsync<InvalidDataException>(() => fresh.RecoverAsync(Ct));
            Assert.False(fresh.Ready);
            Assert.NotNull(fresh.LastFailure);
            Assert.Equal(503, (await fresh.AcceptAsync(TelemetrySignal.Metrics,
                SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
            Assert.Equal(0, fresh.AcceptedBatches);
            Assert.Equal(0, fresh.Wal.TotalBytes);
            Assert.Equal(manifest, Assert.Single(fresh.Archive.Manifests()));
        }
        await objects.PutAsync(manifest.ObjectKey, original, Ct);
        using var repaired = Open(objects);
        await repaired.RecoverAsync(Ct);
        Assert.True(repaired.Ready);
        Assert.Null(repaired.LastFailure);
        await repaired.ReplayArchiveAsync(Ct);
        Assert.True(repaired.Ready);
        Assert.Equal(200, (await repaired.AcceptAsync(TelemetrySignal.Metrics,
            SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
        _ = await repaired.Reader.ReadAsync(Ct);
        repaired.CompleteWork();
    }

    [Theory]
    [InlineData("rejected-count")]
    [InlineData("accepted-keys")]
    public async Task Checksum_valid_admission_mismatch_stays_closed_after_new_instance_and_repairs(string mode)
    {
        var manifest = await Seed();
        var originalObject = await objects.GetAsync(manifest.ObjectKey, Ct);
        Assert.NotNull(originalObject);
        var archive = new SignalArchive(objects, root);
        var original = await archive.ReadAsync(manifest, Ct);
        var broken = mode == "rejected-count"
            ? original with { RejectedCount = original.RejectedCount + 1 }
            : original with { AcceptedKeys = ["m/999/0/0/0"] };
        broken.Validate(); // The failure is decoder consistency, not hash/field validation.
        var bytes = RawSignalCodec.Encode(broken);
        var builder = new RawObjectBuilder(); builder.Add(broken.EnvelopeId, broken.ReceivedAt, bytes);
        var built = builder.Build(3);
        var brokenManifest = manifest with { ObjectSha256 = built.Sha256, EnvelopeLength = bytes.Length };
        var manifestPath = Path.Combine(archive.ManifestDirectory, original.EnvelopeId.ToString("N") + ".json");
        using (var active = Open(objects))
        {
            await active.RecoverAsync(Ct);
            await objects.PutAsync(manifest.ObjectKey, built.Compressed, Ct);
            await DurableFile.WriteAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(brokenManifest, RawSignalCodec.Json), Ct);
            Assert.Equal(mode == "rejected-count" ? original.RejectedCount + 1 : original.RejectedCount,
                (await archive.ReadAsync(brokenManifest, Ct)).RejectedCount);
            await Assert.ThrowsAsync<InvalidDataException>(() => active.ReplayArchiveAsync(Ct));
            Assert.False(active.Ready);
        }
        using (var fresh = Open(objects))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fresh.RecoverAsync(Ct));
            Assert.False(fresh.Ready);
            Assert.Equal(503, (await fresh.AcceptAsync(TelemetrySignal.Metrics,
                SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
            Assert.Equal(0, fresh.Wal.TotalBytes);
            Assert.Equal(0, fresh.AcceptedBatches);
        }
        await objects.PutAsync(manifest.ObjectKey, originalObject, Ct);
        await DurableFile.WriteAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, RawSignalCodec.Json), Ct);
        using var repaired = Open(objects);
        await repaired.RecoverAsync(Ct);
        Assert.True(repaired.Ready);
        Assert.Null(repaired.LastFailure);
        Assert.Equal(RawSignalCodec.Encode(original), RawSignalCodec.Encode(await archive.ReadAsync(manifest, Ct)));
        Assert.False(Directory.Exists(Path.Combine(root, "processed"))); // Verification is not materialization.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transient_archive_outage_does_not_latch_corruption_and_replay_recovers(bool timeout)
    {
        await Seed();
        var flaky = new ReadFault(objects);
        using var ingest = Open(flaky);
        await ingest.RecoverAsync(Ct);
        flaky.Failure = timeout ? new TimeoutException("temporary") : new IOException("temporary");
        if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => ingest.ReplayArchiveAsync(Ct));
        else await Assert.ThrowsAsync<IOException>(() => ingest.ReplayArchiveAsync(Ct));
        Assert.True(ingest.Ready);
        Assert.NotNull(ingest.LastFailure);
        flaky.Failure = null;
        await ingest.ReplayArchiveAsync(Ct);
        Assert.True(ingest.Ready);
        Assert.Null(ingest.LastFailure);
    }

    private sealed class ReadFault(IRawObjectStore inner) : IRawObjectStore
    {
        public Exception? Failure { get; set; }
        public Task EnsureBucketAsync(CancellationToken cancellationToken = default) => inner.EnsureBucketAsync(cancellationToken);
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            inner.PutAsync(key, content, cancellationToken);
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Failure is { } failure ? Task.FromException<byte[]?>(failure) : inner.GetAsync(key, cancellationToken);
        public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);
    }

    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
