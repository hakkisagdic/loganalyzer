using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

public sealed class TopologyAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "topology-admission-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private readonly InMemoryObjectStore objects = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string ServiceNode = TopologyIdentity.Node(TopologyNodeKind.Service,
        Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("""
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"bizigo.source_key","value":{"stringValue":"SA"}},
          {"key":"service.namespace","value":{"stringValue":"n"}},
          {"key":"service.name","value":{"stringValue":"checkout"}}]},
          "scopeSpans":[{"spans":[{"traceId":"00112233445566778899aabbccddeeff",
          "spanId":"0011223344556677","name":"parent","kind":2,
          "startTimeUnixNano":"1000","endTimeUnixNano":"1001"}]}]}]}
        """);

    private SignalIngest Open(TopologyResolver topology) => new(new(), new(factory), objects,
        Options.Create(new SignalOptions { Directory = root, ObservedRetentionDays = 10 }),
        Options.Create(new WalOptions { Directory = root }), Options.Create(new RawStoreOptions()),
        NullLogger<WriteAheadLog>.Instance, owners: new OwnerResolver(), topology: topology);

    [Fact]
    public async Task Binding_snapshot_before_wal_ack_and_nonretroactive_replay()
    {
        var topology = new TopologyResolver { NodeId = ServiceNode };
        using var ingest = Open(topology);
        await ingest.RecoverAsync(Ct);
        var admitted = await ingest.AcceptAsync(TelemetrySignal.Traces, Payload, "application/json", Ct);
        Assert.Equal(200, admitted.Status);
        var work = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal(3, work.Envelope.Version);
        Assert.Equal("SA", Assert.Single(topology.Requests!).Owner.SourceId);
        Assert.Equal("n", Assert.Single(topology.Requests!).ServiceNamespace);
        Assert.Equal("checkout", Assert.Single(topology.Requests!).ServiceName);
        var binding = Assert.Single(work.Envelope.TopologyBindings!);
        Assert.Equal(ServiceNode, binding.NodeId);
        Assert.Equal(10, work.Envelope.ObservedRetentionDays);
        Assert.Equal(work.Envelope.ComputeTopologyBindingsHash(), work.Envelope.TopologyBindingsSha256);
        Assert.Equal(binding, Assert.Single(RawSignalCodec.Decode(RawSignalCodec.Encode(work.Envelope)).TopologyBindings!));
        await ingest.ProcessAsync(work, Ct);
        topology.NodeId = null; // A new alias state cannot reclassify the accepted event.
        await ingest.ReplayArchiveAsync(Ct);
        var archived = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        Assert.Equal(binding, Assert.Single(archived.TopologyBindings!));
        Assert.Equal(work.Envelope.TopologyBindingsSha256, archived.TopologyBindingsSha256);
    }

    [Fact]
    public async Task Negative_binding_remains_negative_after_later_alias()
    {
        var topology = new TopologyResolver();
        using var ingest = Open(topology);
        await ingest.RecoverAsync(Ct);
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Traces, Payload, "application/json", Ct)).Status);
        var work = await ingest.Reader.ReadAsync(Ct);
        Assert.Equal("MissingBinding", Assert.Single(work.Envelope.TopologyBindings!).Reason);
        await ingest.ProcessAsync(work, Ct);
        topology.NodeId = ServiceNode;
        await ingest.ReplayArchiveAsync(Ct);
        Assert.Equal("MissingBinding", Assert.Single((await ingest.Archive.ReadAsync(
            Assert.Single(ingest.Archive.Manifests()), Ct)).TopologyBindings!).Reason);
    }

    [Fact]
    public async Task Resolver_timeout_rejects_before_wal_fsync()
    {
        var topology = new TopologyResolver { Fail = true };
        using var ingest = Open(topology);
        await ingest.RecoverAsync(Ct);
        var before = ingest.Wal.TotalBytes;
        var admission = await ingest.AcceptAsync(TelemetrySignal.Traces, Payload, "application/json", Ct);
        Assert.Equal(503, admission.Status);
        Assert.Equal(before, ingest.Wal.TotalBytes);
        Assert.False(ingest.Reader.TryRead(out _));
        topology.Fail = false;
        topology.NodeId = ServiceNode;
        Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Traces, Payload, "application/json", Ct)).Status);
    }

    private sealed class OwnerResolver : ITelemetryOwnerResolver
    {
        public Task<TelemetryOwnerBinding[]> ResolveAsync(IReadOnlyList<TelemetryOwnershipRequest> requests,
            CancellationToken cancellationToken) => Task.FromResult(requests.Select(r =>
                new TelemetryOwnerBinding(r.LeafKey, "SA", "A", 7, r.EventTimeUnixNano, "known")).ToArray());
    }

    private sealed class TopologyResolver : ITopologyBindingResolver
    {
        public string? NodeId { get; set; }
        public bool Fail { get; set; }
        public IReadOnlyList<TopologyBindingRequest>? Requests { get; private set; }
        public Task<TopologyLeafBinding[]> ResolveAsync(IReadOnlyList<TopologyBindingRequest> requests,
            CancellationToken cancellationToken)
        {
            Requests = requests;
            if (Fail) throw new TimeoutException("Topology history unavailable.");
            return Task.FromResult(requests.Select(r => new TopologyLeafBinding(r.Owner.LeafKey,
                r.Owner.EventTimeUnixNano, r.Owner.SourceId, r.Owner.OwnerGroup, r.Owner.HistoryRevision,
                NodeId, null, NodeId is null ? null : 5, null, NodeId is null ? null : 9,
                "display", NodeId is null ? "MissingBinding" : "Resolved")).ToArray());
        }
    }

    public void Dispose() { factory.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
