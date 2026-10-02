using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>Legacy restore sets stay readable throughout copy-on-write upgrade;
/// absent source candidates never authorize a current inventory owner.</summary>
public sealed class LegacySignalReplayTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "legacy-replay-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private readonly InMemoryObjectStore objects = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SignalIngest Open(ISignalCheckpoints? gate = null) => new(new(), new(factory), objects,
        Options.Create(new SignalOptions { Directory = root }), Options.Create(new WalOptions()),
        Options.Create(new RawStoreOptions()), NullLogger<WriteAheadLog>.Instance, gate);

    private async Task<SignalArchiveManifest> SeedAsync(string first = "legacy", string second = "legacy-host")
    {
        var payload = Encoding.UTF8.GetBytes("""
            {"resourceMetrics":[{"resource":{"attributes":[
              {"key":"bizigo.source_key","value":{"stringValue":"FIRST"}},
              {"key":"host.name","value":{"stringValue":"SECOND"}},
              {"key":"owner_group","value":{"stringValue":"admin"}}]},
              "scopeMetrics":[{"metrics":[{"name":"legacy","gauge":{"dataPoints":[{"asInt":"9","timeUnixNano":"123"}]}}]}]}]}
            """.Replace("FIRST", first, StringComparison.Ordinal).Replace("SECOND", second, StringComparison.Ordinal));
        var decoded = new OtlpTelemetryDecoder().Decode(TelemetrySignal.Metrics, payload, "application/json");
        Assert.Single(decoded.Accepted);
        var envelope = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Metrics, "application/json", DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(payload), payload, 1, decoded.Accepted.Select(l => l.Key).ToArray(), 0);
        return await new SignalArchive(objects, root).ArchiveAsync(envelope, "retained-no-wal", Ct);
    }

    [Theory]
    [InlineData("archive-before-manifest")]
    [InlineData("archive-manifest-before-rename")]
    [InlineData("archive-manifest-after-rename")]
    public async Task Interrupted_legacy_upgrade_preserves_original_restore_set_and_retries(string stage)
    {
        var original = await SeedAsync();
        var originalBytes = await objects.GetAsync(original.ObjectKey, Ct);
        var archive = new SignalArchive(objects, root);
        using (var ingest = Open(new Fault(stage)))
        {
            await ingest.RecoverAsync(Ct);
            await Assert.ThrowsAsync<IOException>(() => ingest.ReplayArchiveAsync(Ct));
        }
        Assert.Equal(originalBytes, await objects.GetAsync(original.ObjectKey, Ct));
        Assert.Equal(1, (await archive.ReadAsync(original, Ct)).Version);
        var current = await archive.ReadAsync(Assert.Single(archive.Manifests()), Ct);
        Assert.Equal(stage.EndsWith("after-rename", StringComparison.Ordinal) ? RawSignalEnvelope.CurrentVersion : 1, current.Version);
        using var recovered = Open(); await recovered.RecoverAsync(Ct); await recovered.ReplayArchiveAsync(Ct);
        var manifest = Assert.Single(archive.Manifests());
        Assert.NotEqual(original.ObjectKey, manifest.ObjectKey);
        var upgraded = await archive.ReadAsync(manifest, Ct);
        Assert.Equal(original.EnvelopeId, upgraded.EnvelopeId); Assert.Equal(original.PayloadSha256, upgraded.PayloadSha256);
        Assert.Equal("legacy-owner-unknown", Assert.Single(upgraded.OwnerBindings!).Reason);
        Assert.Equal("LegacyTopologyUnknown", Assert.Single(upgraded.TopologyBindings!).Reason);
        await recovered.ReplayArchiveAsync(Ct);
        Assert.Equal(manifest.ObjectKey, Assert.Single(archive.Manifests()).ObjectKey);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "processed"), "*.json"));
    }

    [Theory]
    [InlineData("", "legacy-host", "legacy-host")]
    [InlineData("   ", "legacy-host", "legacy-host")]
    [InlineData("", "", "_unknown")]
    [InlineData("   ", "   ", "_unknown")]
    [InlineData("legacy", "legacy-host", "legacy")]
    public async Task Legacy_blank_candidates_use_next_nonblank_or_deterministic_unknown(string first, string second, string expected)
    {
        await SeedAsync(first, second);
        await using (var db = factory.CreateDbContext())
        { db.Sources.Add(new SourceEntity { SourceId = "legacy-host", OwnerGroup = "admin" }); await db.SaveChangesAsync(Ct); }
        using var ingest = Open(); await ingest.RecoverAsync(Ct); await ingest.ReplayArchiveAsync(Ct);
        var envelope = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        var binding = Assert.Single(envelope.OwnerBindings!);
        Assert.Equal(expected, binding.SourceId); Assert.Equal(OwnerGroups.Unassigned, binding.OwnerGroup);
        Assert.Equal("legacy-owner-unknown", binding.Reason);
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Equal(envelope.OwnerBindingsSha256, (await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct)).OwnerBindingsSha256);
    }

    [Fact]
    public async Task Normal_legacy_upgrade_remains_idempotent()
    {
        var original = await SeedAsync();
        using var ingest = Open(); await ingest.RecoverAsync(Ct); await ingest.ReplayArchiveAsync(Ct);
        var first = Assert.Single(ingest.Archive.Manifests());
        Assert.Equal(original.EnvelopeId, first.EnvelopeId);
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Equal(first.ObjectKey, Assert.Single(ingest.Archive.Manifests()).ObjectKey);
        Assert.Equal(OwnerGroups.Unassigned, Assert.Single((await ingest.Archive.ReadAsync(first, Ct)).OwnerBindings!).OwnerGroup);
    }

    private sealed class Fault(string stage) : ISignalCheckpoints
    { public Task ReachAsync(string point, CancellationToken token) => point == stage ? throw new IOException("Fixture interrupted archive upgrade") : Task.CompletedTask; }
    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
