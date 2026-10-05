using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>O07: a real OS process dies at each production durability boundary.
/// The parent owns the PID and the DevStack collection owns PG/CH/S3.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyReplayProcessTests(DevStackFixture stack)
{
    [Theory]
    [InlineData("after-wal-before-ack", false)]
    [InlineData("after-wal-before-ack", true)]
    [InlineData("archive-before-manifest", false)]
    [InlineData("archive-before-manifest", true)]
    [InlineData("after-observed-db-before-publish", false)]
    [InlineData("after-observed-db-before-publish", true)]
    [InlineData("after-telemetry-db-before-checkpoint", false)]
    [InlineData("after-telemetry-db-before-checkpoint", true)]
    public async Task Projector_SIGKILL_fresh_process_restores_one_directed_proof(string stage, bool childFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, ct);
        await f.SourceAsync("crash-parent", "A");
        await f.SourceAsync("crash-child", "A");
        var registry = new TopologyRegistry(f.Factory);
        var scope = AccessScope.ForGroups("o07-parent", ["A"]);
        var parent = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "parent", "A", true,
            [new("crash-parent", "", "parent")]), ct);
        var child = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "child", "A", true,
            [new("crash-child", "", "child")]), ct);
        Assert.Equal(201, parent.Status); Assert.Equal(201, child.Status);
        var eventTime = checked((ulong)Math.Max(decimal.Parse(parent.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture),
            decimal.Parse(child.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture)) + 1000UL);
        var parentExport = TelemetryDbFixture.Traces("crash-parent", eventTime);
        var childExport = TelemetryDbFixture.Traces("crash-child", eventTime + 1);
        foreach (var (export, service) in new[] { (parentExport, "parent"), (childExport, "child") })
            export.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = service;
        var parentSpan = parentExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = childExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        var root = Path.Combine(f.Root, "process"); Directory.CreateDirectory(root);
        var first = Path.Combine(f.Root, "first.pb"); var second = Path.Combine(f.Root, "second.pb");
        await File.WriteAllBytesAsync(first, (childFirst ? childExport : parentExport).ToByteArray(), ct);
        await File.WriteAllBytesAsync(second, (childFirst ? parentExport : childExport).ToByteArray(), ct);
        var initial = Path.Combine(f.Root, "initial.json"); var restart = Path.Combine(f.Root, "restart.json");
        await File.WriteAllBytesAsync(initial, JsonSerializer.SerializeToUtf8Bytes(new { root, stage, recover = false, first, second }), ct);
        await File.WriteAllBytesAsync(restart, JsonSerializer.SerializeToUtf8Bytes(new { root, stage, recover = true, first, second }), ct);
        var services = new ServiceCollection().AddLogging();
        services.AddControlPlane(stack.PostgresConnectionString);
        services.AddBizigoDataPlane(f.Storage.Options);
        await using var provider = services.BuildServiceProvider();
        await using var queryScope = provider.CreateAsyncScope();
        var query = queryScope.ServiceProvider.GetRequiredService<IScopedQuery>();
        var request = new TopologyEdgeQuery(eventTime + 1000000000m, Provenance: TopologyProvenance.Observed);
        var before = await State(f, ct);
        var label = "o07-" + stage + (childFirst ? "-child-first" : "-parent-first");
        using var victim = Start(initial, f);
        var victimOut = victim.StandardOutput.ReadToEndAsync(ct); var victimErr = victim.StandardError.ReadToEndAsync(ct);
        Process? recovery = null; Task<string>? recoveryOut = null; Task<string>? recoveryErr = null;
        try
        {
            var markerPath = Path.Combine(root, "stage.json");
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(TimeSpan.FromSeconds(45));
                while (!File.Exists(markerPath))
                {
                    Assert.False(victim.HasExited, "Crash child exited before its durable stage marker.");
                    await Task.Delay(50, wait.Token);
                }
            }
            using var marker = JsonDocument.Parse(await File.ReadAllBytesAsync(markerPath, ct));
            Assert.Equal(victim.Id, marker.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(stage, marker.RootElement.GetProperty("stage").GetString());
            Assert.True(marker.RootElement.GetProperty("fsynced").GetBoolean());
            Assert.False(victim.HasExited);
            // Process.Kill on supported Linux/macOS runners sends SIGKILL. No
            // graceful stop, cancellation or checkpoint exception is accepted.
            victim.Kill(entireProcessTree: true);
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await victim.WaitForExitAsync(stop.Token);
            Assert.Equal(137, victim.ExitCode);
            var frames = Directory.GetFiles(Path.Combine(root, "wal"), "*.log")
                .SelectMany(p => WriteAheadLog.ReadFrames(p, strict: true)).Select(b => RawSignalCodec.Decode(b.Span)).ToArray();
            Assert.Equal(2, frames.Length);
            Assert.All(frames, e => { Assert.Single(e.TopologyBindings!); Assert.NotNull(e.TopologyBindingsSha256); });
            Assert.True(File.Exists(Path.Combine(root, "first-ack.json")));
            Assert.Equal(stage != "after-wal-before-ack", File.Exists(Path.Combine(root, "second-ack.json")));
            var archive = new SignalArchive(f.Objects, root);
            var archived = archive.Manifests().ToArray();
            Assert.Equal(stage is "after-wal-before-ack" or "archive-before-manifest" ? 1 : 2, archived.Length);
            var objects = new List<object>();
            foreach (var envelope in frames)
            {
                var key = $"otlp/v1/traces/{envelope.EnvelopeId:N}.json.zst";
                var bytes = await f.Objects.GetAsync(key, ct);
                objects.Add(new { envelope.EnvelopeId, key, present = bytes is not null,
                    sha256 = bytes is null ? null : RawSignalEnvelope.Hash(bytes) });
            }
            if (stage == "archive-before-manifest")
                foreach (var envelope in frames)
                    Assert.NotNull(await f.Objects.GetAsync($"otlp/v1/traces/{envelope.EnvelopeId:N}.json.zst", ct));
            foreach (var manifest in archived) await archive.ReadAsync(manifest, ct);
            var interrupted = await State(f, ct);
            var visibleBefore = await query.SearchTopologyEdgesAsync(request, scope, ct);
            // A child-first request intentionally publishes an explicit
            // MissingParent resolution before the parent arrives. The later
            // resolved edge is a second state transition, not a duplicated
            // proof or replay of the same publication.
            var childFirstWatermarkCount = childFirst ? 1UL : 0UL;
            var childFirstEpochCount = childFirst ? 1L : 0L;
            Assert.Equal(stage == "after-telemetry-db-before-checkpoint" ? 1 : 0, visibleBefore.Items.Count);
            Assert.Equal(stage is "after-observed-db-before-publish" or "after-telemetry-db-before-checkpoint" ? 2 : 1,
                await Scalar(f, "SELECT count() FROM trace_spans FINAL"));
            if (stage == "after-observed-db-before-publish")
            {
                Assert.Equal(1, await Scalar(f, "SELECT count() FROM topology_edges_observed FINAL"));
                Assert.Equal(1, interrupted.PendingCount);
                Assert.Equal(before.Watermark + childFirstWatermarkCount, interrupted.Watermark);
                Assert.Equal(before.Epoch + childFirstEpochCount, interrupted.Epoch);
                Assert.Equal(interrupted.Watermark + 1, interrupted.PendingSequence);
                Assert.Equal(64, interrupted.PendingKey!.Length);
            }
            Assert.Single(Directory.GetFiles(Path.Combine(root, "processed"), "*.json"));
            TelemetryDbFixture.Evidence(label + "-interrupted", new { pid = victim.Id, signal = "SIGKILL", exitCode = victim.ExitCode,
                childFirst, stage, marker = marker.RootElement, before, interrupted, objects, archived,
                ackReturned = stage != "after-wal-before-ack", rawDeliveryGuarantee = "durable WAL, ACK uncertain before return",
                publicEdges = visibleBefore.Items, envelopes = frames.Select(e => new { e.EnvelopeId, e.PayloadSha256, e.TopologyBindingsSha256 }) });
            recovery = Start(restart, f);
            recoveryOut = recovery.StandardOutput.ReadToEndAsync(ct); recoveryErr = recovery.StandardError.ReadToEndAsync(ct);
            Assert.NotEqual(victim.Id, recovery.Id);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            { timeout.CancelAfter(TimeSpan.FromSeconds(90)); await recovery.WaitForExitAsync(timeout.Token); }
            Assert.True(recovery.ExitCode == 0, await recoveryErr);
            var recoveredBytes = await File.ReadAllBytesAsync(Path.Combine(root, "recovered.json"), ct);
            using var recovered = JsonDocument.Parse(recoveredBytes);
            Assert.Equal(recovery.Id, recovered.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal(2, recovered.RootElement.GetProperty("verified").GetArrayLength());
            var after = await State(f, ct);
            Assert.Equal(0, after.PendingCount);
            Assert.Equal(before.Watermark + childFirstWatermarkCount + 1UL, after.Watermark);
            Assert.Equal(before.Epoch + childFirstEpochCount + 1L, after.Epoch);
            Assert.Equal((long)after.Watermark, after.PublishedSequence);
            Assert.Equal(2, await Scalar(f, "SELECT count() FROM trace_spans FINAL"));
            Assert.Equal(1, await Scalar(f, "SELECT count() FROM topology_edges_observed FINAL"));
            var final = await query.SearchTopologyEdgesAsync(request, scope, ct);
            // The child-first MissingParent record and the later Resolved
            // record are two immutable publication states. They must still
            // converge to one directed public proof, rather than duplicating
            // the edge or either of its two input manifests.
            var edge = Assert.Single(final.Items);
            Assert.Equal(parent.Node.Id, edge.FromNode); Assert.Equal(child.Node.Id, edge.ToNode);
            Assert.Equal("A", edge.FromOwnerGroup); Assert.Equal("A", edge.ToOwnerGroup);
            Assert.True(edge.Directed); Assert.Equal(TopologyProvenance.Observed, edge.Provenance);
            var detail = await query.GetTopologyEdgeAsync(edge.Id, request.ReadClockUnixNano, scope, ct);
            Assert.NotNull(detail); Assert.Equal(2, detail.Evidence.Count);
            Assert.Equal(2, detail.Evidence.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
            var finalManifests = archive.Manifests().ToArray();
            Assert.Equal(2, finalManifests.Length);
            Assert.All(frames, envelope => Assert.Single(finalManifests, manifest => manifest.EnvelopeId == envelope.EnvelopeId));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "processed"), "*.json").Length);
            foreach (var envelope in frames)
            {
                var restored = await archive.ReadAsync(archive.Manifests().Single(m => m.EnvelopeId == envelope.EnvelopeId), ct);
                Assert.Equal(RawSignalCodec.Encode(envelope), RawSignalCodec.Encode(restored));
            }
            TelemetryDbFixture.Evidence(label + "-recovered", new { before, interrupted, after, killedPid = victim.Id,
                restartPid = recovery.Id, recovered = recovered.RootElement, edges = final.Items, proof = detail.Evidence,
                archive = archive.Manifests().ToArray(), sameRoots = true });
        }
        finally
        {
            if (!victim.HasExited) victim.Kill(entireProcessTree: true);
            if (recovery is { HasExited: false }) recovery.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await victim.WaitForExitAsync(cleanup.Token);
            if (recovery is not null) await recovery.WaitForExitAsync(cleanup.Token);
            TelemetryDbFixture.Evidence(label + "-cleanup", new { killedPid = victim.Id, ownedPidAlive = !victim.HasExited,
                restartPid = recovery?.Id, restartAlive = recovery is { HasExited: false },
                stdout = await victimOut, stderr = await victimErr,
                restartStdout = recoveryOut is null ? null : await recoveryOut, restartStderr = recoveryErr is null ? null : await recoveryErr });
            recovery?.Dispose();
        }
    }

    private Process Start(string config, TelemetryDbFixture f)
    {
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
        return Process.Start(start) ?? throw new IOException("Could not start owned topology child.");
    }

    private static async Task<long> Scalar(TelemetryDbFixture f, string sql) =>
        long.Parse((await f.SqlAsync(sql)).Trim(), CultureInfo.InvariantCulture);

    private sealed record PublicationState(long Epoch, long PublishedSequence, ulong Watermark,
        long PendingCount, string? PendingKey, ulong? PendingSequence);

    private static async Task<PublicationState> State(TelemetryDbFixture f, CancellationToken ct)
    {
        await using var db = await f.Factory.CreateDbContextAsync(ct);
        var row = await db.TopologyReadState.AsNoTracking().SingleAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT publication_key, publication_sequence FROM bizigo.topology_publication_pending";
        await using var reader = await command.ExecuteReaderAsync(ct);
        string? key = null; ulong? sequence = null; long count = 0;
        while (await reader.ReadAsync(ct)) { count++; key = reader.GetString(0); sequence = checked((ulong)reader.GetInt64(1)); }
        return new(row.Epoch, row.PublishedSequence, await new TopologyPublicationWatermarkReader(f.Storage).ReadAsync(ct), count, key, sequence);
    }
}
