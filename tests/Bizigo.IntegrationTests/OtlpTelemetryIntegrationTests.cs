using System.Text;
using System.Text.Json.Nodes;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.IntegrationTests;

/// <summary>When Planner runs this gate it proves real PostgreSQL inventory and
/// S3 archive read-back/replay after local WAL removal, including raw-log isolation.</summary>
[Collection(DevStackCollection.Name)]
public sealed class OtlpTelemetryIntegrationTests(DevStackFixture stack)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task Real_inventory_and_S3_preserve_signal_envelope_after_wal_retention(bool traces)
    {
        var token = TestContext.Current.CancellationToken;
        var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
        await using (var db = await factory.CreateDbContextAsync(token))
        {
            db.Sources.Add(new SourceEntity { SourceId = "telemetry-known", OwnerGroup = "server-group" });
            await db.SaveChangesAsync(token);
        }
        var options = DevStackSetup.RawOptions(stack);
        options.SegmentRetention = TimeSpan.Zero;
        using var store = new S3RawObjectStore(Options.Create(options));
        var root = Path.Combine(Path.GetTempPath(), "otlp-integration-" + Guid.NewGuid().ToString("N"));
        var body = traces ? """
            {"resourceSpans":[{"resource":{"attributes":[{"key":"bizigo.source_key","value":{"stringValue":"telemetry-known"}},{"key":"owner_group","value":{"stringValue":"attacker"}}]},"scopeSpans":[{"spans":[{"name":"span","traceId":"11111111111111111111111111111111","spanId":"1111111111111111","parentSpanId":"2222222222222222","startTimeUnixNano":"1","endTimeUnixNano":"2","events":[{"name":"event","timeUnixNano":"1"}],"links":[{"traceId":"33333333333333333333333333333333","spanId":"3333333333333333"}]}]}]}]}
            """ : """
            {"resourceMetrics":[{"resource":{"attributes":[{"key":"bizigo.source_key","value":{"stringValue":"telemetry-known"}},{"key":"owner_group","value":{"stringValue":"attacker"}}]},"scopeMetrics":[{"metrics":[{"name":"histogram","histogram":{"dataPoints":[{"count":"3","sum":8,"bucketCounts":["1","2"],"explicitBounds":[4]}]}}]}]}]}
            """;
        // This fixture verifies known inventory ownership. Event-time history
        // requires a current event after the source was committed, not epoch 1ns.
        var timestamp = checked((ulong)(DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
        var document = JsonNode.Parse(body)!;
        if (traces)
        {
            var span = document["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!;
            span["startTimeUnixNano"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
            span["endTimeUnixNano"] = (timestamp + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else document["resourceMetrics"]![0]!["scopeMetrics"]![0]!["metrics"]![0]!["histogram"]!["dataPoints"]![0]!["timeUnixNano"] = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        SignalIngest Open() => new(new(), new SourceDirectory(factory), store,
            Options.Create(new SignalOptions { Directory = root }), Options.Create(new WalOptions { Directory = root }),
            Options.Create(options), NullLogger<WriteAheadLog>.Instance);
        try
        {
            Guid id;
            using (var ingest = Open())
            {
                await ingest.RecoverAsync(token);
                var admission = await ingest.AcceptAsync(traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics, bytes, "application/json", token);
                Assert.Equal(200, admission.Status);
                id = admission.EnvelopeId!.Value;
                await ingest.ProcessAsync(await ingest.Reader.ReadAsync(token), token);
                var manifest = Assert.Single(ingest.Archive.Manifests());
                var archived = await ingest.Archive.ReadAsync(manifest, token);
                Assert.Equal(bytes, archived.Payload);
                Assert.Equal(RawSignalEnvelope.Hash(bytes), archived.PayloadSha256);
                Assert.StartsWith("otlp/v1/", manifest.ObjectKey, StringComparison.Ordinal);
                await ingest.SweepAsync(token);
                Assert.Empty(ingest.Wal.ListSealedSegments());
            }
            Directory.Delete(Path.Combine(root, "processed"), true);
            using (var reopened = Open())
            {
                await reopened.RecoverAsync(token);
                await Task.WhenAll(reopened.ReplayArchiveAsync(token), reopened.ReplayArchiveAsync(token));
                var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "processed"), "*.json"));
                var result = JsonNode.Parse(await File.ReadAllBytesAsync(path, token))!;
                Assert.Equal(id.ToString("N"), result["envelope_id"]!.GetValue<string>());
                var leaf = Assert.Single(result["leaves"]!.AsArray())!;
                Assert.Equal("server-group", leaf["owner_group"]!.GetValue<string>());
                if (traces)
                {
                    Assert.Equal("2222222222222222", leaf["span"]!["parentSpanId"]!.GetValue<string>());
                    Assert.Single(leaf["span"]!["events"]!.AsArray());
                    Assert.Single(leaf["span"]!["links"]!.AsArray());
                }
                else Assert.Equal("2", leaf["metric"]!["histogram"]!["dataPoints"]![0]!["bucketCounts"]![1]!.GetValue<string>());
            }
            await using var check = await factory.CreateDbContextAsync(token);
            Assert.Equal(0, await check.RawManifest.CountAsync(token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
