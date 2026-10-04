using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Bizigo.Query;

public enum TopologyRepairStartMode { Startup, OperatorResume }
public enum TopologyRepairInitializationStatus { Ready, MaintenanceRequired, RepairIncomplete, Busy }
public sealed record TopologyRepairInitializationResult(TopologyRepairInitializationStatus Status,
    long? Generation = null, string? DiagnosticCode = null);

public interface ITopologyRepairCheckpoints
{
    Task ReachAsync(string stage, int tableIndex, CancellationToken cancellationToken);
}

public sealed class NoTopologyRepairCheckpoints : ITopologyRepairCheckpoints
{
    public Task ReachAsync(string stage, int tableIndex, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Quiesced PG-aware 0014 cutover. All four CH targets are made from verified
/// immutable manifests in fresh per-attempt tables; no surviving old edge,
/// current owner binding or reconstructed trace guess is repair authority.
/// </summary>
public sealed class TopologyPublicationRepairRunner(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ClickHouseContext clickHouse,
    TopologyObservedRepairReadiness readiness,
    ITopologyRepairCheckpoints? checkpoints = null)
{
    private const long AdvisoryLockId = 735032;
    private readonly TopologyPublicationRepairStorage storage = new(clickHouse);
    private readonly TopologyRepairSchemaInspector inspector = new(clickHouse);

    private sealed record State(string Phase, long Generation, Guid PgIdentity,
        bool AutomaticEmptyInit, long PublishedSequence, Guid? ClickHouseIdentity,
        IReadOnlyList<TopologyRepairTableIdentity>? OldCanonical,
        Dictionary<string, string[]>? AllowedCopyUuids);
    private sealed record Attestation(Guid PgIdentity, Guid ChIdentity,
        IReadOnlyList<TopologyRepairTableIdentity> OldCanonical,
        DateTimeOffset ValidUntil, bool Revoked);
    private sealed record Receipt(string Key, long Sequence);
    private sealed record RebuildAuthority(string PrefixHash, string ParentHash, string LifecycleHash,
        Receipt? Pending, bool PendingHasReceipt, string? PendingPayloadHash)
    {
        public string? UncommittedPendingKey => PendingHasReceipt ? null : Pending?.Key;
    }

    public async Task<TopologyRepairInitializationResult> InitializeAsync(TopologyRepairStartMode mode,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!db.Database.IsNpgsql())
            return new(TopologyRepairInitializationStatus.RepairIncomplete, DiagnosticCode: "pg-required");
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (!await TryLockAsync(connection, cancellationToken))
            return new(TopologyRepairInitializationStatus.Busy, DiagnosticCode: "publisher-busy");
        try
        {
            var state = await ReadStateAsync(connection, cancellationToken);
            if (state.Phase == "Ready")
            {
                try
                {
                    await readiness.RequireReadyAsync(cancellationToken);
                    if (mode == TopologyRepairStartMode.Startup
                        || (await ReadAttestationAsync(connection,
                            checked(state.Generation + 1), cancellationToken)) is null)
                        return new(TopologyRepairInitializationStatus.Ready, state.Generation);
                    // Explicitly attesting the next generation requests a
                    // quiesced rebuild even when schema identity is intact:
                    // a missing physical row can be caught by the scoped
                    // reader while the schema certificate still matches.
                }
                catch (TopologyObservedRepairUnavailableException)
                {
                    if (mode == TopologyRepairStartMode.Startup)
                        return new(TopologyRepairInitializationStatus.RepairIncomplete, state.Generation,
                            "ready-certificate-invalid");
                    // An operator may explicitly initiate a new generation
                    // only after attesting the current PG/CH identities. The
                    // invalid Ready certificate is never itself repair input.
                }
            }

            var chIdentity = Guid.Parse(await inspector.ReadDatabaseUuidAsync(cancellationToken));
            var canonicalBefore = await inspector.ReadCanonicalAsync(cancellationToken);
            long generation;
            bool automaticEmpty;
            IReadOnlyList<TopologyRepairTableIdentity> oldCanonical;
            Dictionary<string, string[]> allowedCopies;
            if (state.Phase == "Repairing")
            {
                generation = state.Generation;
                automaticEmpty = state.AutomaticEmptyInit;
                if (state.ClickHouseIdentity != chIdentity || state.OldCanonical is null
                    || state.AllowedCopyUuids is null
                    || !CurrentCanonicalIsBound(canonicalBefore, state.AllowedCopyUuids))
                    return new(TopologyRepairInitializationStatus.RepairIncomplete, generation,
                        "repair-target-identity-changed");
                oldCanonical = state.OldCanonical;
                allowedCopies = state.AllowedCopyUuids;
                var retained = await inspector.ReadRetainedTopologyUuidsAsync(cancellationToken);
                if (!oldCanonical.All(table => retained.Contains(table.Uuid)))
                    return new(TopologyRepairInitializationStatus.RepairIncomplete, generation,
                        "legacy-identity-not-retained");
                if (!automaticEmpty && !await HasValidAttestationAsync(connection, state, chIdentity,
                        oldCanonical, isResume: true, cancellationToken))
                    return new(TopologyRepairInitializationStatus.MaintenanceRequired, generation,
                        "drain-attestation-required");
            }
            else
            {
                generation = checked(state.Generation + 1);
                automaticEmpty = state.Phase == "Uninitialized"
                    && await IsFreshEmptyAsync(connection, state, cancellationToken);
                oldCanonical = canonicalBefore;
                allowedCopies = oldCanonical.ToDictionary(table => table.Name,
                    table => new[] { table.Uuid }, StringComparer.Ordinal);
                if (!automaticEmpty && (mode != TopologyRepairStartMode.OperatorResume
                    || !await HasValidAttestationAsync(connection, state with { Generation = generation },
                        chIdentity, oldCanonical, isResume: false, cancellationToken)))
                    return new(TopologyRepairInitializationStatus.MaintenanceRequired, generation,
                        "drain-attestation-required");
            }

            var copyAttempt = Guid.NewGuid();
            await EnterRepairingAsync(db, generation, copyAttempt, automaticEmpty,
                chIdentity, oldCanonical, allowedCopies, cancellationToken);
            var current = await ReadStateAsync(connection, cancellationToken);
            var committed = await new TopologyPublicationWatermarkReader(clickHouse).ReadAsync(cancellationToken);
            if (current.PublishedSequence < 0
                || (ulong)current.PublishedSequence < committed
                || (ulong)current.PublishedSequence > checked(committed + 1))
                return new(TopologyRepairInitializationStatus.RepairIncomplete, generation,
                    "publication-ack-divergent");

            var copies = await storage.CreateFreshCopiesAsync(copyAttempt, cancellationToken);
            var authority = await RebuildFromManifestsAsync(connection, copies, current.PublishedSequence,
                cancellationToken);
            if (!automaticEmpty && !await HasValidAttestationAsync(connection,
                    current with { Generation = generation }, chIdentity, oldCanonical,
                    isResume: true, cancellationToken))
                return new(TopologyRepairInitializationStatus.MaintenanceRequired, generation,
                    "drain-attestation-expired-before-swap");
            allowedCopies = await RegisterVerifiedCopyIdentitiesAsync(db, generation, copyAttempt,
                copies, allowedCopies, cancellationToken);
            // The watermark may lag a committed PG receipt by one interrupted
            // ACK. Never advance it until the corresponding physical batch is
            // present and verified in fresh targets.
            await storage.ExchangeFreshCopiesAsync(copies, async token =>
            {
                var phase = await ReadStateAsync(connection, token);
                var liveCh = Guid.Parse(await inspector.ReadDatabaseUuidAsync(token));
                var liveCanonical = await inspector.ReadCanonicalAsync(token);
                if (phase.Phase != "Repairing" || phase.Generation != generation
                    || phase.PgIdentity != current.PgIdentity || phase.ClickHouseIdentity != chIdentity
                    || liveCh != chIdentity || phase.AllowedCopyUuids is null
                    || !IdentitySetsEqual(phase.AllowedCopyUuids, allowedCopies)
                    || !CurrentCanonicalIsBound(liveCanonical, allowedCopies))
                    throw new TopologyObservedRepairUnavailableException();
                if (!automaticEmpty && !await HasValidAttestationAsync(connection, phase,
                        chIdentity, oldCanonical, isResume: true, token))
                    throw new TopologyObservedRepairUnavailableException();
            }, (tableIndex, token) => (checkpoints ?? new NoTopologyRepairCheckpoints())
                .ReachAsync("after-canonical-exchange", tableIndex, token), cancellationToken);
            await VerifyCanonicalAuthorityAsync(connection, current.PublishedSequence,
                authority.Pending, authority.PendingHasReceipt, cancellationToken);
            if ((ulong)current.PublishedSequence != committed)
                await new TopologyPublicationWatermarkWriter(
                    new TopologyPublicationWatermarkReader(clickHouse), clickHouse)
                    .CommitAsync((ulong)current.PublishedSequence, cancellationToken);
            // A crash after the PG receipt commit, but before CH ACK or the
            // pending DELETE, leaves the exact receipted (key,sequence) pair.
            // Its batch is already in the verified receipt prefix; never copy
            // or hash it a second time as an uncommitted publication.
            if (authority.PendingHasReceipt && authority.Pending is { } residue)
                await DeleteReceiptedPendingAsync(db, residue, cancellationToken);

            var canonical = await inspector.ReadCanonicalAsync(cancellationToken);
            if (canonical.Any(table => !TopologyObservedRepairReadiness.HasRequiredVersionKey(table))
                || !await inspector.HasVersionedObservedEngineAsync(cancellationToken)
                || !await inspector.HasReadyColumnShapesAsync(cancellationToken))
                return new(TopologyRepairInitializationStatus.RepairIncomplete, generation,
                    "canonical-schema-invalid");
            if (!automaticEmpty && !await HasValidAttestationAsync(connection,
                    current with { Generation = generation }, chIdentity, canonical,
                    isResume: true, cancellationToken))
                return new(TopologyRepairInitializationStatus.MaintenanceRequired, generation,
                    "drain-attestation-expired-before-certificate");
            await MarkReadyV2Async(db, generation, current.PgIdentity, chIdentity,
                current.PublishedSequence, authority, canonical, copyAttempt, automaticEmpty,
                cancellationToken);
            await readiness.RequireReadyAsync(cancellationToken);
            return new(TopologyRepairInitializationStatus.Ready, generation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed copy/manifest/identity check leaves PG Repairing and
            // never issues a Ready certificate. The public path is generic 503.
            return new(TopologyRepairInitializationStatus.RepairIncomplete,
                DiagnosticCode: "authority-or-copy-incomplete");
        }
        finally { await UnlockAsync(connection); }
    }

    /// <summary>
    /// Operator-facing API: the caller explicitly attests that old binaries
    /// have stopped and will remain drained through the supplied window.
    /// Software captures identities but does not infer the human assertion.
    /// </summary>
    public async Task<long> RecordOperatorDrainAsync(string operatorSubject, string drainStatement,
        DateTimeOffset validUntil, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operatorSubject) || string.IsNullOrWhiteSpace(drainStatement)
            || validUntil <= DateTimeOffset.UtcNow)
            throw new ArgumentException("An explicit live operator drain attestation is required.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!db.Database.IsNpgsql()) throw new NotSupportedException("PostgreSQL authority is required.");
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        if (!await TryLockAsync(connection, cancellationToken))
            throw new TopologyObservedRepairUnavailableException();
        try
        {
            var state = await ReadStateAsync(connection, cancellationToken);
            var generation = state.Phase == "Repairing" ? state.Generation : checked(state.Generation + 1);
            var chIdentity = Guid.Parse(await inspector.ReadDatabaseUuidAsync(cancellationToken));
            var currentCanonical = await inspector.ReadCanonicalAsync(cancellationToken);
            var previous = await ReadAttestationAsync(connection, generation, cancellationToken);
            IReadOnlyList<TopologyRepairTableIdentity> oldCanonical;
            if (state.Phase == "Repairing")
            {
                var retained = await inspector.ReadRetainedTopologyUuidsAsync(cancellationToken);
                // A partial EXCHANGE changes canonical names. A renewed human
                // drain statement may extend the same generation, but it may
                // never re-identify the displaced legacy tables from the now
                // mixed canonical view.
                if (state.ClickHouseIdentity != chIdentity || state.OldCanonical is null
                    || state.AllowedCopyUuids is null
                    || !CurrentCanonicalIsBound(currentCanonical, state.AllowedCopyUuids)
                    || previous is null || previous.PgIdentity != state.PgIdentity
                    || previous.ChIdentity != chIdentity
                    || !previous.OldCanonical.OrderBy(table => table.Name, StringComparer.Ordinal)
                        .SequenceEqual(state.OldCanonical.OrderBy(table => table.Name, StringComparer.Ordinal))
                    || !previous.OldCanonical.All(table => retained.Contains(table.Uuid)))
                    throw new TopologyObservedRepairUnavailableException();
                oldCanonical = previous.OldCanonical;
            }
            else
            {
                if (previous is not null && (previous.PgIdentity != state.PgIdentity
                    || previous.ChIdentity != chIdentity
                    || !previous.OldCanonical.OrderBy(table => table.Name, StringComparer.Ordinal)
                        .SequenceEqual(currentCanonical.OrderBy(table => table.Name, StringComparer.Ordinal))))
                    throw new TopologyObservedRepairUnavailableException();
                oldCanonical = previous?.OldCanonical ?? currentCanonical;
            }
            var oldJson = JsonSerializer.Serialize(oldCanonical);
            int affected;
            if (previous is null)
                affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO bizigo.topology_repair_attestations
                    (generation, pg_database_identity, clickhouse_database_uuid,
                     old_canonical_identity_json, operator_subject, drain_statement, valid_until)
                VALUES ({generation}, {state.PgIdentity}, {chIdentity}, CAST({oldJson} AS jsonb),
                        {operatorSubject}, {drainStatement}, {validUntil})
                """, cancellationToken);
            else
                affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE bizigo.topology_repair_attestations
                SET operator_subject = {operatorSubject}, drain_statement = {drainStatement},
                    attested_at = now(), valid_until = {validUntil}, revoked_at = NULL
                WHERE generation = {generation}
                  AND pg_database_identity = {state.PgIdentity}
                  AND clickhouse_database_uuid = {chIdentity}
                """, cancellationToken);
            if (affected != 1) throw new InvalidDataException("Operator attestation changed concurrently.");
            return generation;
        }
        finally { await UnlockAsync(connection); }
    }

