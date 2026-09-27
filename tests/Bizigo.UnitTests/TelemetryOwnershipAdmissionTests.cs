using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class TelemetryOwnershipAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "telemetry-owner-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private SignalIngest Open(ITelemetryOwnerResolver? resolver = null, ITelemetrySink? sink = null) =>
        new(new(), new(factory), new InMemoryObjectStore(), Options.Create(new SignalOptions { Directory = root }),
            Options.Create(new WalOptions { Directory = root }), Options.Create(new RawStoreOptions { SegmentRetention = TimeSpan.Zero }),
            NullLogger<WriteAheadLog>.Instance, owners: resolver, sink: sink);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_failure_rejects_before_wal_and_recovers(bool timeout)
    {
        var resolver = new RecoveringResolver { Fail = true, Timeout = timeout };
        using var ingest = Open(resolver);
        await ingest.RecoverAsync(Ct);
        var before = ingest.Wal.TotalBytes;
        var rejected = await ingest.AcceptAsync(TelemetrySignal.Metrics, SignalDurabilityTests.Payload(), "application/json", Ct);
        Assert.Equal(503, rejected.Status);
        Assert.True(rejected.RetryAfterSeconds > 0);
        Assert.Equal(0, ingest.AcceptedBatches);
        Assert.Equal(before, ingest.Wal.TotalBytes);
        Assert.False(ingest.Reader.TryRead(out _));
        await using var db = factory.CreateDbContext();
        Assert.Empty(await db.TelemetryOwnerClaims.ToArrayAsync(Ct));
        resolver.Fail = false;
        var accepted = await ingest.AcceptAsync(TelemetrySignal.Metrics, SignalDurabilityTests.Payload(), "application/json", Ct);
        Assert.Equal(200, accepted.Status);
        var work = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal("A", Assert.Single(work.Envelope.OwnerBindings!).OwnerGroup);
        work.Envelope.Validate();
    }

    [Fact]
    public async Task Healthy_missing_history_is_durable_unassigned()
    {
        using var ingest = Open();
        await ingest.RecoverAsync(Ct);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
        var work = await ingest.Reader.ReadAsync(Ct);
        var before = Assert.Single(work.Envelope.OwnerBindings!);
        Assert.Equal("_unassigned", before.OwnerGroup);
        Assert.Equal("unknown", before.Reason);
        await ingest.ProcessAsync(work, Ct);
        await using (var db = factory.CreateDbContext())
        {
            db.Sources.Add(new SourceEntity { SourceId = "missing", OwnerGroup = "B" });
            await db.SaveChangesAsync(Ct);
        }
        await ingest.ReplayArchiveAsync(Ct);
        var archived = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        Assert.Equal(before, Assert.Single(archived.OwnerBindings!));
    }

    [Fact]
    public async Task Per_leaf_history_is_bound_before_ack_and_survives_transfer()
    {
        await using (var db = factory.CreateDbContext())
        {
            db.HistoryClock = new FixedClock(DateTimeOffset.UnixEpoch.AddSeconds(1));
            db.Sources.Add(new SourceEntity { SourceId = "device", OwnerGroup = "A" });
            await db.SaveChangesAsync(Ct);
        }
        await using (var db = factory.CreateDbContext())
        {
            db.HistoryClock = new FixedClock(DateTimeOffset.UnixEpoch.AddSeconds(2));
            (await db.Sources.SingleAsync(Ct)).OwnerGroup = "B";
            await db.SaveChangesAsync(Ct);
        }
        using var ingest = Open();
        await ingest.RecoverAsync(Ct);
        var payload = Encoding.UTF8.GetBytes("""
            {"resourceMetrics":[{"resource":{"attributes":[{"key":"bizigo.source_key","value":{"stringValue":"device"}},{"key":"owner_group","value":{"stringValue":"admin"}}]},"scopeMetrics":[{"metrics":[{"name":"boundary","gauge":{"dataPoints":[{"asInt":"1","timeUnixNano":"1999999999"},{"asInt":"2","timeUnixNano":"2000000000"},{"asInt":"3","timeUnixNano":"2000000001"}]}}]}]}]}
            """);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics, payload, "application/json", Ct)).Status);
        var work = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal(new[] { "A", "B", "B" }, work.Envelope.OwnerBindings!.Select(b => b.OwnerGroup));
        Assert.Equal(new ulong[] { 1999999999, 2000000000, 2000000001 }, work.Envelope.OwnerBindings!.Select(b => b.EventTimeUnixNano));
        var raw = RawSignalCodec.Encode(work.Envelope);
        Assert.Equal(work.Envelope.OwnerBindings, RawSignalCodec.Decode(raw).OwnerBindings);
        await ingest.ProcessAsync(work, Ct);
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Equal(work.Envelope.OwnerBindings, (await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct)).OwnerBindings);
    }

    [Fact]
    public async Task Database_delivery_failure_cannot_use_old_file_only_checkpoint_for_retention()
    {
        var sink = new FailingSink();
        using var ingest = Open(sink: sink);
        await ingest.RecoverAsync(Ct);
        await ingest.AcceptAsync(TelemetrySignal.Metrics, SignalDurabilityTests.Payload(), "application/json", Ct);
        var work = await ingest.Reader.ReadAsync(Ct);
        Directory.CreateDirectory(Path.Combine(root, "processed"));
        await File.WriteAllTextAsync(Path.Combine(root, "processed", work.Envelope.EnvelopeId.ToString("N") + ".json"), "{\"version\":1}", Ct);
        await Assert.ThrowsAsync<IOException>(() => ingest.ProcessAsync(work, Ct));
        await ingest.SweepAsync(Ct);
        Assert.NotEmpty(ingest.Wal.ListSealedSegments());
        sink.Fail = false;
        await ingest.ProcessAsync(work, Ct);
        await ingest.SweepAsync(Ct);
        Assert.Empty(ingest.Wal.ListSealedSegments());
        Assert.Equal(2, sink.Calls);
    }

    private sealed class FailingSink : ITelemetrySink
    {
        public bool Fail { get; set; } = true;
        public int Calls { get; private set; }
        public Task WriteAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        { Calls++; if (Fail) throw new IOException("Database unavailable."); return Task.CompletedTask; }
    }
    private sealed class RecoveringResolver : ITelemetryOwnerResolver
    {
        public bool Fail { get; set; }
        public bool Timeout { get; set; }
        public Task<TelemetryOwnerBinding[]> ResolveAsync(IReadOnlyList<TelemetryOwnershipRequest> requests, CancellationToken cancellationToken)
        {
            if (Fail) throw Timeout ? new TimeoutException("History timed out.") : new IOException("History unavailable.");
            return Task.FromResult(requests.Select(r => new TelemetryOwnerBinding(r.LeafKey, "source", "A", 1, r.EventTimeUnixNano, "known")).ToArray());
        }
    }
    internal sealed class FixedClock(DateTimeOffset value) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => value; }
    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
