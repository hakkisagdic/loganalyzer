using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
public sealed class TelemetryReplayIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_restore_after_wal_retention_preserves_A_and_unassigned(bool traces)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("moving", "A");
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        var restore = Path.Combine(f.Root, "restored");
        var original = new List<RawSignalEnvelope>();
        using (var ingest = f.Open())
        {
            await ingest.RecoverAsync(Ct);
            foreach (var source in new[] { "moving", "unmapped" })
            {
                IMessage request = traces ? TelemetryDbFixture.Traces(source, f.Now) : TelemetryDbFixture.Metrics(source, f.Now, false);
                original.Add(await f.EmitAsync(ingest, request, signal));
            }
            await ingest.SweepAsync(Ct); Assert.Empty(ingest.Wal.ListSealedSegments());
            Directory.CreateDirectory(Path.Combine(restore, "manifests"));
            foreach (var path in Directory.GetFiles(ingest.Archive.ManifestDirectory, "*.json"))
                File.Copy(path, Path.Combine(restore, "manifests", Path.GetFileName(path)));
        }
        // Restore set is only verified S3 objects and manifests. No old local
        // processed file, WAL, mutable source cache or owner-claim row survives.
        Directory.Delete(Path.Combine(f.Root, "processed"), true);
        await f.Db.TelemetryOwnerClaims.ExecuteDeleteAsync(Ct);
        await f.SourceAsync("moving", "B", f.Clock.GetUtcNow().AddSeconds(1));
        await f.SourceAsync("unmapped", "B", f.Clock.GetUtcNow().AddSeconds(1));
        var table = traces ? "trace_spans" : "metric_points";
        await f.SqlAsync("TRUNCATE TABLE " + table);
        await f.SqlAsync("TRUNCATE TABLE telemetry_feed_history");
        using var restored = f.Open(restore); await restored.RecoverAsync(Ct);
        await Task.WhenAll(restored.ReplayArchiveAsync(Ct), restored.ReplayArchiveAsync(Ct));
        var query = f.Window(signal);
        foreach (var owner in new[] { "A", "_unassigned" })
        {
            var row = Assert.Single((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups(owner, [owner]), Ct)).Records);
            var expected = original.Single(e => e.OwnerBindings![0].OwnerGroup == owner);
            Assert.Equal(expected.EnvelopeId, row.EnvelopeId); Assert.Equal(expected.OwnerBindings![0], row.Owner);
            Assert.Equal(expected.OwnerBindingsSha256, row.OwnerBindingSha256);
        }
        var b = AccessScope.ForGroups("new-B", ["B"]);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, b, Ct)).Records);
        IMessage fresh = traces ? TelemetryDbFixture.Traces("moving", f.Now + 2000000000) : TelemetryDbFixture.Metrics("moving", f.Now + 2000000000, false);
        await f.EmitAsync(restored, fresh, signal);
        Assert.Single((await f.Query.SearchTelemetryAsync(query, b, Ct)).Records);
        Assert.Equal(3, (await f.Query.CountTelemetryAsync(query, AccessScope.System("restore-check"), Ct)).Count);
        TelemetryDbFixture.Evidence("archive-restore-" + signal, new { restore, original, logicalCount = 3, owners = new[] { "A", "_unassigned", "B" } });
    }

    [Fact, Trait("Category", "Integration")]
    public async Task New_binding_corruption_never_uses_legacy_fallback()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("corrupt", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var envelope = await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("corrupt", f.Now, false), TelemetrySignal.Metrics);
        var manifest = Assert.Single(ingest.Archive.Manifests());
        var invalid = envelope with { OwnerBindings = null, OwnerBindingsSha256 = null };
        // Recompute the outer object checksum so validation reaches the corrupt
        // v2 ownership metadata; an archive-byte mismatch alone is insufficient.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(invalid, RawSignalCodec.Json);
        var builder = new RawObjectBuilder(); builder.Add(invalid.EnvelopeId, invalid.ReceivedAt, bytes);
        var built = builder.Build(3); await f.Objects.PutAsync(manifest.ObjectKey, built.Compressed, Ct);
        var changed = manifest with { ObjectSha256 = built.Sha256, EnvelopeLength = bytes.Length };
        await File.WriteAllBytesAsync(Path.Combine(ingest.Archive.ManifestDirectory, envelope.EnvelopeId.ToString("N") + ".json"), JsonSerializer.SerializeToUtf8Bytes(changed, RawSignalCodec.Json), Ct);
        await f.SqlAsync("TRUNCATE TABLE metric_points");
        await Assert.ThrowsAsync<InvalidDataException>(() => ingest.ReplayArchiveAsync(Ct));
        Assert.Equal(0, (await f.Query.CountTelemetryAsync(f.Window(TelemetrySignal.Metrics), AccessScope.System("corruption"), Ct)).Count);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Conflicting_writers_cannot_publish_two_owners_before_merge()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("conflict", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var envelope = await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("conflict", f.Now, false), TelemetrySignal.Metrics);
        var decoded = new OtlpTelemetryDecoder().Replay(envelope);
        var competing = envelope with { OwnerBindings = envelope.OwnerBindings!.Select(o => o with { OwnerGroup = "B" }).ToArray() };
        competing = competing with { OwnerBindingsSha256 = competing.ComputeOwnerBindingsHash() };
        var correct = decoded.Accepted.Select(l => TelemetryMaterializer.Materialize(envelope, l, envelope.OwnerBindings!.Single(o => o.LeafKey == l.Key))).ToArray();
        var wrong = decoded.Accepted.Select(l => TelemetryMaterializer.Materialize(competing, l, competing.OwnerBindings!.Single(o => o.LeafKey == l.Key))).ToArray();
        await f.SqlAsync("SYSTEM STOP MERGES metric_points");
        var secondWriter = new TelemetryWriter(f.Storage, new Bizigo.ControlPlane.HistoricalTelemetryOwners(f.Factory));
        await Task.WhenAll(f.Writer.WriteAsync(correct, Ct), Assert.ThrowsAsync<InvalidDataException>(() => secondWriter.WriteAsync(wrong, Ct)));
        var query = f.Window(TelemetrySignal.Metrics);
        Assert.Single((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("A", ["A"]), Ct)).Records);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("B", ["B"]), Ct)).Records);
        Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, AccessScope.System("conflict"), Ct)).Count);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM metric_points WHERE owner_group='B'")).Trim());
        await f.SqlAsync("SYSTEM START MERGES metric_points");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Database_ack_before_checkpoint_recovery_is_logically_idempotent()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("interrupted", "A");
        Guid id;
        using (var ingest = f.Open(checkpoints: new FailCheckpoint()))
        {
            await ingest.RecoverAsync(Ct);
            var admitted = await ingest.AcceptAsync(TelemetrySignal.Metrics, TelemetryDbFixture.Metrics("interrupted", f.Now, false).ToByteArray(), "application/x-protobuf", Ct);
            Assert.Equal(200, admitted.Status); id = admitted.EnvelopeId!.Value;
            var work = await ingest.Reader.ReadAsync(Ct);
            await Assert.ThrowsAsync<IOException>(() => ingest.ProcessAsync(work, Ct)); ingest.CompleteWork();
            Assert.False(File.Exists(Path.Combine(f.Root, "processed", id.ToString("N") + ".json")));
            await ingest.Wal.SealAsync(Ct); Assert.NotEmpty(ingest.Wal.ListSealedSegments());
            Assert.Equal(1, (await f.Query.CountTelemetryAsync(f.Window(TelemetrySignal.Metrics), AccessScope.System("ack-observer"), Ct)).Count);
        }
        using var reopened = f.Open(); await reopened.RecoverAsync(Ct); await reopened.ReplayArchiveAsync(Ct);
        Assert.True(File.Exists(Path.Combine(f.Root, "processed", id.ToString("N") + ".json")));
        var query = f.Window(TelemetrySignal.Metrics); var scope = AccessScope.ForGroups("recovered", ["A"]);
        Assert.Single((await f.Query.SearchTelemetryAsync(query, scope, Ct)).Records);
        Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        Assert.Equal(1, Assert.Single((await f.Query.SummarizeTelemetryAsync(query, scope, Ct)).Groups).Count);
        await reopened.SweepAsync(Ct); Assert.Empty(reopened.Wal.ListSealedSegments());
    }

    /// <summary>Planner-only: actual S3 object/manifest and ClickHouse delivery
    /// remain recoverable across each legacy upgrade publication boundary.</summary>
    [Theory, Trait("Category", "Integration")]
    [InlineData(false, "archive-before-manifest")]
    [InlineData(false, "archive-manifest-before-rename")]
    [InlineData(false, "archive-manifest-after-rename")]
    [InlineData(true, "archive-before-manifest")]
    [InlineData(true, "archive-manifest-before-rename")]
    [InlineData(true, "archive-manifest-after-rename")]
    public async Task Legacy_upgrade_interruption_preserves_real_object_and_restores_database(bool traces, string stage)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("legacy", "B");
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        var payload = traces ? TelemetryDbFixture.Traces("legacy", f.Now).ToByteArray()
            : TelemetryDbFixture.Metrics("legacy", f.Now, false).ToByteArray();
        var decoded = new OtlpTelemetryDecoder().Decode(signal, payload, "application/x-protobuf");
        var legacy = new RawSignalEnvelope(1, Guid.NewGuid(), signal, "application/x-protobuf", DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(payload), payload, 1, decoded.Accepted.Select(l => l.Key).ToArray(), 0);
        var archive = new SignalArchive(f.Objects, f.Root);
        // Seed a historical restore set before opening SignalIngest, whose
        // RecoverAsync normally creates the fixture's unique bucket.
        await f.Objects.EnsureBucketAsync(Ct);
        var original = await archive.ArchiveAsync(legacy, "retained-no-wal", Ct);
        var originalBytes = await f.Objects.GetAsync(original.ObjectKey, Ct);
        using (var interrupted = f.Open(checkpoints: new ArchiveFault(stage)))
        {
            await interrupted.RecoverAsync(Ct);
            await Assert.ThrowsAsync<IOException>(() => interrupted.ReplayArchiveAsync(Ct));
        }
        Assert.Equal(originalBytes, await f.Objects.GetAsync(original.ObjectKey, Ct));
        Assert.Equal(1, (await archive.ReadAsync(original, Ct)).Version);
        await archive.ReadAsync(Assert.Single(archive.Manifests()), Ct);
        using var recovered = f.Open(); await recovered.RecoverAsync(Ct); await recovered.ReplayArchiveAsync(Ct);
        await recovered.ReplayArchiveAsync(Ct);
        var query = f.Window(signal);
        var rows = await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("legacy", ["_unassigned"]), Ct);
        Assert.Single(rows.Records); Assert.Equal("legacy-owner-unknown", rows.Records[0].Owner.Reason);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("current", ["B"]), Ct)).Records);
        var manifest = Assert.Single(archive.Manifests()); Assert.NotEqual(original.ObjectKey, manifest.ObjectKey);
        TelemetryDbFixture.Evidence("legacy-upgrade-" + signal + "-" + stage, new { original, manifest, rows });
    }

    private sealed class ArchiveFault(string stage) : ISignalCheckpoints
    { public Task ReachAsync(string point, CancellationToken token) => point == stage ? throw new IOException("Fixture archive publication interruption") : Task.CompletedTask; }

    private sealed class FailCheckpoint : ISignalCheckpoints
    {
        public Task ReachAsync(string checkpoint, CancellationToken token) => checkpoint == "after-telemetry-db-before-checkpoint"
            ? Task.FromException(new IOException("Injected interruption after actual database acknowledgement.")) : Task.CompletedTask;
    }
}