    private async Task<RebuildAuthority> RebuildFromManifestsAsync(DbConnection connection,
        TopologyRepairCopyTables copies, long publishedSequence, CancellationToken token)
    {
        using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var parents = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var lifecycle = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long last = 0;
        Receipt? lastReceipt = null;
        while (last < publishedSequence)
        {
            var page = await ReadReceiptPageAsync(connection, last, publishedSequence, token);
            if (page.Count == 0) throw new InvalidDataException("Committed publication receipt prefix has a gap.");
            foreach (var receipt in page)
            {
                if (receipt.Sequence != checked(last + 1))
                    throw new InvalidDataException("Committed publication receipt prefix is not contiguous.");
                var manifest = await storage.ReadVerifiedManifestAsync(receipt.Key, token)
                    ?? throw new InvalidDataException("Committed publication manifest is missing.");
                await storage.WriteVerifiedBatchAsync(copies, manifest.Batch, checked((ulong)receipt.Sequence), token);
                await storage.VerifyCopiedBatchAsync(copies, manifest.Batch, checked((ulong)receipt.Sequence), token);
                AppendPrefix(prefix, receipt.Sequence, receipt.Key, manifest);
                AppendDecisions(parents, lifecycle, receipt.Sequence, manifest.Batch);
                last = receipt.Sequence;
                lastReceipt = receipt;
            }
        }
        var pending = await ReadPendingAsync(connection, token);
        string? pendingPayload = null;
        var pendingHasReceipt = false;
        if (pending is not null)
        {
            var receiptedSequence = await ReadReceiptSequenceByKeyAsync(connection, pending.Key, token);
            pendingHasReceipt = receiptedSequence == pending.Sequence
                && pending.Sequence == publishedSequence && lastReceipt == pending;
            if (!pendingHasReceipt && (receiptedSequence is not null
                || pending.Sequence != checked(publishedSequence + 1)))
                throw new InvalidDataException("Pending publication sequence is not contiguous with PG receipts.");
            if (!pendingHasReceipt)
            {
                var manifest = await storage.ReadVerifiedManifestAsync(pending.Key, token)
                    ?? throw new InvalidDataException("Pending publication manifest is missing.");
                await storage.WriteVerifiedBatchAsync(copies, manifest.Batch, checked((ulong)pending.Sequence), token);
                await storage.VerifyCopiedBatchAsync(copies, manifest.Batch, checked((ulong)pending.Sequence), token);
                AppendDecisions(parents, lifecycle, pending.Sequence, manifest.Batch);
                pendingPayload = manifest.PayloadSha256;
            }
        }
        return new(Hex(prefix.GetHashAndReset()), Hex(parents.GetHashAndReset()),
            Hex(lifecycle.GetHashAndReset()), pending, pendingHasReceipt, pendingPayload);
    }

