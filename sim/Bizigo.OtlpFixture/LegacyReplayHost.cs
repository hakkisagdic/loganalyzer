using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>Container-free actual process replay of a WAL-free legacy restore set.</summary>
internal static class LegacyReplayHost
{
    internal static async Task RunAsync(string[] args)
    {
        var mode = args[0]; var root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
        var stage = args.Length > 2 ? args[2] : "none";
        var store = new Objects(Path.Combine(root, "objects"), mode == "replay" && stage == "object-write");
        var state = Path.Combine(root, "state");
        if (mode == "seed")
        {
            var first = args.Length > 2 ? args[2] : "legacy";
            var second = args.Length > 3 ? args[3] : "legacy-host";
            var payload = Encoding.UTF8.GetBytes("""
                {"resourceMetrics":[{"resource":{"attributes":[
                {"key":"bizigo.source_key","value":{"stringValue":FIRST}},
                {"key":"host.name","value":{"stringValue":SECOND}},
                {"key":"owner_group","value":{"stringValue":"admin"}}]},
                "scopeMetrics":[{"metrics":[{"name":"legacy","gauge":{"dataPoints":[{"asInt":"9","timeUnixNano":"123"}]}}]}]}]}
                """.Replace("FIRST", JsonSerializer.Serialize(first), StringComparison.Ordinal)
                    .Replace("SECOND", JsonSerializer.Serialize(second), StringComparison.Ordinal));
            var decoded = new OtlpTelemetryDecoder().Decode(TelemetrySignal.Metrics, payload, "application/json");
            var envelope = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Metrics, "application/json", DateTimeOffset.UtcNow,
                RawSignalEnvelope.Hash(payload), payload, 1, decoded.Accepted.Select(l => l.Key).ToArray(), 0);
            await new SignalArchive(store, state).ArchiveAsync(envelope, "retained-no-wal", default);
            Console.WriteLine(JsonSerializer.Serialize(new { envelope.EnvelopeId, accepted = decoded.Accepted.Count }));
            return;
        }
        if (mode != "replay") throw new ArgumentException("Unsupported legacy fixture mode.");
        using var ingest = new SignalIngest(new(), new SourceDirectory(new Factory()), store,
            Options.Create(new SignalOptions { Directory = state }), Options.Create(new WalOptions()),
            Options.Create(new RawStoreOptions()), NullLogger<WriteAheadLog>.Instance, new KillAt(stage));
        await ingest.RecoverAsync(default); await ingest.ReplayArchiveAsync(default);
        var restored = await ingest.Archive.ReadAsync(ingest.Archive.Manifests().Single(), default);
        Console.WriteLine(JsonSerializer.Serialize(new { version = restored.Version, restored.EnvelopeId,
            restored.PayloadSha256, owners = restored.OwnerBindings }, RawSignalCodec.Json));
    }

    private static void Kill()
    {
        Console.WriteLine("KILL pid=" + Environment.ProcessId); Console.Out.Flush();
        Process.GetCurrentProcess().Kill(); Thread.Sleep(Timeout.Infinite);
    }
    private sealed class KillAt(string stage) : ISignalCheckpoints
    { public Task ReachAsync(string point, CancellationToken token) { if (point == stage) Kill(); return Task.CompletedTask; } }
    private sealed class Factory : IDbContextFactory<ControlPlaneDbContext>
    {
        private readonly DbContextOptions<ControlPlaneDbContext> options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public ControlPlaneDbContext CreateDbContext() => new(options);
    }
    private sealed class Objects(string root, bool kill) : IRawObjectStore
    {
        public Task EnsureBucketAsync(CancellationToken cancellationToken = default) { Directory.CreateDirectory(root); return Task.CompletedTask; }
        public async Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            await DurableFile.WriteAsync(Path.Combine(root, key), content, cancellationToken);
            if (kill) Kill();
        }
        public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            File.Exists(Path.Combine(root, key)) ? await File.ReadAllBytesAsync(Path.Combine(root, key), cancellationToken) : null;
        public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<RawObjectInfo?>(null);
    }
}
