using Bizigo.Contracts;

namespace Bizigo.Ingest.Wal;

/// <summary>
/// Checks the consistency of a caller-supplied frozen WAL roster against exact
/// decompressed archive envelope bytes and already existing owner claims. This
/// cannot prove that the roster was the original deployment spool: independent
/// historical custody is required before any v2 upgrade or Ready certificate.
/// There is deliberately no activation/upgrade result.
/// </summary>
public static class TopologyLegacyWalCandidateVerifier
{
    public static void VerifyCandidate(
        IReadOnlyList<string> frozenSegmentPaths,
        IReadOnlyDictionary<Guid, ReadOnlyMemory<byte>> archivedEnvelopeBytes,
        IReadOnlyDictionary<Guid, string> existingOwnerClaimHashes,
        long maxRosterBytes,
        int maxFrames)
    {
        ArgumentNullException.ThrowIfNull(frozenSegmentPaths);
        ArgumentNullException.ThrowIfNull(archivedEnvelopeBytes);
        ArgumentNullException.ThrowIfNull(existingOwnerClaimHashes);
        if (maxRosterBytes <= 0 || maxFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRosterBytes), "Candidate scan limits must be positive.");
        if (frozenSegmentPaths.Count == 0)
            throw new InvalidDataException("Original WAL roster candidate is empty.");

        // Size the entire declared universe before reading a subset. This does
        // not acquire an exclusive spool lease; the caller must provide one.
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var path in frozenSegmentPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !paths.Add(Path.GetFullPath(path)))
                throw new InvalidDataException("Missing or duplicate WAL roster segment.");
            var length = new FileInfo(path).Length;
            if (length <= 0 || length > maxRosterBytes - totalBytes)
                throw new InvalidDataException("WAL roster is empty or exceeds its complete-scan cap.");
            totalBytes += length;
        }

        var seen = new Dictionary<Guid, byte[]>();
        var frames = 0;
        foreach (var path in frozenSegmentPaths)
        {
            foreach (var frame in WriteAheadLog.ReadFrames(path, strict: true))
            {
                if (++frames > maxFrames)
                    throw new InvalidDataException("WAL roster exceeds its complete-scan frame cap.");
                var envelope = RawSignalCodec.Decode(frame.Span);
                if (envelope.Version != RawSignalEnvelope.CurrentVersion)
                    throw new InvalidDataException("Original topology admission requires a full v3 WAL frame.");
                if (seen.TryGetValue(envelope.EnvelopeId, out var previous)
                    && !previous.AsSpan().SequenceEqual(frame.Span))
                    throw new InvalidDataException("Divergent WAL frames reuse one EnvelopeId.");
                if (!archivedEnvelopeBytes.TryGetValue(envelope.EnvelopeId, out var archived)
                    || !archived.Span.SequenceEqual(frame.Span))
                    throw new InvalidDataException("WAL frame and decompressed archive bytes differ.");
                if (!existingOwnerClaimHashes.TryGetValue(envelope.EnvelopeId, out var claim)
                    || !string.Equals(claim, envelope.OwnerBindingsSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Historical owner claim is missing or divergent.");
                if (previous is null) seen.Add(envelope.EnvelopeId, frame.ToArray());
            }
        }

        if (seen.Count == 0 || seen.Count != archivedEnvelopeBytes.Count
            || seen.Count != existingOwnerClaimHashes.Count)
            throw new InvalidDataException("WAL, archive and existing-claim universes are incomplete.");
    }
}
