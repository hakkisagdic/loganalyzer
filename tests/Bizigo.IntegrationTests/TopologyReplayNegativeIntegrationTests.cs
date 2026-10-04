using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>Real PG/CH/S3 admission and archive negatives. No mocked resolver,
/// projection or object store.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyReplayNegativeIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, "MissingBinding")]
    [InlineData(true, "SourceUnresolved")]
    public async Task Payload_claim_and_unknown_replay_never_gain_later_binding(bool unknown, string expectedReason)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var source = unknown ? "unknown-source" : "known-A";
        if (!unknown) await f.SourceAsync(source, "A");
        await f.SourceAsync("known-B", "B");
        var registry = new TopologyRegistry(f.Factory);
        var scope = AccessScope.ForGroups("negative-admin", ["A", "B"]);
        var foreign = await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "checkout-B", "B", true, [new("known-B", "", "checkout")]), Ct);
        Assert.Equal(201, foreign.Status);
        var time = checked((ulong)decimal.Parse(foreign.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture) + 1000UL);
        var payload = TelemetryDbFixture.Traces(source, time);
        payload.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "checkout";
        payload.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "owner_group").Value.StringValue = "B";
        payload.ResourceSpans[0].ScopeSpans[0].Spans[0].ParentSpanId = ByteString.Empty;
        // Admit with the original negative binding, but do not materialize it.
        // Restore the real archived envelope into a distinct root with no WAL so
        // the first typed write happens only after the later registry change.
        RawSignalEnvelope envelope;
        var archiveRoot = Path.Combine(f.Root, "negative-archive-only");
        var archive = new SignalArchive(f.Objects, archiveRoot);
        using (var admission = f.Open())
        {
            await admission.RecoverAsync(Ct);
            Assert.Equal(200, (await admission.AcceptAsync(TelemetrySignal.Traces,
                payload.ToByteArray(), "application/x-protobuf", Ct)).Status);
            var work = await admission.Reader.ReadAsync(Ct);
            admission.CompleteWork();
            envelope = work.Envelope;
            await archive.ArchiveAsync(envelope, "admission-snapshot-only", Ct);
        }
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        var binding = Assert.Single(envelope.TopologyBindings!);
        Assert.Equal(expectedReason, binding.Reason); Assert.Null(binding.NodeId);
        Assert.Equal(unknown ? OwnerGroups.Unassigned : "A", binding.OwnerGroup);
        if (unknown) await f.SourceAsync(source, "A", DateTimeOffset.UtcNow.AddSeconds(1));
        var later = await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "checkout-A", "A", true, [new(source, "", "checkout")]), Ct);
        Assert.Equal(201, later.Status);
        Assert.NotEqual(foreign.Node.Id, later.Node!.Id);
        await using var services = Services(f);
        using var replay = f.Open(archiveRoot, services.GetRequiredService<ITelemetrySink>());
        await replay.RecoverAsync(Ct);
        Assert.Empty(replay.Wal.ListSealedSegments());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        await replay.ReplayArchiveAsync(Ct);
        var firstTyped = await AssertCapturedNegativeRecord(f, envelope, binding, expectedReason);
        await replay.ReplayArchiveAsync(Ct);
        var repeatedTyped = await AssertCapturedNegativeRecord(f, envelope, binding, expectedReason);
        Assert.Equal(firstTyped, repeatedTyped);
        var restored = await replay.Archive.ReadAsync(Assert.Single(replay.Archive.Manifests()), Ct);
        Assert.Equal(RawSignalCodec.Encode(envelope), RawSignalCodec.Encode(restored));
        Assert.Equal(binding, Assert.Single(restored.TopologyBindings!));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        await using var read = services.CreateAsyncScope();
        Assert.Empty((await read.ServiceProvider.GetRequiredService<IScopedQuery>().SearchTopologyEdgesAsync(
            new(time + 1000000000m, Provenance: TopologyProvenance.Observed), scope, Ct)).Items);
        TelemetryDbFixture.Evidence("o05-negative-replay-" + unknown, new { unknown, expectedReason,
            binding, envelope.EnvelopeId, envelope.PayloadSha256, envelope.TopologyBindingsSha256,
            laterNode = later.Node.Id, foreignNode = foreign.Node.Id, replayCount = 2,
            firstTyped, repeatedTyped, archiveOnlyFirstMaterialization = true,
            freshProcess = false });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Legacy_archive_has_explicit_unknown_topology_despite_current_matching_registry(int version)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var envelope = await Admission(f);
        var legacy = envelope with { Version = version, EnvelopeId = Guid.NewGuid(),
            TopologyBindings = null, TopologyBindingsSha256 = null,
            OwnerBindings = version == 1 ? null : envelope.OwnerBindings, OwnerBindingsSha256 = null };
        if (version == 2) legacy = legacy with { OwnerBindingsSha256 = legacy.ComputeOwnerBindingsHash() };
        legacy.Validate();
        var root = Path.Combine(f.Root, "legacy-only");
        var archive = new SignalArchive(f.Objects, root);
        var before = await archive.ArchiveAsync(legacy, "archived-without-wal", Ct);
        await using var services = Services(f);
        using var replay = f.Open(root, services.GetRequiredService<ITelemetrySink>());
        await replay.RecoverAsync(Ct);
        Assert.Empty(replay.Wal.ListSealedSegments());
        await replay.ReplayArchiveAsync(Ct);
        await replay.ReplayArchiveAsync(Ct);
        var restored = await archive.ReadAsync(Assert.Single(archive.Manifests()), Ct);
        Assert.Equal(RawSignalEnvelope.CurrentVersion, restored.Version);
        Assert.Equal(legacy.EnvelopeId, restored.EnvelopeId);
        Assert.Equal(legacy.Payload, restored.Payload);
        var binding = Assert.Single(restored.TopologyBindings!);
        Assert.Equal("LegacyTopologyUnknown", binding.Reason); Assert.Null(binding.NodeId);
        Assert.Equal(version == 1 ? OwnerGroups.Unassigned : "A", binding.OwnerGroup);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.NotNull(await f.Objects.GetAsync(before.ObjectKey, Ct)); // copy-on-write retained old restore set
        TelemetryDbFixture.Evidence("o08-legacy-" + version, new { version, legacy.EnvelopeId,
            before, after = Assert.Single(archive.Manifests()), binding });
    }

    [Theory]
    [InlineData("topology-checksum")]
    [InlineData("topology-missing")]
    [InlineData("owner-missing")]
    [InlineData("rejected-count")]
    [InlineData("accepted-keys")]
    public async Task Corrupt_required_snapshot_fails_before_typed_write_and_preserves_object(string failure)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var admitted = await Admission(f);
        var broken = failure switch
        {
            "topology-checksum" => admitted with { TopologyBindingsSha256 = new string('0', 64) },
            "topology-missing" => admitted with { TopologyBindings = null, TopologyBindingsSha256 = null },
            "owner-missing" => admitted with { OwnerBindings = null, OwnerBindingsSha256 = null },
            "rejected-count" => admitted with { RejectedCount = admitted.RejectedCount + 1 },
            "accepted-keys" => WrongAcceptedKey(admitted),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        if (failure is "rejected-count" or "accepted-keys") broken.Validate();
        // Deliberately create a physically valid object/manifest with an invalid
        // inner admission snapshot; bypass only the test serializer's Validate.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(broken, RawSignalCodec.Json);
        var objectBuilder = new RawObjectBuilder(); objectBuilder.Add(broken.EnvelopeId, broken.ReceivedAt, bytes);
        var built = objectBuilder.Build(3);
        var key = "invalid-topology/" + broken.EnvelopeId.ToString("N") + ".json.zst";
        await f.Objects.PutAsync(key, built.Compressed, Ct);
        var root = Path.Combine(f.Root, "invalid-only"); var manifestRoot = Path.Combine(root, "manifests");
        Directory.CreateDirectory(manifestRoot);
        var manifest = new SignalArchiveManifest(1, broken.EnvelopeId, broken.Signal, broken.PayloadSha256,
            key, built.Sha256, "no-wal", DateTimeOffset.UtcNow, bytes.Length);
        await File.WriteAllBytesAsync(Path.Combine(manifestRoot, broken.EnvelopeId.ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, RawSignalCodec.Json), Ct);
        await using var services = Services(f);
        await AssertFreshProcessRejectsCorruptArchive(f, root, failure);
        using var replay = f.Open(root, services.GetRequiredService<ITelemetrySink>());
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.RecoverAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.ReplayArchiveAsync(Ct));
        Assert.False(replay.Ready); Assert.NotNull(replay.LastFailure);
        var walBytes = replay.Wal.TotalBytes;
        var refused = await replay.AcceptAsync(admitted.Signal, admitted.Payload, admitted.ContentType, Ct);
        Assert.Equal(503, refused.Status);
        Assert.Equal(walBytes, replay.Wal.TotalBytes);
        Assert.Equal(0, replay.AcceptedBatches);
        Assert.False(replay.Reader.TryRead(out _));
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.RecoverAsync(Ct)); // Empty WAL cannot hide archive corruption.
        Assert.False(replay.Ready);
        Assert.NotNull(replay.LastFailure);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(built.Compressed, await f.Objects.GetAsync(key, Ct));
        Assert.Equal(manifest, Assert.Single(replay.Archive.Manifests()));
        TelemetryDbFixture.Evidence("o08-corrupt-" + failure, new { failure, manifest,
            replay.Ready, replay.LastFailure, typedCount = 0, graphCount = 0, objectPreserved = true });

        // Explicit operator repair uses the original verified admission snapshot,
        // never current registry inference, and preserves the corrupt object for diagnosis.
        var repairedBytes = RawSignalCodec.Encode(admitted);
        var repairBuilder = new RawObjectBuilder(); repairBuilder.Add(admitted.EnvelopeId, admitted.ReceivedAt, repairedBytes);
        var repaired = repairBuilder.Build(3);
        var repairKey = "repaired-topology/" + admitted.EnvelopeId.ToString("N") + ".json.zst";
        await f.Objects.PutAsync(repairKey, repaired.Compressed, Ct);
        var repairedManifest = manifest with { ObjectKey = repairKey, ObjectSha256 = repaired.Sha256,
            EnvelopeLength = repairedBytes.Length };
        await DurableFile.WriteAsync(Path.Combine(manifestRoot, admitted.EnvelopeId.ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(repairedManifest, RawSignalCodec.Json), Ct);
        await replay.ReplayArchiveAsync(Ct);
        await replay.RecoverAsync(Ct);
        Assert.True(replay.Ready); Assert.Null(replay.LastFailure);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal(built.Compressed, await f.Objects.GetAsync(key, Ct));
        Assert.Equal(RawSignalCodec.Encode(admitted), RawSignalCodec.Encode(
            await replay.Archive.ReadAsync(Assert.Single(replay.Archive.Manifests()), Ct)));
        TelemetryDbFixture.Evidence("o08-repaired-" + failure, new { failure, replay.Ready,
            replay.LastFailure, repairedManifest, originalCorruptObjectPreserved = true });
    }

    private static RawSignalEnvelope WrongAcceptedKey(RawSignalEnvelope original)
    {
        const string wrongKey = "t/999/0/0";
        var changed = original with
        {
            AcceptedKeys = [wrongKey],
            OwnerBindings = [Assert.Single(original.OwnerBindings!) with { LeafKey = wrongKey }],
            TopologyBindings = [Assert.Single(original.TopologyBindings!) with { LeafKey = wrongKey }],
        };
        changed = changed with { OwnerBindingsSha256 = changed.ComputeOwnerBindingsHash() };
        return changed with { TopologyBindingsSha256 = changed.ComputeTopologyBindingsHash() };
    }

    private async Task AssertFreshProcessRejectsCorruptArchive(TelemetryDbFixture f, string root, string label)
    {
        var config = Path.Combine(f.Root, "corrupt-restart.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
        { root, stage = "after-wal-before-ack", recover = true, first = "", second = "" }), Ct);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = DevStackSetup.RepoPath(""),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(DevStackSetup.RepoPath("sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"));
        start.ArgumentList.Add("--topology-crash"); start.ArgumentList.Add(config);
        start.Environment["ConnectionStrings__ControlPlane"] = stack.PostgresConnectionString;
        start.Environment["ConnectionStrings__ClickHouse"] = f.Storage.Options.ConnectionString;
        start.Environment["RawStore__ServiceUrl"] = f.RawOptions.ServiceUrl;
        start.Environment["RawStore__Bucket"] = f.RawOptions.Bucket;
        start.Environment["RawStore__AccessKey"] = f.RawOptions.AccessKey;
        start.Environment["RawStore__SecretKey"] = f.RawOptions.SecretKey;
        using var child = Process.Start(start) ?? throw new IOException("Could not start corrupt archive recovery child.");
        var stdout = child.StandardOutput.ReadToEndAsync(Ct);
        var stderr = child.StandardError.ReadToEndAsync(Ct);
        try
        {
            Assert.NotEqual(Environment.ProcessId, child.Id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await child.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, child.ExitCode);
            Assert.Contains(nameof(InvalidDataException), await stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "recovery-ready.json")));
            Assert.False(File.Exists(Path.Combine(root, "recovered.json")));
            Assert.False(File.Exists(Path.Combine(root, "first-ack.json")));
            Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await child.WaitForExitAsync(cleanup.Token);
            TelemetryDbFixture.Evidence("o08-corrupt-process-" + label, new
            { pid = child.Id, child.ExitCode, ownedPidAlive = !child.HasExited, stdout = await stdout, stderr = await stderr });
        }
    }

    private static async Task<string> AssertCapturedNegativeRecord(TelemetryDbFixture f,
        RawSignalEnvelope envelope, TopologyLeafBinding binding, string expectedReason)
    {
        using var rows = JsonDocument.Parse(await f.SqlAsync(
            "SELECT record, owner_group, resource_id FROM trace_spans FINAL FORMAT JSON"));
        var row = Assert.Single(rows.RootElement.GetProperty("data").EnumerateArray());
        var serialized = row.GetProperty("record").GetString()!;
        var typed = JsonSerializer.Deserialize<TelemetryRecord>(serialized, RawSignalCodec.Json)!;
        Assert.NotNull(typed);
        Assert.Equal(envelope.EnvelopeId, typed.EnvelopeId);
        Assert.Equal(envelope.PayloadSha256, typed.PayloadSha256);
        Assert.Equal(envelope.TopologyBindingsSha256, typed.TopologyBindingsSha256);
        Assert.Equal(envelope.OwnerBindingsSha256, typed.OwnerBindingSha256);
        Assert.Equal(Assert.Single(envelope.OwnerBindings!), typed.Owner);
        Assert.Equal(binding, typed.Topology);
        Assert.NotNull(typed.Topology);
        Assert.False(typed.Topology.Resolved);
        Assert.Null(typed.Topology.NodeId);
        Assert.Null(typed.Topology.ServiceNodeId);
        Assert.Null(typed.Topology.InstanceNodeId);
        Assert.Equal(expectedReason, typed.Topology.Reason);
        Assert.Equal(binding.OwnerGroup, typed.Owner.OwnerGroup);
        Assert.Equal(binding.SourceId, typed.Owner.SourceId);
        Assert.Equal(binding.OwnerGroup, row.GetProperty("owner_group").GetString());
        Assert.Equal(binding.SourceId, row.GetProperty("resource_id").GetString());
        return serialized;
    }

    private async Task<RawSignalEnvelope> Admission(TelemetryDbFixture f)
    {
        await f.SourceAsync("archive-source", "A");
        var registry = new TopologyRegistry(f.Factory);
        var node = await registry.CreateAsync(AccessScope.ForGroups("fixture", ["A"]), true,
            new(TopologyNodeKind.Service, "current-checkout", "A", true, [new("archive-source", "", "checkout")]), Ct);
        Assert.Equal(201, node.Status);
        var time = checked((ulong)decimal.Parse(node.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture) + 1000UL);
        var payload = TelemetryDbFixture.Traces("archive-source", time);
        payload.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "checkout";
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Traces, payload.ToByteArray(), "application/x-protobuf", Ct)).Status);
        var work = await ingest.Reader.ReadAsync(Ct); ingest.CompleteWork();
        Assert.True(Assert.Single(work.Envelope.TopologyBindings!).Resolved);
        return work.Envelope; // deliberately not materialized
    }

    private ServiceProvider Services(TelemetryDbFixture f)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddControlPlane(stack.PostgresConnectionString);
        services.AddBizigoDataPlane(f.Storage.Options);
        return services.BuildServiceProvider();
    }
}
