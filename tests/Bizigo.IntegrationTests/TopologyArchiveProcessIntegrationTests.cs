using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.IntegrationTests;

/// <summary>O06: only verified S3 objects, local manifests and historical registry
/// survive. A new OS process rebuilds a fresh CH projection without any WAL.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyArchiveProcessIntegrationTests(DevStackFixture stack)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_only_fresh_process_preserves_historical_owner_and_one_proof(bool childFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, ct);
        await f.SourceAsync("archive-parent", "A");
        await f.SourceAsync("archive-child", "A");
        var registry = new TopologyRegistry(f.Factory);
        var scope = AccessScope.ForGroups("o06-owner", ["A"]);
        var parent = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "parent", "A", true,
            [new("archive-parent", "", "parent")]), ct);
        var child = await registry.CreateAsync(scope, true, new(TopologyNodeKind.Service, "child", "A", true,
            [new("archive-child", "", "child")]), ct);
        Assert.Equal(201, parent.Status); Assert.Equal(201, child.Status);
        var eventTime = checked((ulong)Math.Max(decimal.Parse(parent.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture),
            decimal.Parse(child.Node!.ValidFromUnixNano, CultureInfo.InvariantCulture)) + 1000UL);
        var parentExport = TelemetryDbFixture.Traces("archive-parent", eventTime);
        var childExport = TelemetryDbFixture.Traces("archive-child", eventTime + 1);
        foreach (var (export, service) in new[] { (parentExport, "parent"), (childExport, "child") })
            export.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = service;
        var parentSpan = parentExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        var childSpan = childExport.ResourceSpans[0].ScopeSpans[0].Spans[0];
        parentSpan.ParentSpanId = ByteString.Empty;
        childSpan.SpanId = ByteString.CopyFrom(Convert.FromHexString("0000000000000002"));
        childSpan.ParentSpanId = parentSpan.SpanId;
        var request = new TopologyEdgeQuery(eventTime + 1000000000m, Provenance: TopologyProvenance.Observed);
        var root = Path.Combine(f.Root, "archive-only");
        var manifests = Path.Combine(root, "manifests"); Directory.CreateDirectory(manifests);
        var envelopes = new List<RawSignalEnvelope>();
        TopologyEdgeDetail original;
        await using (var originalServices = Services(f.Storage))
        {
            using var ingest = f.Open(sink: originalServices.GetRequiredService<ITelemetrySink>());
            await ingest.RecoverAsync(ct);
            foreach (var export in childFirst ? new[] { childExport, parentExport } : new[] { parentExport, childExport })
                envelopes.Add(await f.EmitAsync(ingest, export, TelemetrySignal.Traces));
            await using var read = originalServices.CreateAsyncScope();
            var query = read.ServiceProvider.GetRequiredService<IScopedQuery>();
            var edge = Assert.Single((await query.SearchTopologyEdgesAsync(request, scope, ct)).Items);
            original = (await query.GetTopologyEdgeAsync(edge.Id, request.ReadClockUnixNano, scope, ct))!;
            Assert.NotNull(original); Assert.Equal(2, original.Evidence.Count);
            foreach (var manifest in ingest.Archive.Manifests())
                await ingest.Archive.ReadAsync(manifest, ct);
            foreach (var file in Directory.GetFiles(ingest.Archive.ManifestDirectory, "*.json"))
                File.Copy(file, Path.Combine(manifests, Path.GetFileName(file)));
        }
        Assert.Equal(2, Directory.GetFiles(manifests, "*.json").Length);
        Assert.False(Directory.Exists(Path.Combine(root, "wal")));
        Assert.False(Directory.Exists(Path.Combine(root, "processed")));
        // Current ownership disagrees with the immutable event-time snapshot.
        // Rebuilding must never resolve these envelopes against today's owner.
        await f.SourceAsync("archive-parent", "B", DateTimeOffset.UtcNow.AddSeconds(1));
        await f.SourceAsync("archive-child", "B", DateTimeOffset.UtcNow.AddSeconds(1));
        using var fresh = await DevStackSetup.ClickHouseAsync(stack, ct);
        await using (var db = await f.Factory.CreateDbContextAsync(ct))
        {
            // Restore input retains registry history, not derived projection receipts.
            // Reset PG publication state together with the empty CH publication store.
            await using var reset = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM bizigo.topology_publication_pending;
                DELETE FROM bizigo.topology_publication_receipts;
                UPDATE bizigo.topology_read_state SET published_sequence = 0, epoch = epoch + 1;
                """, ct);
            await db.TelemetryOwnerClaims.ExecuteDeleteAsync(ct);
            await reset.CommitAsync(ct);
            Assert.True(await db.TopologyBindings.AnyAsync(ct));
            Assert.True(await db.SourceOwnershipHistory.AnyAsync(ct));
        }
        var config = Path.Combine(f.Root, "archive-recover.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
        { root, stage = "after-wal-before-ack", recover = true, first = "", second = "" }), ct);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = DevStackSetup.RepoPath(""),
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(DevStackSetup.RepoPath("sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"));
        start.ArgumentList.Add("--topology-crash"); start.ArgumentList.Add(config);
        start.Environment["ConnectionStrings__ControlPlane"] = stack.PostgresConnectionString;
        start.Environment["ConnectionStrings__ClickHouse"] = fresh.Options.ConnectionString;
        start.Environment["RawStore__ServiceUrl"] = f.RawOptions.ServiceUrl;
        start.Environment["RawStore__Bucket"] = f.RawOptions.Bucket;
        start.Environment["RawStore__AccessKey"] = f.RawOptions.AccessKey;
        start.Environment["RawStore__SecretKey"] = f.RawOptions.SecretKey;
        using var childProcess = Process.Start(start) ?? throw new IOException("Could not start archive-only child.");
        var stdout = childProcess.StandardOutput.ReadToEndAsync(ct);
        var stderr = childProcess.StandardError.ReadToEndAsync(ct);
        var label = "o06-archive-only-" + (childFirst ? "child-first" : "parent-first");
        try
        {
            Assert.NotEqual(Environment.ProcessId, childProcess.Id);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            { timeout.CancelAfter(TimeSpan.FromSeconds(90)); await childProcess.WaitForExitAsync(timeout.Token); }
            Assert.True(childProcess.ExitCode == 0, await stderr);
            using var recovery = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(root, "recovered.json"), ct));
            Assert.Equal(childProcess.Id, recovery.RootElement.GetProperty("pid").GetInt32());
            Assert.True(recovery.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal(2, recovery.RootElement.GetProperty("verified").GetArrayLength());
            Assert.Equal(2, recovery.RootElement.GetProperty("replayCount").GetInt32());
            var archive = new SignalArchive(f.Objects, root);
            Assert.Equal(2, archive.Manifests().Count());
            foreach (var envelope in envelopes)
            {
                var restored = await archive.ReadAsync(archive.Manifests().Single(m => m.EnvelopeId == envelope.EnvelopeId), ct);
                Assert.Equal(RawSignalCodec.Encode(envelope), RawSignalCodec.Encode(restored));
                Assert.Equal(envelope.TopologyBindingsSha256, restored.TopologyBindingsSha256);
                Assert.Equal(envelope.OwnerBindingsSha256, restored.OwnerBindingsSha256);
            }
            // The host may open an empty WAL, but recovery cannot have read a frame.
            var wal = Path.Combine(root, "wal");
            if (Directory.Exists(wal))
                Assert.All(Directory.GetFiles(wal, "*.log"), p => Assert.Equal(0L, new FileInfo(p).Length));
            await using var restoredServices = Services(fresh);
            await using var restoredScope = restoredServices.CreateAsyncScope();
            var query = restoredScope.ServiceProvider.GetRequiredService<IScopedQuery>();
            var edge = Assert.Single((await query.SearchTopologyEdgesAsync(request, scope, ct)).Items);
            Assert.Equal(original.Edge.Id, edge.Id);
            Assert.Equal(parent.Node!.Id, edge.FromNode); Assert.Equal(child.Node!.Id, edge.ToNode);
            Assert.Equal("A", edge.FromOwnerGroup); Assert.Equal("A", edge.ToOwnerGroup);
            Assert.True(edge.Directed); Assert.Equal(TopologyProvenance.Observed, edge.Provenance);
            var detail = await query.GetTopologyEdgeAsync(edge.Id, request.ReadClockUnixNano, scope, ct);
            Assert.NotNull(detail);
            Assert.Equal(original.Evidence.OrderBy(e => e.Id, StringComparer.Ordinal), detail.Evidence.OrderBy(e => e.Id, StringComparer.Ordinal));
            Assert.Equal(2, detail.Evidence.Count);
            Assert.Null(detail.EvidenceCursor);
            Assert.Empty((await query.SearchTopologyEdgesAsync(request, AccessScope.ForGroups("o06-B", ["B"]), ct)).Items);
            Assert.Equal(2, await f.Db.TelemetryOwnerClaims.CountAsync(ct));
            TelemetryDbFixture.Evidence(label, new { pid = childProcess.Id, childFirst,
                localWalCopied = false, freshDatabase = true, currentOwner = "B", historicalOwner = "A",
                verified = recovery.RootElement, originalEdge = original.Edge, edge, evidence = detail.Evidence,
                envelopeIds = envelopes.Select(e => e.EnvelopeId) });
        }
        finally
        {
            if (!childProcess.HasExited) childProcess.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await childProcess.WaitForExitAsync(cleanup.Token);
            TelemetryDbFixture.Evidence(label + "-cleanup", new { pid = childProcess.Id, ownedPidAlive = !childProcess.HasExited,
                stdout = await stdout, stderr = await stderr });
        }
    }

    private ServiceProvider Services(ClickHouseContext storage)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddControlPlane(stack.PostgresConnectionString);
        services.AddBizigoDataPlane(storage.Options);
        return services.BuildServiceProvider();
    }
}
