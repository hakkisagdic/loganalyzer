using System.Text.Json;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>
/// PG guard oracles. A sealed legacy set is not evidence of ORIGINAL archive
/// custody and these tests never activate a nonempty conversion set.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyLegacyMembershipGuardIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string Key = new('a', 64);
    private static readonly string Digest = new('b', 64);

    [Fact]
    public async Task Native_v4_empty_member_set_is_sealed_and_bound_to_ready_certificate()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
        var gate = new TopologyObservedRepairReadiness(factory, storage);
        var runner = new TopologyPublicationRepairRunner(factory, storage, gate);
        var ready = await runner.InitializeAsync(TopologyRepairStartMode.Startup, Ct);
        Assert.Equal(TopologyRepairInitializationStatus.Ready, ready.Status);
        var stamp = await gate.RequireReadyAsync(Ct);
        Assert.Equal(ready.Generation, stamp.Generation);

        await using var db = await factory.CreateDbContextAsync(Ct);
        var certificateJson = await db.Database.SqlQueryRaw<string>(
            "SELECT certificate_json::text AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1")
            .SingleAsync(Ct);
        var certificate = JsonSerializer.Deserialize<TopologyRepairCertificateV2>(certificateJson);
        Assert.NotNull(certificate);
        Assert.Equal(2, certificate.FormatVersion);
        Assert.Equal(0, certificate.MemberCount);
        Assert.Equal(0L, certificate.SidecarRevision);
        var empty = TopologyRepairMembershipBuilder.Build(
            Array.Empty<TopologyRepairMembershipPublication>(), 0, null);
        Assert.Equal(empty.CanonicalSha256, certificate.MembershipSha256);
        var sealedSet = await db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM bizigo.topology_repair_member_sets
            WHERE member_set_id = {certificate.MemberSetId} AND sealed_at IS NOT NULL
              AND member_count = 0 AND canonical_sha256 = {empty.CanonicalSha256}
            """).SingleAsync(Ct);
        Assert.Equal(1L, sealedSet);
    }

    [Fact]
    public async Task Global_sidecar_exact_retry_does_not_bump_revision_and_divergence_is_rejected()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var (pg, ch, _) = await PrepareRepairingAsync(factory);
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, Key, Digest, new string('c', 64)));
        Assert.Equal(0, await InsertSidecarAsync(db, pg, ch, Key, Digest, new string('c', 64)));
        Assert.Equal(1L, await SidecarRevisionAsync(db));
        await Assert.ThrowsAsync<PostgresException>(() =>
            InsertSidecarAsync(db, pg, ch, Key, Digest, new string('d', 64)));
        Assert.Equal(1L, await SidecarRevisionAsync(db));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_legacy_conversion_sidecars
            SET original_payload_sha256 = {new string('e', 64)}
            WHERE original_publication_key = {Key} AND original_sequence = 1
            """, Ct));
    }

    [Fact]
    public async Task Incomplete_or_stale_member_set_cannot_seal_or_activate_legacy_ready()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var (pg, ch, attempt) = await PrepareRepairingAsync(factory);
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, Key, Digest, new string('c', 64)));
        var candidate = TopologyRepairMembershipBuilder.Build(
            [new TopologyRepairMembershipPublication(1, Key, true, Digest)], 1, null);
        var setId = Guid.NewGuid();
        await InsertSetAsync(db, setId, pg, ch, attempt, 1, candidate);
        // No member row: the sealed header cannot hide an omitted conversion.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_member_sets SET sealed_at = now()
            WHERE member_set_id = {setId}
            """, Ct));
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_members
                (member_set_id, original_sequence, original_publication_key, conversion_digest)
            VALUES ({setId}, 1, {Key}, {Digest})
            """, Ct));
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_member_sets SET sealed_at = now()
            WHERE member_set_id = {setId}
            """, Ct));
        // Sealing validates mechanics; it does not synthesize historical custody.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_members
                (member_set_id, original_sequence, original_publication_key, conversion_digest)
            VALUES ({setId}, 1, {Key}, {Digest}) ON CONFLICT DO NOTHING
            """, Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM bizigo.topology_repair_member_sets WHERE member_set_id = {setId}
            """, Ct));

        var prefix = new string('1', 64);
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state SET receipt_prefix_sequence = 1,
                receipt_prefix_sha256 = {prefix} WHERE id = 1 AND phase = 'Repairing'
            """, Ct));
        var attemptedCertificate = JsonSerializer.Serialize(new
        {
            FormatVersion = 2, MemberSetId = setId, SidecarRevision = 1,
            MemberCount = 1, MembershipSha256 = candidate.CanonicalSha256,
        });
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state SET phase = 'Ready',
                certificate_member_set_id = {setId},
                certificate_json = CAST({attemptedCertificate} AS jsonb),
                certificate_digest = {new string('2', 64)} WHERE id = 1
            """, Ct));
    }

    [Fact]
    public async Task Sidecar_revision_drift_prevents_seal_of_an_old_attempt()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var (pg, ch, attempt) = await PrepareRepairingAsync(factory);
        await using var db = await factory.CreateDbContextAsync(Ct);
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, Key, Digest, new string('c', 64)));
        var setId = Guid.NewGuid();
        var empty = TopologyRepairMembershipBuilder.Build(
            Array.Empty<TopologyRepairMembershipPublication>(), 0, null);
        await InsertSetAsync(db, setId, pg, ch, attempt, 1, empty);
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, new string('3', 64),
            new string('4', 64), new string('5', 64), 2));
        Assert.Equal(2L, await SidecarRevisionAsync(db));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_member_sets SET sealed_at = now()
            WHERE member_set_id = {setId}
            """, Ct));
    }

    [Fact]
    public async Task Extra_conversion_row_cannot_hide_behind_a_sealed_header_count()
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, Ct);
        var (pg, ch, attempt) = await PrepareRepairingAsync(factory);
        await using var db = await factory.CreateDbContextAsync(Ct);
        var secondKey = new string('3', 64);
        var secondDigest = new string('4', 64);
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, Key, Digest, new string('c', 64)));
        Assert.Equal(1, await InsertSidecarAsync(db, pg, ch, secondKey, secondDigest,
            new string('5', 64), 2));
        var candidate = TopologyRepairMembershipBuilder.Build(
            [new TopologyRepairMembershipPublication(1, Key, true, Digest)], 1, null);
        var setId = Guid.NewGuid();
        await InsertSetAsync(db, setId, pg, ch, attempt, 2, candidate);
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_members
                (member_set_id, original_sequence, original_publication_key, conversion_digest)
            VALUES ({setId}, 1, {Key}, {Digest})
            """, Ct));
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_members
                (member_set_id, original_sequence, original_publication_key, conversion_digest)
            VALUES ({setId}, 2, {secondKey}, {secondDigest})
            """, Ct));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_member_sets SET sealed_at = now()
            WHERE member_set_id = {setId}
            """, Ct));
    }

    private static async Task<(Guid Pg, Guid Ch, Guid Attempt)> PrepareRepairingAsync(
        IDbContextFactory<ControlPlaneDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync(Ct);
        var pg = await db.Database.SqlQueryRaw<Guid>(
            "SELECT pg_database_identity AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1")
            .SingleAsync(Ct);
        var ch = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state
            SET phase = 'Repairing', generation = 1, copy_attempt_id = {attempt},
                clickhouse_database_uuid = {ch}, old_canonical_identity_json = '[]'::jsonb,
                allowed_copy_identity_json = '{{}}'::jsonb,
                certificate_json = NULL, certificate_digest = NULL,
                certificate_member_set_id = NULL
            WHERE id = 1 AND phase = 'Uninitialized'
            """, Ct));
        return (pg, ch, attempt);
    }

    private static Task<int> InsertSidecarAsync(ControlPlaneDbContext db, Guid pg, Guid ch,
        string key, string digest, string payload, long sequence = 1) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO bizigo.topology_legacy_conversion_sidecars
          (original_publication_key, original_sequence, conversion_digest,
           original_payload_sha256, original_rowset_sha256, upgraded_payload_sha256,
           upgraded_rowset_sha256, original_catalog_sha256, created_generation,
           pg_database_identity, clickhouse_database_uuid)
        VALUES ({key}, {sequence}, {digest}, {payload}, {new string('d', 64)},
                {new string('e', 64)}, {new string('f', 64)}, {new string('0', 64)}, 1, {pg}, {ch})
        ON CONFLICT (original_publication_key, original_sequence) DO NOTHING
        """, Ct);

    private static Task<int> InsertSetAsync(ControlPlaneDbContext db, Guid setId, Guid pg, Guid ch,
        Guid attempt, long revision, TopologyRepairMemberSetCandidate candidate) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_member_sets
              (member_set_id, generation, copy_attempt_id, pg_database_identity,
               clickhouse_database_uuid, sampled_sidecar_revision, receipt_prefix_sequence,
               receipt_prefix_sha256, member_count, canonical_byte_length, canonical_sha256)
            VALUES ({setId}, 1, {attempt}, {pg}, {ch}, {revision}, 1, {new string('1', 64)},
                    {candidate.Count}, {candidate.CanonicalByteLength}, {candidate.CanonicalSha256})
            """, Ct);

    private static Task<long> SidecarRevisionAsync(ControlPlaneDbContext db) =>
        db.Database.SqlQueryRaw<long>(
            "SELECT sidecar_revision AS \"Value\" FROM bizigo.topology_repair_state WHERE id=1")
            .SingleAsync(Ct);
}