    private async Task VerifyCanonicalAuthorityAsync(DbConnection connection, long publishedSequence,
        Receipt? expectedPending, bool pendingHasReceipt, CancellationToken token)
    {
        long edges = 0, conflicts = 0, parents = 0;
        long last = 0;
        while (last < publishedSequence)
        {
            var page = await ReadReceiptPageAsync(connection, last, publishedSequence, token);
            if (page.Count == 0) throw new InvalidDataException("Committed receipt prefix changed after exchange.");
            foreach (var receipt in page)
            {
                if (receipt.Sequence != checked(last + 1))
                    throw new InvalidDataException("Committed receipt prefix changed after exchange.");
                var manifest = await storage.ReadVerifiedManifestAsync(receipt.Key, token)
                    ?? throw new InvalidDataException("Committed manifest disappeared after exchange.");
                await storage.VerifyCanonicalBatchAsync(manifest.Batch, checked((ulong)receipt.Sequence), token);
                edges = checked(edges + manifest.Batch.Edges.Count);
                conflicts = checked(conflicts + manifest.Batch.Conflicts.Count);
                parents = checked(parents + manifest.Batch.ParentResolutions.Count);
                last = receipt.Sequence;
            }
        }
        var pending = await ReadPendingAsync(connection, token);
        if (pending != expectedPending
            || pending is not null && pending.Sequence !=
                (pendingHasReceipt ? publishedSequence : checked(publishedSequence + 1)))
            throw new InvalidDataException("Pending publication changed after exchange.");
        if (pending is not null && !pendingHasReceipt)
        {
            var manifest = await storage.ReadVerifiedManifestAsync(pending.Key, token)
                ?? throw new InvalidDataException("Pending manifest disappeared after exchange.");
            await storage.VerifyCanonicalBatchAsync(manifest.Batch, checked((ulong)pending.Sequence), token);
            edges = checked(edges + manifest.Batch.Edges.Count);
            conflicts = checked(conflicts + manifest.Batch.Conflicts.Count);
            parents = checked(parents + manifest.Batch.ParentResolutions.Count);
        }
        await storage.VerifyCanonicalTotalsAsync(edges, conflicts, parents, token);
    }

