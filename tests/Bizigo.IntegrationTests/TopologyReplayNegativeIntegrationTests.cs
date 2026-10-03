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
/// projection or object store. This source is staged outside the build until release.</summary>
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
        await using var services = Services(f);
        using var ingest = f.Open(sink: services.GetRequiredService<ITelemetrySink>());
        await ingest.RecoverAsync(Ct);
        var envelope = await f.EmitAsync(ingest, payload, TelemetrySignal.Traces);
        var binding = Assert.Single(envelope.TopologyBindings!);
        Assert.Equal(expectedReason, binding.Reason); Assert.Null(binding.NodeId);
        Assert.Equal(unknown ? OwnerGroups.Unassigned : "A", binding.OwnerGroup);
        if (unknown) await f.SourceAsync(source, "A", DateTimeOffset.UtcNow.AddSeconds(1));
        var later = await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, "checkout-A", "A", true, [new(source, "", "checkout")]), Ct);
        Assert.Equal(201, later.Status);
        Assert.NotEqual(foreign.Node.Id, later.Node!.Id);
        await ingest.ReplayArchiveAsync(Ct);
        await ingest.ReplayArchiveAsync(Ct);
        var restored = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        Assert.Equal(RawSignalCodec.Encode(envelope), RawSignalCodec.Encode(restored));
        Assert.Equal(binding, Assert.Single(restored.TopologyBindings!));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        await using var read = services.CreateAsyncScope();
        Assert.Empty((await read.ServiceProvider.GetRequiredService<IScopedQuery>().SearchTopologyEdgesAsync(
            new(time + 1000000000m, Provenance: TopologyProvenance.Observed), scope, Ct)).Items);
        TelemetryDbFixture.Evidence("o05-negative-replay-" + unknown, new { unknown, expectedReason,
            binding, envelope.EnvelopeId, envelope.PayloadSha256, envelope.TopologyBindingsSha256,
            laterNode = later.Node.Id, foreignNode = foreign.Node.Id, replayCount = 2 });
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
    public async Task Corrupt_required_snapshot_fails_before_typed_write_and_preserves_object(string failure)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var admitted = await Admission(f);
        var broken = failure switch
        {
            "topology-checksum" => admitted with { TopologyBindingsSha256 = new string('0', 64) },
            "topology-missing" => admitted with { TopologyBindings = null, TopologyBindingsSha256 = null },
            "owner-missing" => admitted with { OwnerBindings = null, OwnerBindingsSha256 = null },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
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
        using var replay = f.Open(root, services.GetRequiredService<ITelemetrySink>());
        await replay.RecoverAsync(Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => replay.ReplayArchiveAsync(Ct));
        Assert.False(replay.Ready); Assert.NotNull(replay.LastFailure);
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM trace_spans FINAL")).Trim());
        Assert.Equal("0", (await f.SqlAsync("SELECT count() FROM topology_edges_observed FINAL")).Trim());
        Assert.Equal(built.Compressed, await f.Objects.GetAsync(key, Ct));
        Assert.Equal(manifest, Assert.Single(replay.Archive.Manifests()));
        TelemetryDbFixture.Evidence("o08-corrupt-" + failure, new { failure, manifest,
            replay.Ready, replay.LastFailure, typedCount = 0, graphCount = 0, objectPreserved = true });
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
