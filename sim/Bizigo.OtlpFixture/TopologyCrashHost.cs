using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;

/// <summary>Explicit process-test executable. The only substitution is a blocking
/// checkpoint; WAL, S3, typed writer, publication coordinator and projector are production code.</summary>
internal static class TopologyCrashHost
{
    internal sealed record Configuration(string Root, string Stage, bool Recover, string First, string Second,
        int TelemetryRetentionDays = 90, int ObservedRetentionDays = 90);

    public static async Task RunAsync(string path)
    {
        var config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllBytesAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Missing crash configuration.");
        var allowed = new[] { "after-wal-before-ack", "archive-before-manifest",
            "after-observed-db-before-publish", "after-telemetry-db-before-checkpoint" };
        if (!allowed.Contains(config.Stage, StringComparer.Ordinal)) throw new InvalidDataException("Unknown crash gate.");
        if (config.TelemetryRetentionDays is < 1 or > 36500 || config.ObservedRetentionDays is < 1 or > 36500)
            throw new InvalidDataException("Invalid fixture retention configuration.");
        Directory.CreateDirectory(config.Root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddControlPlane(builder.Configuration.GetConnectionString("ControlPlane")!);
        builder.Services.AddBizigoDataPlane(new ClickHouseOptions
        { ConnectionString = builder.Configuration.GetConnectionString("ClickHouse")!, MigrationsDirectory = "db/clickhouse",
            TelemetryRetentionDays = config.TelemetryRetentionDays });
        builder.Services.Configure<RawStoreOptions>(builder.Configuration.GetSection(RawStoreOptions.SectionName));
        builder.Services.AddSingleton<IRawObjectStore, S3RawObjectStore>();
        builder.Services.AddSingleton<OtlpTelemetryDecoder>();
        builder.Services.Configure<SignalOptions>(o =>
        { o.Directory = config.Root; o.ObservedRetentionDays = config.ObservedRetentionDays; });
        builder.Services.Configure<WalOptions>(o => { o.Directory = config.Root; o.FlushToDisk = true; });
        var gate = new KillGate(config.Root, config.Stage);
        builder.Services.AddSingleton<ISignalCheckpoints>(gate);
        // Replace only the checkpoint injection, preserving the real PG/CH protocol.
        builder.Services.AddSingleton<ITopologyObservedProjector>(sp =>
        {
            var coordinator = sp.GetRequiredService<ITopologyPublicationCoordinator>();
            return new TopologyObservedProjector(sp.GetRequiredService<ClickHouseContext>(),
                coordinator.PublishAsync, gate, coordinator.ReadPendingKeyAsync);
        });
        builder.Services.AddSingleton<SignalIngest>(sp =>
            ActivatorUtilities.CreateInstance<SignalIngest>(sp, sp.GetRequiredService<ITelemetrySink>()));
        await using var app = builder.Build();
        var ingest = app.Services.GetRequiredService<SignalIngest>();
        await ingest.RecoverAsync(ct);
        if (!ingest.Ready) throw new InvalidOperationException("Recovery did not become ready: " + ingest.LastFailure);
        if (config.Recover)
        {
            // Distinguish startup admission readiness from later archive replay.
            // Corrupt required restore metadata must prevent this receipt entirely.
            await Save(config.Root, "recovery-ready.json", new { pid = Environment.ProcessId, ready = ingest.Ready }, ct);
            // Also exercise archive replay after WAL recovery; repeated replay must not multiply proof.
            await ingest.ReplayArchiveAsync(ct);
            await ingest.ReplayArchiveAsync(ct);
            var verified = new List<object>();
            foreach (var manifest in ingest.Archive.Manifests())
            {
                var envelope = await ingest.Archive.ReadAsync(manifest, ct);
                verified.Add(new { manifest, envelope.EnvelopeId, envelope.PayloadSha256,
                    envelope.OwnerBindingsSha256, envelope.TopologyBindingsSha256, envelope.TopologyBindings });
            }
            await Save(config.Root, "recovered.json", new { pid = Environment.ProcessId,
                verified, replayCount = 2, verifiedReplayCount = 2, ready = ingest.Ready,
                configuredTelemetryRetentionDays = app.Services.GetRequiredService<TelemetryRetentionPolicy>().Days,
                configuredObservedRetentionDays = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SignalOptions>>().Value.ObservedRetentionDays }, ct);
            return;
        }
        await Send(config.First, "first", ingest, config.Root, ct);
        gate.Armed = true;
        await Send(config.Second, "second", ingest, config.Root, ct);
        throw new InvalidOperationException("Requested crash stage was never reached.");
    }

    private static async Task Send(string path, string name, SignalIngest ingest, string root, CancellationToken ct)
    {
        var admission = await ingest.AcceptAsync(TelemetrySignal.Traces, await File.ReadAllBytesAsync(path, ct),
            "application/x-protobuf", ct);
        await Save(root, name + "-ack.json", admission, ct);
        if (admission.Status != 200 || admission.Accepted != 1 || admission.Rejected != 0)
            throw new InvalidDataException("Unexpected raw admission: " + JsonSerializer.Serialize(admission));
        var work = await ingest.Reader.ReadAsync(ct);
        await ingest.ProcessAsync(work, ct);
        ingest.CompleteWork();
    }

    private static Task Save(string root, string name, object value, CancellationToken ct) =>
        DurableFile.WriteAsync(Path.Combine(root, name), JsonSerializer.SerializeToUtf8Bytes(value), ct);

    private sealed class KillGate(string root, string stage) : ISignalCheckpoints, ITopologyProjectionCheckpoints
    {
        public bool Armed { get; set; }
        public async Task ReachAsync(string point, CancellationToken ct)
        {
            if (!Armed || point != stage) return;
            // DurableFile fsyncs the marker before atomic rename. No release path:
            // the parent must really kill this PID, not throw/resume a callback.
            await Save(root, "stage.json", new { pid = Environment.ProcessId, stage, fsynced = true }, ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }
}