    private static async Task DeleteReceiptedPendingAsync(ControlPlaneDbContext db,
        Receipt pending, CancellationToken token)
    {
        var removed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM bizigo.topology_publication_pending
            WHERE id = 1 AND publication_key = {pending.Key}
              AND publication_sequence = {pending.Sequence}
              AND EXISTS (
                SELECT 1 FROM bizigo.topology_publication_receipts
                WHERE publication_key = {pending.Key} AND publication_sequence = {pending.Sequence})
            """, token);
        if (removed != 1)
            throw new InvalidDataException("Receipted pending publication changed during repair.");
    }

    private static void AppendPrefix(IncrementalHash hash, long sequence, string key,
        TopologyVerifiedManifest manifest)
    {
        AppendLine(hash, sequence, key, manifest.PayloadSha256, manifest.RowsetSha256);
    }

    private static void AppendDecisions(IncrementalHash parentHash, IncrementalHash lifecycleHash,
        long sequence, TopologyProjectionBatch batch)
    {
        foreach (var decision in batch.ParentResolutions.OrderBy(item => item.ChildAnchor, StringComparer.Ordinal)
                     .ThenBy(item => item.ChildFingerprint, StringComparer.Ordinal))
            AppendLine(parentHash, sequence, JsonSerializer.Serialize(decision));
        foreach (var edge in batch.Edges.OrderBy(item => item.EdgeId, StringComparer.Ordinal))
        {
            var row = TopologyObservedProjector.ReconstructEdgeRow(edge, checked((ulong)sequence));
            AppendLine(lifecycleHash, sequence, edge.EdgeId, TopologyObservedRowDigest.Compute(row));
        }
    }

    private static void AppendLine(IncrementalHash hash, params object[] fields)
    {
        var line = string.Join(":", fields.Select(field => Convert.ToString(field, CultureInfo.InvariantCulture))) + "\n";
        hash.AppendData(Encoding.UTF8.GetBytes(line));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static async Task<IReadOnlyList<Receipt>> ReadReceiptPageAsync(DbConnection connection,
        long after, long published, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT publication_key, publication_sequence
            FROM bizigo.topology_publication_receipts
            WHERE publication_sequence > @after AND publication_sequence <= @published
            ORDER BY publication_sequence LIMIT 256
            """;
        AddParameter(command, "after", after);
        AddParameter(command, "published", published);
        var result = new List<Receipt>(256);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var key = reader.GetString(0);
            if (!IsSha256(key)) throw new InvalidDataException("Publication receipt key is malformed.");
            result.Add(new(key, reader.GetInt64(1)));
        }
        return result;
    }

    private static async Task<Receipt?> ReadPendingAsync(DbConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT publication_key, publication_sequence FROM bizigo.topology_publication_pending
            WHERE id = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var key = reader.GetString(0);
        if (!IsSha256(key)) throw new InvalidDataException("Pending publication key is malformed.");
        var result = new Receipt(key, reader.GetInt64(1));
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Ambiguous pending publication.");
        return result;
    }

    private static async Task<long?> ReadReceiptSequenceByKeyAsync(DbConnection connection,
        string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT publication_sequence FROM bizigo.topology_publication_receipts "
            + "WHERE publication_key = @key";
        AddParameter(command, "key", key);
        var result = await command.ExecuteScalarAsync(token);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static bool IsSha256(string value) => value.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool CurrentCanonicalIsBound(IReadOnlyList<TopologyRepairTableIdentity> current,
        IReadOnlyDictionary<string, string[]> allowed)
    {
        if (allowed.Count != TopologyRepairSchemaInspector.CanonicalNames.Count
            || current.Count != allowed.Count) return false;
        foreach (var table in current)
            if (!allowed.TryGetValue(table.Name, out var identities)
                || identities.Length is < 1 or > 256
                || !identities.Contains(table.Uuid, StringComparer.Ordinal))
                return false;
        return true;
    }

    private static bool IdentitySetsEqual(IReadOnlyDictionary<string, string[]> actual,
        IReadOnlyDictionary<string, string[]> expected) => actual.Count == expected.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var values)
            && values.Order(StringComparer.Ordinal).SequenceEqual(
                pair.Value.Order(StringComparer.Ordinal), StringComparer.Ordinal));

    private async Task<Dictionary<string, string[]>> RegisterVerifiedCopyIdentitiesAsync(
        ControlPlaneDbContext db, long generation, Guid attempt,
        TopologyRepairCopyTables copies, Dictionary<string, string[]> prior, CancellationToken token)
    {
        var identities = await inspector.ReadCopyUuidsAsync(copies, token);
        var next = prior.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var (canonical, uuid) in identities)
        {
            if (!next.TryGetValue(canonical, out var entries) || entries.Length >= 256)
                throw new InvalidDataException("Topology repair copy identity cap exceeded.");
            next[canonical] = entries.Append(uuid).Distinct(StringComparer.Ordinal).ToArray();
        }
        var json = JsonSerializer.Serialize(next);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state
            SET allowed_copy_identity_json = CAST({json} AS jsonb), updated_at = now()
            WHERE id = 1 AND phase = 'Repairing' AND generation = {generation}
              AND copy_attempt_id = {attempt}
            """, token);
        if (updated != 1) throw new InvalidDataException("Repair copy identity reservation changed.");
        return next;
    }

    private async Task<bool> IsFreshEmptyAsync(DbConnection connection, State state, CancellationToken token)
    {
        if (state.PublishedSequence != 0) return false;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM bizigo.topology_publication_receipts)
                 + (SELECT count(*) FROM bizigo.topology_publication_pending)
                 + (SELECT count(*) FROM bizigo.topology_legacy_conversion_sidecars)
                 + (SELECT count(*) FROM bizigo.topology_repair_member_sets)
                 + (SELECT count(*) FROM bizigo.topology_repair_members)
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != 0)
            return false;
        return await storage.IsProvablyEmptyAsync(token);
    }

    private async Task<bool> HasValidAttestationAsync(DbConnection connection, State state, Guid chIdentity,
        IReadOnlyList<TopologyRepairTableIdentity> canonical, bool isResume, CancellationToken token)
    {
        var attestation = await ReadAttestationAsync(connection, state.Generation, token);
        if (attestation is null || attestation.Revoked || attestation.ValidUntil <= DateTimeOffset.UtcNow
            || attestation.PgIdentity != state.PgIdentity || attestation.ChIdentity != chIdentity
            || attestation.OldCanonical.Count != canonical.Count)
            return false;
        if (!isResume)
            return attestation.OldCanonical.OrderBy(t => t.Name, StringComparer.Ordinal)
                .SequenceEqual(canonical.OrderBy(t => t.Name, StringComparer.Ordinal));
        if (state.ClickHouseIdentity != chIdentity || state.OldCanonical is null
            || !attestation.OldCanonical.OrderBy(t => t.Name, StringComparer.Ordinal)
                .SequenceEqual(state.OldCanonical.OrderBy(t => t.Name, StringComparer.Ordinal)))
            return false;
        var retained = await inspector.ReadRetainedTopologyUuidsAsync(token);
        return attestation.OldCanonical.All(table => retained.Contains(table.Uuid));
    }

    private static async Task<Attestation?> ReadAttestationAsync(DbConnection connection, long generation,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pg_database_identity, clickhouse_database_uuid,
                   old_canonical_identity_json::text, valid_until, revoked_at
            FROM bizigo.topology_repair_attestations WHERE generation = @generation
            """;
        AddParameter(command, "generation", generation);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var old = JsonSerializer.Deserialize<TopologyRepairTableIdentity[]>(reader.GetString(2))
            ?? throw new InvalidDataException("Repair attestation table identity is missing.");
        var result = new Attestation(reader.GetGuid(0), reader.GetGuid(1), old,
            reader.GetFieldValue<DateTimeOffset>(3), !reader.IsDBNull(4));
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Ambiguous repair attestation.");
        return result;
    }

    private static async Task<State> ReadStateAsync(DbConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.phase, r.generation, r.pg_database_identity,
                   r.automatic_empty_init, s.published_sequence,
                   r.clickhouse_database_uuid, r.old_canonical_identity_json::text,
                   r.allowed_copy_identity_json::text
            FROM bizigo.topology_repair_state r
            CROSS JOIN bizigo.topology_read_state s
            WHERE r.id = 1 AND s.id = 1
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Repair state is missing.");
        var result = new State(reader.GetString(0), reader.GetInt64(1), reader.GetGuid(2),
            reader.GetBoolean(3), reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<TopologyRepairTableIdentity[]>(reader.GetString(6)),
            reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, string[]>>(reader.GetString(7)));
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Ambiguous repair state.");
        return result;
    }

    private static async Task EnterRepairingAsync(ControlPlaneDbContext db, long generation,
        Guid copyAttempt, bool automaticEmpty, Guid chIdentity,
        IReadOnlyList<TopologyRepairTableIdentity> oldCanonical,
        Dictionary<string, string[]> allowedCopies, CancellationToken token)
    {
        var oldJson = JsonSerializer.Serialize(oldCanonical);
        var allowedJson = JsonSerializer.Serialize(allowedCopies);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state
            SET phase = 'Repairing', generation = {generation}, copy_attempt_id = {copyAttempt},
                clickhouse_database_uuid = {chIdentity},
                old_canonical_identity_json = CAST({oldJson} AS jsonb),
                allowed_copy_identity_json = CAST({allowedJson} AS jsonb),
                automatic_empty_init = {automaticEmpty}, certificate_digest = NULL,
                certificate_json = NULL, certificate_member_set_id = NULL,
                receipt_prefix_sha256 = NULL,
                receipt_prefix_sequence = 0, updated_at = now()
            WHERE id = 1 AND generation <= {generation}
            """, token);
        if (updated != 1) throw new InvalidDataException("Repair phase changed unexpectedly.");
        await db.Database.ExecuteSqlRawAsync("UPDATE bizigo.topology_read_state SET epoch = epoch + 1 WHERE id = 1", token);
        await transaction.CommitAsync(token);
    }

    private static async Task MarkReadyV2Async(ControlPlaneDbContext db, long generation,
        Guid pgIdentity, Guid chIdentity, long prefixSequence, RebuildAuthority authority,
        IReadOnlyList<TopologyRepairTableIdentity> canonical, Guid copyAttempt,
        bool automaticEmpty, CancellationToken token)
    {
        // The repair storage only accepts verified v4 manifests. Its pre-v4
        // rejection is the classification source; no caller-provided boolean
        // can turn an unauthenticated legacy publication into a native one.
        var candidate = TopologyRepairMembershipBuilder.Build(
            Array.Empty<TopologyRepairMembershipPublication>(), 0, null);
        if (candidate.Count != 0)
            throw new TopologyObservedRepairUnavailableException();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var connection = db.Database.GetDbConnection();
        long sidecarRevision;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                SELECT r.sidecar_revision, s.published_sequence,
                       p.publication_key, p.publication_sequence
                FROM bizigo.topology_repair_state r
                CROSS JOIN bizigo.topology_read_state s
                LEFT JOIN bizigo.topology_publication_pending p ON p.id = 1
                WHERE r.id = 1 AND s.id = 1 AND r.phase = 'Repairing'
                  AND r.generation = @generation AND r.copy_attempt_id = @attempt
                  AND r.pg_database_identity = @pg AND r.clickhouse_database_uuid = @ch
                FOR UPDATE OF r, s
                """;
            AddParameter(command, "generation", generation);
            AddParameter(command, "attempt", copyAttempt);
            AddParameter(command, "pg", pgIdentity);
            AddParameter(command, "ch", chIdentity);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || reader.GetInt64(1) != prefixSequence
                || (reader.IsDBNull(2) ? null : reader.GetString(2)) != authority.UncommittedPendingKey
                || (reader.IsDBNull(3) ? null : reader.GetInt64(3)) !=
                    (authority.UncommittedPendingKey is null ? null : authority.Pending!.Sequence))
                throw new InvalidDataException("Repair publication state changed before certification.");
            sidecarRevision = reader.GetInt64(0);
            if (await reader.ReadAsync(token))
                throw new InvalidDataException("Ambiguous repair publication state.");
        }

        var memberSetId = Guid.NewGuid();
        var pendingKey = authority.UncommittedPendingKey;
        long? pendingSequence = pendingKey is null ? null : authority.Pending!.Sequence;
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO bizigo.topology_repair_member_sets
              (member_set_id, generation, copy_attempt_id, pg_database_identity,
               clickhouse_database_uuid, sampled_sidecar_revision, receipt_prefix_sequence,
               receipt_prefix_sha256, pending_publication_key, pending_sequence,
               pending_payload_sha256, member_count, canonical_byte_length, canonical_sha256)
            VALUES ({memberSetId}, {generation}, {copyAttempt}, {pgIdentity}, {chIdentity},
                    {sidecarRevision}, {prefixSequence}, {authority.PrefixHash},
                    {pendingKey}, {pendingSequence}, {authority.PendingPayloadHash}, {candidate.Count},
                    {candidate.CanonicalByteLength}, {candidate.CanonicalSha256})
            """, token);
        if (inserted != 1) throw new InvalidDataException("Repair member set was not inserted.");
        var sealedCount = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_member_sets SET sealed_at = now()
            WHERE member_set_id = {memberSetId} AND sealed_at IS NULL
            """, token);
        if (sealedCount != 1) throw new InvalidDataException("Repair member set was not sealed.");

        var certificate = new TopologyRepairCertificateV2(2, generation, pgIdentity, chIdentity,
            prefixSequence, authority.PrefixHash, pendingKey, authority.PendingPayloadHash,
            authority.ParentHash, authority.LifecycleHash, canonical, memberSetId,
            sidecarRevision, candidate.Count, candidate.CanonicalSha256);
        var json = JsonSerializer.Serialize(certificate);
        var digest = TopologyObservedRepairReadiness.Digest(certificate);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE bizigo.topology_repair_state
            SET phase = 'Ready', certificate_digest = {digest}, certificate_json = CAST({json} AS jsonb),
                certificate_member_set_id = {memberSetId},
                receipt_prefix_sequence = {prefixSequence},
                receipt_prefix_sha256 = {authority.PrefixHash}, updated_at = now()
            WHERE id = 1 AND phase = 'Repairing' AND generation = {generation}
              AND copy_attempt_id = {copyAttempt}
              AND pg_database_identity = {pgIdentity}
              AND clickhouse_database_uuid = {chIdentity}
              AND sidecar_revision = {sidecarRevision}
              AND (SELECT published_sequence FROM bizigo.topology_read_state WHERE id = 1) = {prefixSequence}
              AND ({automaticEmpty} OR EXISTS (
                    SELECT 1 FROM bizigo.topology_repair_attestations a
                    WHERE a.generation = {generation}
                      AND a.pg_database_identity = {pgIdentity}
                      AND a.clickhouse_database_uuid = {chIdentity}
                      AND a.revoked_at IS NULL AND a.valid_until > now()))
            """, token);
        if (updated != 1) throw new InvalidDataException("Repair phase changed before certification.");
        await db.Database.ExecuteSqlRawAsync("UPDATE bizigo.topology_read_state SET epoch = epoch + 1 WHERE id = 1", token);
        await transaction.CommitAsync(token);
    }

    private static async Task<bool> TryLockAsync(DbConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_try_advisory_lock({AdvisoryLockId})";
        return Convert.ToBoolean(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task UnlockAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT pg_advisory_unlock({AdvisoryLockId})";
        await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
