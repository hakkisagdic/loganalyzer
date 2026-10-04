using System.Security.Cryptography;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>Durable attestation of a verified immutable manifest prefix and four live tables.</summary>
public sealed record TopologyRepairCertificate(
    int FormatVersion,
    long Generation,
    Guid PostgresDatabaseIdentity,
    Guid ClickHouseDatabaseUuid,
    long ReceiptPrefixSequence,
    string ReceiptPrefixSha256,
    string? PendingPublicationKey,
    string? PendingPayloadSha256,
    string ParentDecisionSha256,
    string LifecycleSha256,
    IReadOnlyList<TopologyRepairTableIdentity> Tables);

/// <summary>
/// A singleton-safe observed gate. Every read samples both PG and live CH;
/// no cached Ready flag can survive an out-of-band DROP/CREATE or downgrade.
/// </summary>
public sealed class TopologyObservedRepairReadiness(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ClickHouseContext clickHouse) : ITopologyObservedRepairReadiness
{
    private readonly TopologyRepairSchemaInspector inspector = new(clickHouse);

    public async Task<TopologyRepairReadStamp> RequireReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!db.Database.IsNpgsql()) throw new InvalidDataException("PostgreSQL repair authority is required.");
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT phase, generation, pg_database_identity, certificate_digest,
                       certificate_json::text, receipt_prefix_sequence, receipt_prefix_sha256
                FROM bizigo.topology_repair_state WHERE id = 1
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "Ready")
                throw new TopologyObservedRepairUnavailableException();
            var generation = reader.GetInt64(1);
            var pgIdentity = reader.GetGuid(2);
            if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(6))
                throw new TopologyObservedRepairUnavailableException();
            var storedDigest = reader.GetString(3);
            var certificate = JsonSerializer.Deserialize<TopologyRepairCertificate>(reader.GetString(4))
                ?? throw new TopologyObservedRepairUnavailableException();
            var prefixSequence = reader.GetInt64(5);
            var prefixHash = reader.GetString(6);
            if (await reader.ReadAsync(cancellationToken)
                || certificate.FormatVersion != 1 || generation <= 0
                || certificate.Generation != generation
                || certificate.PostgresDatabaseIdentity != pgIdentity
                || certificate.ReceiptPrefixSequence != prefixSequence
                || certificate.ReceiptPrefixSha256 != prefixHash
                || certificate.Tables.Count != TopologyRepairSchemaInspector.CanonicalNames.Count
                || !string.Equals(storedDigest, Digest(certificate), StringComparison.Ordinal))
                throw new TopologyObservedRepairUnavailableException();

            var databaseUuid = await inspector.ReadDatabaseUuidAsync(cancellationToken);
            if (!string.Equals(databaseUuid, certificate.ClickHouseDatabaseUuid.ToString("D"), StringComparison.Ordinal))
                throw new TopologyObservedRepairUnavailableException();
            var live = await inspector.ReadCanonicalAsync(cancellationToken);
            foreach (var actual in live)
            {
                var expected = certificate.Tables.SingleOrDefault(t => t.Name == actual.Name);
                if (expected is null || expected != actual || !HasRequiredVersionKey(actual))
                    throw new TopologyObservedRepairUnavailableException();
            }
            if (!await inspector.HasVersionedObservedEngineAsync(cancellationToken)
                || !await inspector.HasReadyColumnShapesAsync(cancellationToken))
                throw new TopologyObservedRepairUnavailableException();
            var afterShapeCheck = await inspector.ReadCanonicalAsync(cancellationToken);
            if (!live.SequenceEqual(afterShapeCheck))
                throw new TopologyObservedRepairUnavailableException();
            return new(generation, storedDigest);
        }
        catch (OperationCanceledException) { throw; }
        catch (TopologyObservedRepairUnavailableException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No private table identity, owner, anchor or manifest detail leaves
            // this boundary. Caller sees a generic observed-unavailable 503.
            throw new TopologyObservedRepairUnavailableException();
        }
    }

    public static string Digest(TopologyRepairCertificate certificate) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(certificate))).ToLowerInvariant();

    internal static bool HasRequiredVersionKey(TopologyRepairTableIdentity table)
    {
        var expectedEngine = table.Name == "topology_edges_observed" ? "ReplacingMergeTree" : "MergeTree";
        if (!string.Equals(table.Engine, expectedEngine, StringComparison.Ordinal)) return false;
        string[] expectedKey = table.Name switch
        {
            "topology_edges_observed" or "topology_edge_lifecycle" =>
                ["child_owner_group", "from_node_id", "to_node_id", "last_seen", "edge_id", "publication_seq"],
            "topology_span_conflicts" => ["semantic_anchor", "publication_seq"],
            "topology_parent_resolution" => ["child_anchor", "child_fingerprint", "publication_seq"],
            _ => [],
        };
        var actualKey = table.SortingKey.Split(',')
            .Select(part => part.Trim(' ', '\t', '\r', '\n', '(', ')', '`'))
            .ToArray();
        return expectedKey.Length != 0 && expectedKey.SequenceEqual(actualKey, StringComparer.Ordinal);
    }
}
