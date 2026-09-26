using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class SignalArchiveBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "signal-boundary-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static RawSignalEnvelope Envelope()
    {
        var payload = SignalDurabilityTests.Payload();
        return new(1, Guid.NewGuid(), TelemetrySignal.Metrics, "application/json", DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(payload), payload, 1, ["m/0/0/0/0"], 1);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Archive_verification_failure_never_commits_manifest(string mode)
    {
        var store = new BrokenObjects(mode);
        var archive = new SignalArchive(store, root);
        if (mode == "put")
            await Assert.ThrowsAsync<IOException>(() => archive.ArchiveAsync(Envelope(), "wal-segment", Ct));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => archive.ArchiveAsync(Envelope(), "wal-segment", Ct));
        Assert.Empty(archive.Manifests());
    }

    [Fact]
    public async Task Atomic_checkpoint_failure_preserves_previous_final_and_hides_temp()
    {
        var path = Path.Combine(root, "result.json");
        await DurableFile.WriteAsync(path, "old"u8.ToArray(), Ct);
        await Assert.ThrowsAsync<IOException>(() => DurableFile.WriteAsync(path, "new"u8.ToArray(), Ct,
            (stage, _) => stage == "before-rename" ? throw new IOException("crash seam") : Task.CompletedTask));
        Assert.Equal("old", await File.ReadAllTextAsync(path, Ct));
        Assert.Single(Directory.GetFiles(root));
        await DurableFile.WriteAsync(path, "new"u8.ToArray(), Ct);
        Assert.Equal("new", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task Exact_wal_limit_serializes_concurrent_writers()
    {
        var payload = RawSignalCodec.Encode(Envelope());
        using var wal = Wal(payload.Length + 12);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await wal.AppendAsync(payload, Ct); return true; }
            catch (WalFullException) { return false; }
        }));
        Assert.Single(outcomes, x => x);
        Assert.Equal(payload.Length + 12, wal.TotalBytes);
        await wal.SealAsync(Ct);
        Assert.Single(WriteAheadLog.ReadFrames(Assert.Single(wal.ListSealedSegments()).Path, true));
    }

    [Theory]
    [InlineData("crc")]
    [InlineData("magic")]
    [InlineData("length")]
    public async Task Corrupt_middle_frame_preserves_acknowledged_prefix_and_suffix(string field)
    {
        var payloads = new[] { RawSignalCodec.Encode(Envelope()), RawSignalCodec.Encode(Envelope()), RawSignalCodec.Encode(Envelope()) };
        string path;
        using (var wal = Wal())
        {
            foreach (var payload in payloads) await wal.AppendAsync(payload, Ct);
            await wal.SealAsync(Ct);
            path = Assert.Single(wal.ListSealedSegments()).Path;
        }
        var corrupted = await File.ReadAllBytesAsync(path, Ct);
        corrupted[payloads[0].Length + 12 + (field == "magic" ? 0 : field == "length" ? 4 : 8)] ^= 1;
        await File.WriteAllBytesAsync(path, corrupted, Ct);
        for (var i = 0; i < 2; i++)
        {
            using var wal = Wal();
            Assert.Single(wal.Recovery.CorruptSegments);
            Assert.NotNull(wal.Failure);
            Assert.Equal(0, wal.Recovery.TruncatedBytes);
            Assert.Equal(corrupted, await File.ReadAllBytesAsync(path, Ct));
            await Assert.ThrowsAsync<IOException>(() => wal.AppendAsync(payloads[0], Ct));
            Assert.Throws<InvalidDataException>(() => WriteAheadLog.ReadFrames(path, true).ToArray());
        }
    }

    private WriteAheadLog Wal(long max = 10000000) => new(Options.Create(new WalOptions
        { Directory = root, StrictRecovery = true, MaxTotalBytes = max }), NullLogger<WriteAheadLog>.Instance);

    private sealed class BrokenObjects(string mode) : IRawObjectStore
    {
        public Task EnsureBucketAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            mode == "put" ? throw new IOException("put failed") : Task.CompletedTask;
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(mode == "missing" ? null : "wrong bytes"u8.ToArray());
        public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<RawObjectInfo?>(null);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
