using System.Text.Json;
using System.Text.Json.Nodes;
using System.Data.Common;
using System.Threading.Channels;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Otlp;

public sealed record SignalAdmission(int Status, int Accepted = 0, int Rejected = 0,
    string? Error = null, int RetryAfterSeconds = 0, Guid? EnvelopeId = null);
public sealed record SignalWork(RawSignalEnvelope Envelope, string Segment);

/// <summary>Admission, archive and replay share one envelope and one decoder.</summary>
public sealed class SignalIngest : IDisposable
{
    private readonly OtlpTelemetryDecoder decoder;
    private readonly SourceDirectory sources;
    private readonly IRawObjectStore objects;
    private readonly ISignalCheckpoints checkpoints;
    private readonly RawStoreOptions rawOptions;
    private readonly WalOptions walOptions;
    private readonly ITelemetryOwnerResolver owners;
    private readonly ITelemetryBindingRegistry bindings;
    private readonly ITopologyBindingResolver topology;
    private readonly ITelemetrySink? sink;
    private readonly TelemetryRetentionPolicy retention;
    private readonly int observedRetentionDays;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim processing = new(1, 1);
    private readonly Channel<SignalWork> queue;
    private readonly FileStream lease;
    private long acceptedBatches, acceptedLeaves, rejectedFull, rejectedInvalid;
    private volatile bool ready;
    private string? lastFailure;
    // Archive integrity is separate from transient operational failures. Only a
    // complete successful archive verification/replay clears this latch, never
    // successful processing of an unrelated queued envelope. Recovery verifies
    // the archive before opening admission, including in a new process.
    private string? archiveIntegrityFailure;

