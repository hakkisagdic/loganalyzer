using System.Text;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Storage.ClickHouse;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only real database boundary and failure fixtures.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TelemetryBoundaryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_unsigned_time_and_expiry_neighbors_roundtrip_without_rounding(bool traces)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("time", "A", f.Clock.GetUtcNow().AddDays(-100));
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        var table = traces ? "trace_spans" : "metric_points";
        await f.SqlAsync("SYSTEM STOP MERGES " + table);
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var threshold = f.Now - 90 * 86400000000000UL;
        foreach (var timestamp in new[] { threshold - 1, threshold, threshold + 1, ulong.MaxValue - 1 })
        {
            IMessage payload = traces ? TelemetryDbFixture.Traces("time", timestamp) : TelemetryDbFixture.Metrics("time", timestamp, false);
            await f.EmitAsync(ingest, payload, signal);
        }
        Assert.Equal("4", (await f.SqlAsync("SELECT count() FROM " + table + " FINAL")).Trim());
        var scope = AccessScope.ForGroups("nano", ["A"]);
        var query = new TelemetryQuery { Signal = signal, FromNano = threshold - 1, ToNano = threshold + 2 };
        Assert.Equal(threshold + 1, Assert.Single((await f.Query.SearchTelemetryAsync(query, scope, Ct)).Records).TimeUnixNano);
        Assert.Equal(1, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        Assert.Equal(1, Assert.Single((await f.Query.SummarizeTelemetryAsync(query, scope, Ct)).Groups).Count);
        Assert.Equal(1, (await f.Query.CountOutOfScopeTelemetryAsync(query, AccessScope.ForGroups("B", ["B"]), Ct)).Count);
        var top = query with { FromNano = ulong.MaxValue - 1, ToNano = (decimal)ulong.MaxValue + 1 };
        Assert.Equal(ulong.MaxValue - 1, Assert.Single((await f.Query.SearchTelemetryAsync(top, scope, Ct)).Records).TimeUnixNano);
        Assert.Empty((await f.Query.SearchTelemetryAsync(top with { FromNano = ulong.MaxValue }, scope, Ct)).Records);
        await f.SqlAsync("SYSTEM START MERGES " + table);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Feed_insert_failure_and_partial_leaf_batch_leave_retryable_WAL()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("retry", "A");
        using (var ingest = f.Open())
        {
            await ingest.RecoverAsync(Ct);
            var admission = await ingest.AcceptAsync(TelemetrySignal.Metrics, TelemetryDbFixture.Metrics("retry", f.Now).ToByteArray(), "application/x-protobuf", Ct);
            Assert.Equal(200, admission.Status);
            var work = await ingest.Reader.ReadAsync(Ct);
            await f.SqlAsync("RENAME TABLE telemetry_feed_history TO hidden_feed_history");
            try { await Assert.ThrowsAnyAsync<Exception>(() => ingest.ProcessAsync(work, Ct)); }
            finally { await f.SqlAsync("RENAME TABLE hidden_feed_history TO telemetry_feed_history"); ingest.CompleteWork(); }
            Assert.False(File.Exists(Path.Combine(f.Root, "processed", admission.EnvelopeId!.Value.ToString("N") + ".json")));
            await ingest.SweepAsync(Ct); Assert.NotEmpty(ingest.Wal.ListSealedSegments());
        }
        using (var recovered = f.Open())
        {
            await recovered.RecoverAsync(Ct);
            var scope = AccessScope.ForGroups("feed", ["A"]);
            Assert.Equal(5, (await f.Query.CountTelemetryAsync(f.Window(TelemetrySignal.Metrics, "retry"), scope, Ct)).Count);
            Assert.Equal(TelemetryResultStatus.Data, (await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Metrics, "retry", scope, Ct)).Status);
            Assert.Equal(TelemetryResultStatus.NeverFed, (await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Traces, "retry", scope, Ct)).Status);
            await recovered.SweepAsync(Ct); Assert.Empty(recovered.Wal.ListSealedSegments());
        }
        var partialRoot = Path.Combine(f.Root, "partial");
        using (var interrupted = f.Open(partialRoot, new PartialSink(f.Writer)))
        {
            await interrupted.RecoverAsync(Ct);
            var accepted = await interrupted.AcceptAsync(TelemetrySignal.Metrics, TelemetryDbFixture.Metrics("retry", f.Now + 100).ToByteArray(), "application/x-protobuf", Ct);
            Assert.Equal(200, accepted.Status);
            var work = await interrupted.Reader.ReadAsync(Ct);
            await Assert.ThrowsAsync<IOException>(() => interrupted.ProcessAsync(work, Ct)); interrupted.CompleteWork();
            Assert.False(File.Exists(Path.Combine(partialRoot, "processed", accepted.EnvelopeId!.Value.ToString("N") + ".json")));
            await interrupted.SweepAsync(Ct); Assert.NotEmpty(interrupted.Wal.ListSealedSegments());
        }
        using var retry = f.Open(partialRoot); await retry.RecoverAsync(Ct);
        Assert.Equal(10, (await f.Query.CountTelemetryAsync(f.Window(TelemetrySignal.Metrics), AccessScope.System("partial"), Ct)).Count);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Corrupt_typed_row_and_audit_storage_failure_cannot_return_success()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("invalid", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("invalid", f.Now, false), TelemetrySignal.Metrics);
        var scope = AccessScope.ForGroups("invalid-" + Guid.NewGuid().ToString("N"), ["A"]);
        var query = f.Window(TelemetrySignal.Metrics);
        var valid = await f.Query.SearchTelemetryAsync(query, scope, Ct);
        var logicalId = Assert.Single(valid.Records).LogicalId;
        await f.SqlAsync("ALTER TABLE metric_points UPDATE record_version=2 WHERE 1 SETTINGS mutations_sync=2");
        var invalid = await f.Query.SearchTelemetryAsync(query, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Failed, invalid.Status); Assert.Equal("InvalidTypedRecord", invalid.Error);
        Assert.Empty(invalid.Records);
        var detail = await f.Query.GetMetricPointAsync(logicalId, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Failed, detail.Status); Assert.Equal("InvalidTypedRecord", detail.Error);
        Assert.Empty(detail.Records);
        var summary = await f.Query.SummarizeTelemetryAsync(query, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Failed, summary.Status); Assert.Equal("InvalidTypedRecord", summary.Error);
        Assert.Empty(summary.Groups);
        await f.Db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION bizigo.telemetry_audit_test_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture audit unavailable'; END $$;
            CREATE TRIGGER telemetry_audit_test_fault BEFORE INSERT ON bizigo.audit_log
            FOR EACH ROW EXECUTE FUNCTION bizigo.telemetry_audit_test_fault();
            """, Ct);
        try { await Assert.ThrowsAsync<DbUpdateException>(() => f.Query.CountTelemetryAsync(query, scope, Ct)); }
        finally
        {
            await f.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER telemetry_audit_test_fault ON bizigo.audit_log; DROP FUNCTION bizigo.telemetry_audit_test_fault();", Ct);
        }
    }

    private sealed class PartialSink(ITelemetrySink actual) : ITelemetrySink
    {
        public async Task WriteAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken token)
        { await actual.WriteAsync([records[0]], token); throw new IOException("Fixture interrupted remaining batch after first actual DB leaf."); }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Legacy_archive_upgrades_to_durable_unknown_without_current_inventory_guess()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("legacy", "A");
        var raw = TelemetryDbFixture.Metrics("legacy", f.Now, false).ToByteArray();
        var decoded = new OtlpTelemetryDecoder().Decode(TelemetrySignal.Metrics, raw, "application/x-protobuf");
        var legacy = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Metrics, "application/x-protobuf", DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(raw), raw, 1, decoded.Accepted.Select(l => l.Key).ToArray(), 0);
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await ingest.Archive.ArchiveAsync(legacy, "legacy-fixture", Ct);
        await ingest.ReplayArchiveAsync(Ct);
        var query = f.Window(TelemetrySignal.Metrics, "legacy");
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("A", ["A"]), Ct)).Records);
        var row = Assert.Single((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("unknown", ["_unassigned"]), Ct)).Records);
        Assert.Equal("legacy-owner-unknown", row.Owner.Reason); Assert.Equal(legacy.PayloadSha256, row.PayloadSha256);
        Assert.Equal(legacy.EnvelopeId.ToString("N") + "/" + legacy.AcceptedKeys[0], row.LogicalId);
        var upgraded = await ingest.Archive.ReadAsync(Assert.Single(ingest.Archive.Manifests()), Ct);
        Assert.Equal(2, upgraded.Version); Assert.Equal(raw, upgraded.Payload); Assert.NotNull(upgraded.OwnerBindingsSha256);
        await f.SourceAsync("legacy", "B", f.Clock.GetUtcNow().AddSeconds(1)); await ingest.ReplayArchiveAsync(Ct);
        Assert.Empty((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("B", ["B"]), Ct)).Records);
        Assert.Single((await f.Query.SearchTelemetryAsync(query, AccessScope.ForGroups("unknown", ["_unassigned"]), Ct)).Records);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Additive_partial_migration_resumes_preserves_old_events_and_rejects_drift()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var row = new LogEvent { EventId = Guid.NewGuid(), Timestamp = DateTimeOffset.UtcNow, OwnerGroup = "A", SourceId = "old-log", Body = "migration-sentinel" };
        await new EventWriter(f.Storage).WriteEventsAsync([row], Ct);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM events WHERE source_id='old-log'")).Trim());
        // Exactly the durable state after statement one but before the migration
        // commit marker: metric table exists, later tables/marker do not.
        await f.SqlAsync("DROP TABLE trace_spans"); await f.SqlAsync("DROP TABLE telemetry_feed_history");
        await f.SqlAsync("ALTER TABLE schema_migrations DELETE WHERE version='0007_telemetry' SETTINGS mutations_sync=2");
        var migrator = new ClickHouseMigrator(f.Storage);
        var resumed = await migrator.MigrateAsync(DevStackSetup.RepoPath("db/clickhouse"), Ct);
        Assert.Equal(new[] { "0007_telemetry" }, resumed.Applied);
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM events WHERE source_id='old-log'")).Trim());
        Assert.Contains("ReplacingMergeTree", await f.SqlAsync("SHOW CREATE TABLE trace_spans"), StringComparison.Ordinal);
        Assert.Empty((await migrator.MigrateAsync(DevStackSetup.RepoPath("db/clickhouse"), Ct)).Applied);
        var drift = Path.Combine(f.Root, "migration-drift"); Directory.CreateDirectory(drift);
        foreach (var path in Directory.GetFiles(DevStackSetup.RepoPath("db/clickhouse"), "*.sql"))
            File.Copy(path, Path.Combine(drift, Path.GetFileName(path)));
        await File.AppendAllTextAsync(Path.Combine(drift, "0007_telemetry.sql"), "\n-- fixture changed applied migration\n", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(drift, Ct));
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM events WHERE source_id='old-log'")).Trim());
    }
}
