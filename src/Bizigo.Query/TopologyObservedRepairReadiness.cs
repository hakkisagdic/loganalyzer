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
/// Format 2 binds the same physical certificate to a sealed, explicit PG
/// member set. Format 1 JSON and digest bytes remain unchanged.
/// </summary>
public sealed record TopologyRepairCertificateV2(
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
    IReadOnlyList<TopologyRepairTableIdentity> Tables,
    Guid MemberSetId,
    long SidecarRevision,
    int MemberCount,
    string MembershipSha256);

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
                SELECT r.phase, r.generation, r.pg_database_identity, r.certificate_digest,
                       r.certificate_json::text, r.receipt_prefix_sequence, r.receipt_prefix_sha256,
                       r.sidecar_revision, r.certificate_member_set_id,
                       h.generation, h.pg_database_identity, h.clickhouse_database_uuid,
                       h.sampled_sidecar_revision, h.receipt_prefix_sequence,
                       h.receipt_prefix_sha256, h.member_count, h.canonical_byte_length,
                       h.canonical_sha256, h.sealed_at,
                       CASE WHEN h.member_set_id IS NULL THEN NULL ELSE
                           (SELECT count(*) FROM bizigo.topology_repair_members m
                            WHERE m.member_set_id = h.member_set_id) END,
                       CASE WHEN h.member_set_id IS NULL THEN NULL ELSE
                           octet_length(bizigo.topology_repair_member_bytes(h.member_set_id)) END,
                       CASE WHEN h.member_set_id IS NULL THEN NULL ELSE
                           encode(sha256(bizigo.topology_repair_member_bytes(h.member_set_id)), 'hex') END,
                       r.copy_attempt_id, h.copy_attempt_id,
                       h.pending_publication_key, h.pending_sequence, h.pending_conversion_digest,
                       h.pending_payload_sha256,
                       (SELECT count(*) = 9 FROM pg_trigger t WHERE t.tgenabled = 'O'
                         AND ((t.tgrelid = 'bizigo.topology_legacy_conversion_sidecars'::regclass
                               AND t.tgname IN ('topology_sidecar_admit', 'topology_sidecar_revision',
                                                'topology_sidecar_immutable'))
                           OR (t.tgrelid = 'bizigo.topology_repair_member_sets'::regclass
                               AND t.tgname IN ('topology_set_admit', 'topology_set_seal',
                                                'topology_set_no_delete'))
                           OR (t.tgrelid = 'bizigo.topology_repair_members'::regclass
                               AND t.tgname IN ('topology_member_admit', 'topology_member_immutable'))
                           OR (t.tgrelid = 'bizigo.topology_repair_state'::regclass
                               AND t.tgname = 'topology_format2_ready_guard')))
                FROM bizigo.topology_repair_state r
                LEFT JOIN bizigo.topology_repair_member_sets h
                    ON h.member_set_id = r.certificate_member_set_id
                WHERE r.id = 1
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != "Ready")
                throw new TopologyObservedRepairUnavailableException();
            var generation = reader.GetInt64(1);
            var pgIdentity = reader.GetGuid(2);
            if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(6))
                throw new TopologyObservedRepairUnavailableException();
            var storedDigest = reader.GetString(3);
            var certificateJson = reader.GetString(4);
            using var document = JsonDocument.Parse(certificateJson);
            if (!document.RootElement.TryGetProperty("FormatVersion", out var versionElement))
                throw new TopologyObservedRepairUnavailableException();
            var version = versionElement.GetInt32();
            var certificate = version == 1
                ? JsonSerializer.Deserialize<TopologyRepairCertificate>(certificateJson)
                : null;
            var certificateV2 = version == 2
                ? JsonSerializer.Deserialize<TopologyRepairCertificateV2>(certificateJson)
                : null;
            var prefixSequence = reader.GetInt64(5);
            var prefixHash = reader.GetString(6);
            if (generation <= 0 || certificate is null && certificateV2 is null)
                throw new TopologyObservedRepairUnavailableException();

            var tables = certificate?.Tables ?? certificateV2!.Tables;
            var certificateChIdentity = certificate?.ClickHouseDatabaseUuid
                ?? certificateV2!.ClickHouseDatabaseUuid;
            if (tables.Count != TopologyRepairSchemaInspector.CanonicalNames.Count)
                throw new TopologyObservedRepairUnavailableException();
            if (certificate is not null)
            {
                if (certificate.FormatVersion != 1 || certificate.Generation != generation
                    || certificate.PostgresDatabaseIdentity != pgIdentity
                    || certificate.ReceiptPrefixSequence != prefixSequence
                    || certificate.ReceiptPrefixSha256 != prefixHash
                    || !reader.IsDBNull(8)
                    || !string.Equals(storedDigest, Digest(certificate), StringComparison.Ordinal))
                    throw new TopologyObservedRepairUnavailableException();
            }
            else
            {
                if (certificateV2!.FormatVersion != 2 || certificateV2.Generation != generation
                    || certificateV2.PostgresDatabaseIdentity != pgIdentity
                    || certificateV2.ReceiptPrefixSequence != prefixSequence
                    || certificateV2.ReceiptPrefixSha256 != prefixHash
                    || reader.IsDBNull(8) || reader.GetGuid(8) != certificateV2.MemberSetId
                    || reader.GetInt64(7) != certificateV2.SidecarRevision
                    || reader.IsDBNull(18) || reader.GetInt64(9) != generation
                    || reader.GetGuid(10) != pgIdentity
                    || reader.GetGuid(11) != certificateV2.ClickHouseDatabaseUuid
                    || reader.IsDBNull(22) || reader.GetGuid(22) != reader.GetGuid(23)
                    || reader.GetInt64(12) != certificateV2.SidecarRevision
                    || reader.GetInt64(13) != prefixSequence
                    || reader.GetString(14) != prefixHash
                    || (reader.IsDBNull(24) ? null : reader.GetString(24)) != certificateV2.PendingPublicationKey
                    || (reader.IsDBNull(25) ? null : reader.GetInt64(25)) !=
                        (certificateV2.PendingPublicationKey is null ? null
                            : checked(prefixSequence + 1))
                    || !reader.IsDBNull(26)
                    || (reader.IsDBNull(27) ? null : reader.GetString(27)) !=
                        certificateV2.PendingPayloadSha256
                    || !reader.GetBoolean(28)
                    || reader.GetInt32(15) != certificateV2.MemberCount
                    || reader.GetString(17) != certificateV2.MembershipSha256
                    || reader.GetInt64(19) != certificateV2.MemberCount
                    || reader.GetInt32(16) != reader.GetInt32(20)
                    || reader.GetString(17) != reader.GetString(21)
                    || certificateV2.MemberCount != 0 // ORIGINAL custody is not yet established.
                    || !string.Equals(storedDigest, Digest(certificateV2), StringComparison.Ordinal))
                    throw new TopologyObservedRepairUnavailableException();
            }

            var databaseUuid = await inspector.ReadDatabaseUuidAsync(cancellationToken);
            if (!string.Equals(databaseUuid, certificateChIdentity.ToString("D"), StringComparison.Ordinal))
                throw new TopologyObservedRepairUnavailableException();
            var live = await inspector.ReadCanonicalAsync(cancellationToken);
            foreach (var actual in live)
            {
                var expected = tables.SingleOrDefault(t => t.Name == actual.Name);
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

    public static string Digest(TopologyRepairCertificateV2 certificate) =>
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
