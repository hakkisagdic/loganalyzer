using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Replay;

public sealed record TopologyRecoveryRecord(string OccurrenceId, TopologyEdgeProjection Edge);

public sealed record TopologyRecoveryArchive(
    int SchemaVersion,
    string RegistryRevision,
    long PublishedSequence,
    IReadOnlyList<TopologyRecoveryRecord> Records,
    string Checksum);

public sealed record TopologyRecoverySnapshot(
    string RegistryRevision,
    long PublishedSequence,
    IReadOnlyList<TopologyEdgeProjection> Edges);

public enum TopologyRecoveryStatus { Restored = 1, Missing = 2, Corrupt = 3, Unsupported = 4 }

public sealed record TopologyRecoveryResult(
    TopologyRecoveryStatus Status,
    TopologyRecoverySnapshot? Snapshot,
    string? Reason);

/// <summary>
/// Archive restore and WAL continuation for topology projections. The replay
/// key is the envelope-independent occurrence id; input order and duplicates
/// cannot change the resulting edge set.
/// </summary>
public static class TopologyRecovery
{
    public const int CurrentSchemaVersion = 2;

    public static TopologyRecoveryArchive CreateArchive(string registryRevision, long publishedSequence,
        IEnumerable<TopologyRecoveryRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryRevision);
        if (publishedSequence < 0) throw new ArgumentOutOfRangeException(nameof(publishedSequence));
        var ordered = Normalize(records);
        return new(CurrentSchemaVersion, registryRevision, publishedSequence, ordered,
            Checksum(CurrentSchemaVersion, registryRevision, publishedSequence, ordered));
    }

    public static TopologyRecoveryResult Restore(TopologyRecoveryArchive? archive)
    {
        if (archive is null) return new(TopologyRecoveryStatus.Missing, null, "SnapshotMissing");
        if (archive.SchemaVersion != CurrentSchemaVersion)
            return new(TopologyRecoveryStatus.Unsupported, null, "SnapshotSchemaUnsupported");
        if (string.IsNullOrWhiteSpace(archive.RegistryRevision))
            return new(TopologyRecoveryStatus.Corrupt, null, "RegistryRevisionMissing");
        var expected = Checksum(archive.SchemaVersion, archive.RegistryRevision, archive.PublishedSequence,
            archive.Records);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(archive.Checksum ?? string.Empty)))
            return new(TopologyRecoveryStatus.Corrupt, null, "SnapshotChecksumMismatch");
        try
        {
            return new(TopologyRecoveryStatus.Restored,
                new(archive.RegistryRevision, archive.PublishedSequence, Replay(archive.Records)), null);
        }
        catch (InvalidDataException ex)
        {
            return new(TopologyRecoveryStatus.Corrupt, null, ex.Message);
        }
    }

    public static TopologyRecoveryResult Resume(TopologyRecoveryArchive? archive,
        IEnumerable<TopologyRecoveryRecord> walRecords)
    {
        ArgumentNullException.ThrowIfNull(walRecords);
        var restored = Restore(archive);
        if (restored.Status != TopologyRecoveryStatus.Restored) return restored;
        try
        {
            var records = archive!.Records.Concat(walRecords).ToArray();
            var edges = Replay(records);
            var sequence = Math.Max(archive.PublishedSequence,
                edges.Count == 0 ? archive.PublishedSequence : edges.Max(static edge => edge.Sequence));
            return new(TopologyRecoveryStatus.Restored,
                new(archive.RegistryRevision, sequence, edges), null);
        }
        catch (InvalidDataException ex)
        {
            return new(TopologyRecoveryStatus.Corrupt, null, ex.Message);
        }
    }

    public static IReadOnlyList<TopologyEdgeProjection> Replay(IEnumerable<TopologyRecoveryRecord> records)
    {
        var normalized = Normalize(records);
        var edges = new Dictionary<string, TopologyEdgeProjection>(StringComparer.Ordinal);
        foreach (var record in normalized)
        {
            if (edges.TryGetValue(record.Edge.Id, out var existing) && existing != record.Edge)
                throw new InvalidDataException($"Conflicting edge id '{record.Edge.Id}'.");
            edges[record.Edge.Id] = record.Edge;
        }
        return edges.Values.OrderBy(static edge => edge.Id, StringComparer.Ordinal).ToArray();
    }

    private static TopologyRecoveryRecord[] Normalize(IEnumerable<TopologyRecoveryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var occurrences = new Dictionary<string, TopologyEdgeProjection>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(record.OccurrenceId);
            ArgumentNullException.ThrowIfNull(record.Edge);
            if (occurrences.TryGetValue(record.OccurrenceId, out var existing) && existing != record.Edge)
                throw new InvalidDataException($"Conflicting occurrence id '{record.OccurrenceId}'.");
            occurrences[record.OccurrenceId] = record.Edge;
        }
        return occurrences.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => new TopologyRecoveryRecord(item.Key, item.Value)).ToArray();
    }

    private static string Checksum(int schemaVersion, string registryRevision, long publishedSequence,
        IReadOnlyList<TopologyRecoveryRecord> records)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ArchivePayload(schemaVersion, registryRevision,
            publishedSequence, records), JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }

    private sealed record ArchivePayload(int SchemaVersion, string RegistryRevision, long PublishedSequence,
        IReadOnlyList<TopologyRecoveryRecord> Records);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