    public SignalIngest(OtlpTelemetryDecoder decoder, SourceDirectory sources, IRawObjectStore objects,
        IOptions<SignalOptions> options, IOptions<WalOptions> walOptions, IOptions<RawStoreOptions> rawOptions,
        ILogger<WriteAheadLog> logger, ISignalCheckpoints? checkpoints = null, IWalDurability? durability = null,
        ITelemetryOwnerResolver? owners = null, ITelemetryBindingRegistry? bindings = null, ITelemetrySink? sink = null,
        TelemetryRetentionPolicy? retention = null, ITopologyBindingResolver? topology = null)
    {
        this.decoder = decoder; this.sources = sources; this.objects = objects;
        this.rawOptions = rawOptions.Value; this.walOptions = walOptions.Value;
        this.checkpoints = checkpoints ?? new SignalCheckpoints();
        this.owners = owners ?? sources.HistoricalOwners;
        this.bindings = bindings ?? sources.HistoricalOwners;
        this.topology = topology ?? sources.HistoricalTopologyBindings;
        this.sink = sink;
        this.retention = retention ?? new();
        if (this.retention.Days is < 1 or > 36500) throw new ArgumentException("Telemetry retention must be between 1 and 36500 days.", nameof(retention));
        observedRetentionDays = options.Value.ObservedRetentionDays ?? this.retention.Days;
        if (observedRetentionDays is < 1 or > 36500)
            throw new ArgumentException("Observed topology retention must be between 1 and 36500 days.", nameof(options));
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
    public bool Ready => ready && Wal.Failure is null && Volatile.Read(ref archiveIntegrityFailure) is null;
    public string? LastFailure => Wal.Failure ?? Volatile.Read(ref archiveIntegrityFailure) ?? Volatile.Read(ref lastFailure);
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
            TelemetryOwnerBinding[] resolved;
            try { resolved = await owners.ResolveAsync(decoded.Accepted.Select(TelemetryMaterializer.Ownership).ToArray(), token); }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or IOException)
            { SetFailure(ex); return Full("Authoritative ownership history is unavailable."); }
            if (resolved.Length != decoded.Accepted.Count)
                return Full("Authoritative ownership history returned an incomplete decision.");
            TopologyLeafBinding[] topologyBindings;
            try
            {
                topologyBindings = await topology.ResolveAsync(decoded.Accepted.Zip(resolved,
                    TelemetryMaterializer.TopologyRequest).ToArray(), token);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or IOException or InvalidDataException)
            { SetFailure(ex); return Full("Authoritative topology history is unavailable."); }
            if (topologyBindings.Length != decoded.Accepted.Count)
                return Full("Authoritative topology history returned an incomplete decision.");
            var envelope = new RawSignalEnvelope(RawSignalEnvelope.CurrentVersion, Guid.CreateVersion7(), signal, contentType, DateTimeOffset.UtcNow,
                RawSignalEnvelope.Hash(payload.Span), payload.ToArray(), 1,
                decoded.Accepted.Select(x => x.Key).ToArray(), decoded.RejectedCount)
            { OwnerBindings = resolved, TopologyBindings = topologyBindings, RetentionDays = retention.Days,
                ObservedRetentionDays = observedRetentionDays };
            envelope = envelope with { OwnerBindingsSha256 = envelope.ComputeOwnerBindingsHash() };
            envelope = envelope with { TopologyBindingsSha256 = envelope.ComputeTopologyBindingsHash() };
            try { envelope.Validate(); }
            catch (InvalidDataException ex)
            { SetFailure(ex); return Full("Authoritative topology decision is invalid."); }
            try { await bindings.ClaimAsync(envelope.EnvelopeId, envelope.OwnerBindingsSha256, token); }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
            { SetFailure(ex); return Full("Authoritative ownership registry is unavailable."); }
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
            // A fresh process has no in-memory integrity latch. Verify every
            // retained manifest and required admission snapshot before readiness,
            // rather than treating an empty WAL as proof that restore data is safe.
            foreach (var manifest in Archive.Manifests())
            {
                var envelope = await Archive.ReadAsync(manifest, token);
                // Hashes alone do not prove AcceptedKeys/RejectedCount match
                // the captured payload. Reuse replay validation without binding,
                // writing typed rows or changing the archived admission decision.
                _ = decoder.Replay(envelope);
            }
            Volatile.Write(ref archiveIntegrityFailure, null);
            Volatile.Write(ref lastFailure, null);
            ready = true;
        }
        catch (Exception ex) { SetFailure(ex); throw; }
    }

    public async Task ProcessAsync(SignalWork work, CancellationToken token)
    {
        await processing.WaitAsync(token);
        try
        {
            var decoded = decoder.Replay(work.Envelope);
            var bound = BindLegacy(work.Envelope, decoded);
            await bindings.ClaimAsync(bound.EnvelopeId, bound.OwnerBindingsSha256!, token);
            await Archive.ArchiveAsync(bound, work.Segment, token, checkpoints.ReachAsync);
            await WriteResultAsync(bound, decoded, token);
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
            foreach (var manifest in Archive.Manifests().ToArray())
            {
                var envelope = await Archive.ReadAsync(manifest, token);
                var decoded = decoder.Replay(envelope);
                var bound = BindLegacy(envelope, decoded);
                await bindings.ClaimAsync(bound.EnvelopeId, bound.OwnerBindingsSha256!, token);
                if (envelope.Version < RawSignalEnvelope.CurrentVersion)
                    await Archive.ArchiveAsync(bound, manifest.WalSegment, token, checkpoints.ReachAsync);
                await WriteResultAsync(bound, decoded, token);
            }
            Volatile.Write(ref lastFailure, null);
            Volatile.Write(ref archiveIntegrityFailure, null);
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException or JsonException or OtlpDecodeException)
                Volatile.Write(ref archiveIntegrityFailure, ex.GetType().Name + ": " + ex.Message);
            SetFailure(ex);
            throw;
        }
        finally { if (acquired) processing.Release(); }
    }

    private async Task WriteResultAsync(RawSignalEnvelope envelope, TelemetryDecode decoded, CancellationToken token)
    {
        var leaves = new JsonArray();
        var records = new List<TelemetryRecord>();
        foreach (var leaf in decoded.Accepted)
        {
            var source = envelope.OwnerBindings!.Single(b => b.LeafKey == leaf.Key);
            var topologyBinding = envelope.TopologyBindings!.Single(b => b.LeafKey == leaf.Key);
            records.Add(TelemetryMaterializer.Materialize(envelope, leaf, source));
            leaves.Add(new JsonObject
            {
                ["logical_id"] = envelope.EnvelopeId.ToString("N") + "/" + leaf.Key,
                ["key"] = leaf.Key, ["owner_group"] = source.OwnerGroup, ["source_id"] = source.SourceId,
                ["known_source"] = source.Reason == "known",
                ["owner_reason"] = source.Reason, ["owner_history_revision"] = source.HistoryRevision,
                ["topology_binding_sha256"] = envelope.TopologyBindingsSha256,
                ["topology_reason"] = topologyBinding.Reason, ["topology_node_id"] = topologyBinding.NodeId,
                ["topology_node_history_revision"] = topologyBinding.NodeHistoryRevision,
                ["resource"] = OtlpJsonCodec.Format(leaf.Resource), ["scope"] = OtlpJsonCodec.Format(leaf.Scope),
                ["resource_schema_url"] = leaf.ResourceSchemaUrl, ["scope_schema_url"] = leaf.ScopeSchemaUrl,
                ["metric"] = leaf.Metric is null ? null : OtlpJsonCodec.Format(leaf.Metric),
                ["span"] = leaf.Span is null ? null : OtlpJsonCodec.Format(leaf.Span),
            });
        }
        var result = new JsonObject
        {
            ["version"] = 2, ["envelope_id"] = envelope.EnvelopeId.ToString("N"),
            ["delivery"] = sink is null ? "fixture-file-only" : "clickhouse",
            ["owner_binding_sha256"] = envelope.OwnerBindingsSha256,
            ["topology_binding_sha256"] = envelope.TopologyBindingsSha256,
            ["signal"] = envelope.Signal.ToString(), ["payload_sha256"] = envelope.PayloadSha256,
            ["received_at"] = envelope.ReceivedAt, ["rejected_count"] = envelope.RejectedCount, ["leaves"] = leaves,
        };
        // Stable filename is the idempotency key. Result and checkpoint commit
        // together; there is no marker that can advance ahead of durable data.
        var path = Path.Combine(Root, "processed", envelope.EnvelopeId.ToString("N") + ".json");
        if (sink is not null) await sink.WriteAsync(records, token);
        await checkpoints.ReachAsync("after-telemetry-db-before-checkpoint", token);
        await DurableFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(result), token, checkpoints.ReachAsync);
    }

    private static RawSignalEnvelope BindLegacy(RawSignalEnvelope envelope, TelemetryDecode decoded)
    {
        if (envelope.Version == RawSignalEnvelope.CurrentVersion) return envelope;
        // Historical exports without immutable topology decisions must not be
        // rebound from the current alias inventory during restore.
        var bound = envelope.Version == 1 ? envelope with
        {
            Version = 2,
            OwnerBindings = decoded.Accepted.Select(leaf => new TelemetryOwnerBinding(leaf.Key,
                TelemetryMaterializer.Ownership(leaf).Candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate)) ?? "_unknown",
                OwnerGroups.Unassigned, 0, TelemetryMaterializer.Time(leaf), "legacy-owner-unknown")).ToArray(),
        } : envelope;
        if (envelope.Version == 1) bound = bound with { OwnerBindingsSha256 = bound.ComputeOwnerBindingsHash() };
        bound = bound with
        {
            Version = RawSignalEnvelope.CurrentVersion,
            TopologyBindings = bound.OwnerBindings!.Select(owner => new TopologyLeafBinding(owner.LeafKey,
                owner.EventTimeUnixNano, owner.SourceId, owner.OwnerGroup, owner.HistoryRevision,
                null, null, null, null, null, null, "LegacyTopologyUnknown")).ToArray(),
        };
        return bound with { TopologyBindingsSha256 = bound.ComputeTopologyBindingsHash() };
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
                    || m.VerifiedAt > cutoff || !IsDelivered(e))) continue;
                foreach (var envelope in envelopes) await Archive.ReadAsync(manifests[envelope.EnvelopeId], token);
                Wal.Delete(segment);
            }
        }
        finally { processing.Release(); }
    }

    private bool IsDelivered(RawSignalEnvelope envelope)
    {
        var path = Path.Combine(Root, "processed", envelope.EnvelopeId.ToString("N") + ".json");
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var result = document.RootElement;
        return result.TryGetProperty("version", out var version) && version.GetInt32() == 2
            && result.GetProperty("payload_sha256").GetString() == envelope.PayloadSha256
            && result.TryGetProperty("owner_binding_sha256", out var binding) && binding.GetString() is { Length: 64 }
            && (envelope.Version == 1 || binding.GetString() == envelope.OwnerBindingsSha256)
            && (envelope.Version < RawSignalEnvelope.CurrentVersion || result.TryGetProperty("topology_binding_sha256", out var topologyBinding)
                && topologyBinding.GetString() == envelope.TopologyBindingsSha256)
            && result.GetProperty("delivery").GetString() == (sink is null ? "fixture-file-only" : "clickhouse");
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        try { Wal.Dispose(); }
        finally { lease.Dispose(); slots.Dispose(); processing.Dispose(); }
    }
}
