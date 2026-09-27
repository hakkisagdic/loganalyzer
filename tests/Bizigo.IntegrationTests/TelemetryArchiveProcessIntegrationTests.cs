using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: archived objects + manifests restore into an actual
/// fresh process, empty local root, empty ClickHouse database and empty claims.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TelemetryArchiveProcessIntegrationTests(DevStackFixture stack)
{
    [Fact, Trait("Category", "Integration")]
    public async Task Fresh_process_and_database_restore_only_archives_without_current_owner_guess()
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, token);
        await f.SourceAsync("archive-A", "A");
        var saved = new List<RawSignalEnvelope>();
        var root = Path.Combine(f.Root, "fresh-process"); Directory.CreateDirectory(root);
        using (var ingest = f.Open())
        {
            await ingest.RecoverAsync(token);
            foreach (var source in new[] { "archive-A", "archive-unknown" })
            {
                saved.Add(await f.EmitAsync(ingest, TelemetryDbFixture.Metrics(source, f.Now, false), TelemetrySignal.Metrics));
                saved.Add(await f.EmitAsync(ingest, TelemetryDbFixture.Traces(source, f.Now), TelemetrySignal.Traces));
            }
            await ingest.SweepAsync(token); Assert.Empty(ingest.Wal.ListSealedSegments());
            Directory.CreateDirectory(Path.Combine(root, "manifests"));
            foreach (var file in Directory.GetFiles(ingest.Archive.ManifestDirectory, "*.json"))
                File.Copy(file, Path.Combine(root, "manifests", Path.GetFileName(file)));
        }
        await f.SourceAsync("archive-A", "B", f.Clock.GetUtcNow().AddSeconds(1));
        await f.SourceAsync("archive-unknown", "B", f.Clock.GetUtcNow().AddSeconds(1));
        await f.Db.TelemetryOwnerClaims.ExecuteDeleteAsync(token);
        using var fresh = await DevStackSetup.ClickHouseAsync(stack, token);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var tokens = new Dictionary<string, string> { ["restore-A"] = "A", ["restore-B"] = "B", ["restore-U"] = "_unassigned", ["restore-admin"] = "admin" };
        var config = Path.Combine(root, "host.json");
        await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new { port, root, gate = "none", tokens }), token);
        var start = new ProcessStartInfo(Environment.ProcessPath is { } processPath && Path.GetFileNameWithoutExtension(processPath) == "dotnet" ? processPath : "dotnet")
        { WorkingDirectory = DevStackSetup.RepoPath(""), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(DevStackSetup.RepoPath("sim/Bizigo.OtlpFixture/bin/Release/net10.0/Bizigo.OtlpFixture.dll"));
        start.ArgumentList.Add("--telemetry-query"); start.ArgumentList.Add(config);
        start.Environment["ConnectionStrings__ControlPlane"] = stack.PostgresConnectionString;
        start.Environment["ConnectionStrings__ClickHouse"] = fresh.Options.ConnectionString;
        start.Environment["RawStore__ServiceUrl"] = f.RawOptions.ServiceUrl;
        start.Environment["RawStore__Bucket"] = f.RawOptions.Bucket;
        start.Environment["RawStore__AccessKey"] = f.RawOptions.AccessKey;
        start.Environment["RawStore__SecretKey"] = f.RawOptions.SecretKey;
        using var child = Process.Start(start) ?? throw new IOException("Could not start restore fixture.");
        var stdout = child.StandardOutput.ReadToEndAsync(token); var stderr = child.StandardError.ReadToEndAsync(token);
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "restore-admin");
        var pid = child.Id;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                Assert.False(child.HasExited, "Fresh restore process exited before readiness.");
                try
                {
                    using var ready = await http.GetAsync("/fixture/ready", budget.Token);
                    if (ready.IsSuccessStatusCode && (await ready.Content.ReadAsStringAsync(budget.Token)).Contains("true", StringComparison.Ordinal)) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100, budget.Token);
            }
            Assert.False(Directory.Exists(Path.Combine(root, "processed")));
            using var replay = await http.PostAsync("/fixture/replay", new StringContent("{}"), token);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            foreach (var signal in new[] { TelemetrySignal.Metrics, TelemetrySignal.Traces })
                foreach (var pair in new[] { ("restore-A", "A", 1), ("restore-U", "_unassigned", 1), ("restore-B", "B", 0) })
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.Item1);
                    using var response = await http.PostAsync("/fixture/query/list", new StringContent(JsonSerializer.Serialize(f.Window(signal), RawSignalCodec.Json), Encoding.UTF8, "application/json"), token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var page = JsonSerializer.Deserialize<TelemetryPage>(await response.Content.ReadAsStringAsync(token), RawSignalCodec.Json)!;
                    Assert.Equal(pair.Item3, page.Records.Count);
                    foreach (var row in page.Records)
                    {
                        var original = saved.Single(e => e.EnvelopeId == row.EnvelopeId);
                        Assert.Equal(original.OwnerBindings![0], row.Owner); Assert.Equal(original.PayloadSha256, row.PayloadSha256);
                        Assert.Equal(pair.Item2, row.Owner.OwnerGroup);
                    }
                }
            Assert.Equal(4, await f.Db.TelemetryOwnerClaims.CountAsync(token));
            TelemetryDbFixture.Evidence("fresh-process-restore", new { pid, envelopes = saved.Select(e => e.EnvelopeId), freshDatabase = true, copied = "manifests + verified S3 objects only", localWalCopied = false, currentOwner = "B" });
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await child.WaitForExitAsync(stop.Token);
            TelemetryDbFixture.Evidence("fresh-process-restore-cleanup", new { pid, exited = child.HasExited, stdout = await stdout, stderr = await stderr });
        }
    }
}
