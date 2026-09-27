using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Storage.Raw;

public sealed record SignalArchiveManifest(int Version, Guid EnvelopeId, TelemetrySignal Signal,
    string PayloadSha256, string ObjectKey, string ObjectSha256, string WalSegment,
    DateTimeOffset VerifiedAt, int EnvelopeLength);

/// <summary>Internal signal namespace; never inserted into the raw-log query manifest.</summary>
public sealed class SignalArchive(IRawObjectStore store, string directory, int compressionLevel = 3)
{
    public string ManifestDirectory { get; } = Path.Combine(directory, "manifests");

    public async Task<SignalArchiveManifest> ArchiveAsync(RawSignalEnvelope envelope, string segment,
        CancellationToken token, Func<string, CancellationToken, Task>? checkpoint = null)
    {
        var bytes = RawSignalCodec.Encode(envelope);
        var builder = new RawObjectBuilder();
        builder.Add(envelope.EnvelopeId, envelope.ReceivedAt, bytes);
        var built = builder.Build(compressionLevel);
        var key = $"otlp/v1/{envelope.Signal.ToString().ToLowerInvariant()}/{envelope.EnvelopeId:N}.json.zst";
        var path = ManifestPath(envelope.EnvelopeId);
        SignalArchiveManifest? old = File.Exists(path) ? ReadManifest(path) : null;
        if (old is not null && (old.EnvelopeId != envelope.EnvelopeId || old.Signal != envelope.Signal
            || old.PayloadSha256 != envelope.PayloadSha256))
            throw new InvalidDataException("Envelope identity conflicts with archive manifest.");
        if (old is not null)
        {
            key = old.ObjectKey;
            if (old.ObjectSha256 != built.Sha256)
            {
                var previous = await ReadAsync(old, token);
                var legacy = envelope with { Version = 1, OwnerBindings = null, OwnerBindingsSha256 = null };
                if (previous.Version != 1 || envelope.Version != RawSignalEnvelope.CurrentVersion
                    || !RawSignalCodec.Encode(previous).AsSpan().SequenceEqual(RawSignalCodec.Encode(legacy)))
                    throw new InvalidDataException("Archived admission decision cannot be replaced.");
                // Copy-on-write: the old manifest always retains its verified
                // object until the replacement manifest is atomically renamed.
                key = $"otlp/v1/{envelope.Signal.ToString().ToLowerInvariant()}/{envelope.EnvelopeId:N}.{built.Sha256}.json.zst";
                old = null;
            }
        }

        var existing = old is null ? null : await store.GetAsync(key, token);
        if (existing is null || RawSignalEnvelope.Hash(existing) != built.Sha256)
        {
            await store.PutAsync(key, built.Compressed, token);
            existing = await store.GetAsync(key, token);
            if (existing is null || RawSignalEnvelope.Hash(existing) != built.Sha256)
                throw new InvalidDataException("Signal archive read-back checksum mismatch.");
            old = null;
        }
        if (checkpoint is not null) await checkpoint("archive-before-manifest", token);
        var manifest = new SignalArchiveManifest(1, envelope.EnvelopeId, envelope.Signal, envelope.PayloadSha256,
            key, built.Sha256, segment, old?.VerifiedAt ?? DateTimeOffset.UtcNow, bytes.Length);
        await DurableFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(manifest, RawSignalCodec.Json), token,
            checkpoint is null ? null : (stage, ct) => checkpoint("archive-manifest-" + stage, ct));
        return manifest;
    }

    public async Task<RawSignalEnvelope> ReadAsync(SignalArchiveManifest manifest, CancellationToken token)
    {
        if (manifest.Version != 1) throw new InvalidDataException("Unsupported archive manifest version.");
        var content = await store.GetAsync(manifest.ObjectKey, token)
            ?? throw new FileNotFoundException("Signal archive object is missing.", manifest.ObjectKey);
        if (RawSignalEnvelope.Hash(content) != manifest.ObjectSha256)
            throw new InvalidDataException("Signal archive checksum mismatch.");
        var envelope = RawSignalCodec.Decode(RawObjectBuilder.ExtractLine(content, 0, manifest.EnvelopeLength).Span);
        if (envelope.EnvelopeId != manifest.EnvelopeId || envelope.Signal != manifest.Signal
            || envelope.PayloadSha256 != manifest.PayloadSha256)
            throw new InvalidDataException("Signal archive manifest/envelope mismatch.");
        return envelope;
    }

    public IEnumerable<SignalArchiveManifest> Manifests() => Directory.Exists(ManifestDirectory)
        ? Directory.EnumerateFiles(ManifestDirectory, "*.json").Order(StringComparer.Ordinal).Select(ReadManifest)
        : [];

    private string ManifestPath(Guid id) => Path.Combine(ManifestDirectory, id.ToString("N") + ".json");
    private static SignalArchiveManifest ReadManifest(string path) =>
        JsonSerializer.Deserialize<SignalArchiveManifest>(File.ReadAllBytes(path), RawSignalCodec.Json)
        ?? throw new InvalidDataException("Empty archive manifest.");
}
