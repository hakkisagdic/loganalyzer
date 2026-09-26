using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Common.V1;

namespace Bizigo.Ingest.Otlp;

public sealed record SignalAdmission(int Status, int Accepted = 0, int Rejected = 0,
    string? Error = null, int RetryAfterSeconds = 0, Guid? EnvelopeId = null);
public sealed record SignalWork(RawSignalEnvelope Envelope, string Segment);

/// <summary>Admission, archive and replay share one envelope and one decoder.</summary>
public sealed class SignalIngest : IDisposable
{
    private static readonly string[] SourceCandidates =
        ["bizigo.source_key", "service.instance.id", "host.id", "host.name", "service.name"];
    private readonly OtlpTelemetryDecoder decoder;
    private readonly SourceDirectory sources;
    private readonly IRawObjectStore objects;
    private readonly ISignalCheckpoints checkpoints;
    private readonly RawStoreOptions rawOptions;
    private readonly WalOptions walOptions;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim processing = new(1, 1);
    private readonly Channel<SignalWork> queue;
    private readonly FileStream lease;
    private long acceptedBatches, acceptedLeaves, rejectedFull, rejectedInvalid;
    private volatile bool ready;
    private string? lastFailure;

    public SignalIngest(OtlpTelemetryDecoder decoder, SourceDirectory sources, IRawObjectStore objects,
        IOptions<SignalOptions> options, IOptions<WalOptions> walOptions, IOptions<RawStoreOptions> rawOptions,
        ILogger<WriteAheadLog> logger, ISignalCheckpoints? checkpoints = null, IWalDurability? durability = null)
    {
        this.decoder = decoder; this.sources = sources; this.objects = objects;
        this.rawOptions = rawOptions.Value; this.walOptions = walOptions.Value;
        this.checkpoints = checkpoints ?? new SignalCheckpoints();
        if (options.Value.ChannelCapacity <= 0 || options.Value.RetryInterval <= TimeSpan.Zero)
            throw new ArgumentException("Signal queue capacity and retry interval must be positive.");
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.Directory)
            ? Path.Combine(walOptions.Value.Directory, "telemetry") : options.Value.Directory);
        Directory.CreateDirectory(Root);
        lease = new FileStream(Path.Combine(Root, ".writer-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Wal = new WriteAheadLog(Options.Create(new WalOptions
            {
                Directory = Path.Combine(Root, "wal"),
                MaxSegmentBytes = walOptions.Value.MaxSegmentBytes,
                MaxTotalBytes = walOptions.Value.MaxTotalBytes,
                RetryAfterSeconds = walOptions.Value.RetryAfterSeconds,
                // Signal ACKs cannot opt out of durability, even for benchmarks.
                FlushToDisk = true, StrictRecovery = true,
            }), logger, durability);
        }
        catch { lease.Dispose(); throw; }
        Archive = new SignalArchive(objects, Root, this.rawOptions.CompressionLevel);
        slots = new SemaphoreSlim(options.Value.ChannelCapacity, options.Value.ChannelCapacity);
        queue = Channel.CreateBounded<SignalWork>(options.Value.ChannelCapacity);
    }

    public string Root { get; }
    public WriteAheadLog Wal { get; }
    public SignalArchive Archive { get; }
    public ChannelReader<SignalWork> Reader => queue.Reader;
    public bool Ready => ready && Wal.Failure is null;
    public string? LastFailure => Wal.Failure ?? Volatile.Read(ref lastFailure);
    public long AcceptedBatches => Interlocked.Read(ref acceptedBatches);
    public long AcceptedLeaves => Interlocked.Read(ref acceptedLeaves);
    public long RejectedFull => Interlocked.Read(ref rejectedFull);
    public long RejectedInvalid => Interlocked.Read(ref rejectedInvalid);

    public async Task<SignalAdmission> AcceptAsync(TelemetrySignal signal, ReadOnlyMemory<byte> payload,
        string contentType, CancellationToken token)
    {
        TelemetryDecode decoded;
        try { decoded = decoder.Decode(signal, payload, contentType); }
        catch (OtlpDecodeException)
        {
            Interlocked.Increment(ref rejectedInvalid);
            return new(400, Error: "Invalid OTLP export.");
        }
        if (decoded.Accepted.Count + decoded.RejectedCount == 0) return new(200);
        if (!Ready || !slots.Wait(0)) return Full("Signal ingest is recovering, degraded or its queue is full.");
        var enqueued = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var envelope = new RawSignalEnvelope(1, Guid.CreateVersion7(), signal, contentType, DateTimeOffset.UtcNow,
                RawSignalEnvelope.Hash(payload.Span), payload.ToArray(), 1,
                decoded.Accepted.Select(x => x.Key).ToArray(), decoded.RejectedCount);
            var segment = await Wal.AppendAsync(RawSignalCodec.Encode(envelope), token);
            await checkpoints.ReachAsync("after-wal-before-ack", token);
            if (!queue.Writer.TryWrite(new(envelope, segment.Path)))
                throw new IOException("Reserved telemetry admission slot could not be published.");
            enqueued = true;
            Interlocked.Increment(ref acceptedBatches);
            Interlocked.Add(ref acceptedLeaves, decoded.Accepted.Count);
            return new(200, decoded.Accepted.Count, decoded.RejectedCount,
                decoded.RejectedCount > 0 ? "Invalid telemetry leaves rejected." : null, EnvelopeId: envelope.EnvelopeId);
        }
        catch (WalFullException) { return Full("Signal WAL capacity exhausted."); }
        catch (IOException ex)
        {
            SetFailure(ex);
            return new(500, Error: "Telemetry durability failed.");
        }
        finally { if (!enqueued) slots.Release(); }
    }

    private SignalAdmission Full(string reason)
    {
        Interlocked.Increment(ref rejectedFull);
        return new(503, Error: reason, RetryAfterSeconds: Math.Max(1, walOptions.RetryAfterSeconds));
    }

    public void SetFailure(Exception error) => Volatile.Write(ref lastFailure, error.GetType().Name + ": " + error.Message);

    public async Task RecoverAsync(CancellationToken token)
    {
        ready = false;
        try
        {
            await objects.EnsureBucketAsync(token);
            await sources.RefreshAsync(token);
            await Wal.SealAsync(token);
            foreach (var segment in Wal.ListSealedSegments())
                foreach (var bytes in WriteAheadLog.ReadFrames(segment.Path, strict: true))
                    await ProcessAsync(new(RawSignalCodec.Decode(bytes.Span), segment.Path), token);
            ready = true;
            Volatile.Write(ref lastFailure, null);
        }
        catch (Exception ex) { SetFailure(ex); throw; }
    }

    public async Task ProcessAsync(SignalWork work, CancellationToken token)
    {
        await processing.WaitAsync(token);
        try
        {
            var decoded = decoder.Replay(work.Envelope);
            await Archive.ArchiveAsync(work.Envelope, work.Segment, token, checkpoints.ReachAsync);
            await WriteResultAsync(work.Envelope, decoded, token);
            Volatile.Write(ref lastFailure, null);
        }
        catch (Exception ex) { SetFailure(ex); throw; }
        finally { processing.Release(); }
    }

    public void CompleteWork() => slots.Release();

    /// <summary>Explicit operational replay; output file is also its atomic checkpoint.</summary>
    public async Task ReplayArchiveAsync(CancellationToken token)
    {
        var acquired = false;
        try
        {
            await sources.RefreshAsync(token);
            await processing.WaitAsync(token);
            acquired = true;
            foreach (var manifest in Archive.Manifests())
            {
                var envelope = await Archive.ReadAsync(manifest, token);
                await WriteResultAsync(envelope, decoder.Replay(envelope), token);
            }
            Volatile.Write(ref lastFailure, null);
        }
        catch (Exception ex) { SetFailure(ex); throw; }
        finally { if (acquired) processing.Release(); }
    }

    private async Task WriteResultAsync(RawSignalEnvelope envelope, TelemetryDecode decoded, CancellationToken token)
    {
        var leaves = new JsonArray();
        foreach (var leaf in decoded.Accepted)
        {
            var attributes = leaf.Resource.Attributes;
            var candidates = SourceCandidates.Select(name => attributes.FirstOrDefault(a => a.Key == name)?.Value)
                .Where(v => v?.ValueCase == AnyValue.ValueOneofCase.StringValue).Select(v => v!.StringValue);
            var source = sources.ResolveTelemetry(candidates);
            leaves.Add(new JsonObject
            {
                ["logical_id"] = envelope.EnvelopeId.ToString("N") + "/" + leaf.Key,
                ["key"] = leaf.Key, ["owner_group"] = source.OwnerGroup, ["source_id"] = source.SourceId,
                ["known_source"] = source.IsKnown,
                ["resource"] = OtlpJsonCodec.Format(leaf.Resource), ["scope"] = OtlpJsonCodec.Format(leaf.Scope),
                ["resource_schema_url"] = leaf.ResourceSchemaUrl, ["scope_schema_url"] = leaf.ScopeSchemaUrl,
                ["metric"] = leaf.Metric is null ? null : OtlpJsonCodec.Format(leaf.Metric),
                ["span"] = leaf.Span is null ? null : OtlpJsonCodec.Format(leaf.Span),
            });
        }
        var result = new JsonObject
        {
            ["version"] = 1, ["envelope_id"] = envelope.EnvelopeId.ToString("N"),
            ["signal"] = envelope.Signal.ToString(), ["payload_sha256"] = envelope.PayloadSha256,
            ["received_at"] = envelope.ReceivedAt, ["rejected_count"] = envelope.RejectedCount, ["leaves"] = leaves,
        };
        // Stable filename is the idempotency key. Result and checkpoint commit
        // together; there is no marker that can advance ahead of durable data.
        var path = Path.Combine(Root, "processed", envelope.EnvelopeId.ToString("N") + ".json");
        await DurableFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(result), token, checkpoints.ReachAsync);
    }

    public async Task SweepAsync(CancellationToken token)
    {
        await processing.WaitAsync(token);
        try
        {
            await Wal.SealAsync(token);
            var manifests = Archive.Manifests().ToDictionary(m => m.EnvelopeId);
            var cutoff = DateTimeOffset.UtcNow - rawOptions.SegmentRetention;
            foreach (var segment in Wal.ListSealedSegments())
            {
                var envelopes = WriteAheadLog.ReadFrames(segment.Path, strict: true).Select(b => RawSignalCodec.Decode(b.Span)).ToArray();
                if (envelopes.Length == 0 || envelopes.Any(e => !manifests.TryGetValue(e.EnvelopeId, out var m)
                    || m.VerifiedAt > cutoff || !File.Exists(Path.Combine(Root, "processed", e.EnvelopeId.ToString("N") + ".json")))) continue;
                foreach (var envelope in envelopes) await Archive.ReadAsync(manifests[envelope.EnvelopeId], token);
                Wal.Delete(segment);
            }
        }
        finally { processing.Release(); }
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        try { Wal.Dispose(); }
        finally { lease.Dispose(); slots.Dispose(); processing.Dispose(); }
    }
}
