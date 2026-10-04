using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bizigo.UnitTests;

/// <summary>Real SignalIngest/WAL; controlled readiness and observable recovery I/O. No DB certificate claim.</summary>
public sealed class TopologyHostedActivationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "topology-activation-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryControlPlaneFactory factory = new();
    private readonly CountingStore store = new(new InMemoryObjectStore());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IOptions<SignalOptions> OptionsForService => Options.Create(new SignalOptions
    { Directory = root, RetryInterval = TimeSpan.FromMilliseconds(10) });

    private SignalIngest Open() => new(new(), new(factory), store, OptionsForService,
        Options.Create(new WalOptions { Directory = root }),
        Options.Create(new RawStoreOptions { SegmentRetention = TimeSpan.Zero }),
        NullLogger<WriteAheadLog>.Instance);

    [Fact]
    public async Task Initial_not_ready_retries_without_recovery_WAL_or_admission()
    {
        using var ingest = Open();
        var gate = new ControlledGate(rejectFirst: true);
        using var service = new SignalIngestService(ingest, OptionsForService,
            NullLogger<SignalIngestService>.Instance, gate);
        await service.StartAsync(Ct);
        try
        {
            // The second gate call proves the typed failure was retried rather
            // than faulting the service or falling through to recovery.
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(2, gate.Calls);
            Assert.Equal(0, store.EnsureCalls);
            Assert.False(ingest.Ready);
            Assert.False(service.ExecuteTask!.IsCompleted);
            Assert.Equal(503, (await ingest.AcceptAsync(TelemetrySignal.Metrics,
                SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
            Assert.Equal(0, ingest.Wal.TotalBytes);
            Assert.False(ingest.Reader.TryRead(out _));
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task Later_ready_starts_real_recovery_and_reopens_admission()
    {
        using var ingest = Open();
        var gate = new ControlledGate(rejectFirst: true);
        using var service = new SignalIngestService(ingest, OptionsForService,
            NullLogger<SignalIngestService>.Instance, gate);
        await service.StartAsync(Ct);
        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.Equal(0, store.EnsureCalls);
            Assert.False(ingest.Ready);
            gate.Release.TrySetResult();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (!ingest.Ready) await Task.Delay(10, deadline.Token);
            Assert.Equal(1, store.EnsureCalls);
            Assert.Null(ingest.LastFailure);
            Assert.Equal(200, (await ingest.AcceptAsync(TelemetrySignal.Metrics,
                SignalDurabilityTests.Payload(), "application/json", Ct)).Status);
            Assert.Equal(1, ingest.AcceptedBatches);
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public async Task Cancellation_reaches_waiting_readiness_without_starting_recovery()
    {
        using var ingest = Open();
        var gate = new ControlledGate(rejectFirst: false);
        using var service = new SignalIngestService(ingest, OptionsForService,
            NullLogger<SignalIngestService>.Instance, gate);
        await service.StartAsync(Ct);
        try
        {
            await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await service.StopAsync(Ct);
            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteTask!.WaitAsync(Ct));
            Assert.Equal(1, gate.Calls);
            Assert.Equal(0, store.EnsureCalls);
            Assert.False(ingest.Ready);
            Assert.Equal(0, ingest.Wal.TotalBytes);
        }
        finally { await service.StopAsync(Ct); }
    }

    [Fact]
    public void Missing_readiness_is_rejected_instead_of_becoming_implicitly_ready()
    {
        using var ingest = Open();
        Assert.Throws<ArgumentNullException>(() => new SignalIngestService(ingest,
            OptionsForService, NullLogger<SignalIngestService>.Instance, null!));
        Assert.Equal(0, store.EnsureCalls);
        Assert.False(ingest.Ready);
    }

    private sealed class ControlledGate(bool rejectFirst) : ITopologyObservedRepairReadiness
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TopologyRepairReadStamp> RequireReadyAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (rejectFirst && call == 1) throw new TopologyObservedRepairUnavailableException();
            Waiting.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { Cancelled.TrySetResult(); throw; }
            return new(1, "controlled-unit-readiness-not-a-storage-certificate");
        }
    }

    private sealed class CountingStore(IRawObjectStore inner) : IRawObjectStore
    {
        private int ensureCalls;
        public int EnsureCalls => Volatile.Read(ref ensureCalls);
        public Task EnsureBucketAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref ensureCalls); return inner.EnsureBucketAsync(cancellationToken); }
        public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            inner.PutAsync(key, content, cancellationToken);
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => inner.GetAsync(key, cancellationToken);
        public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) => inner.HeadAsync(key, cancellationToken);
    }

    public void Dispose()
    {
        factory.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
