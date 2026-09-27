using System.Diagnostics;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: upgrade actual previous PostgreSQL schema with old
/// reviews, then repeat the migration and read mixed records in two fresh processes.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GoldenReviewMigrationIntegrationTests(DevStackFixture stack)
{
    [Fact]
    public async Task Migration_preserves_unasked_reviews_and_fresh_process_exports()
    {
        var ct = TestContext.Current.CancellationToken;
        var database = "s04_reviews_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(stack.PostgresConnectionString) { Database = database }.ConnectionString;
        await using var admin = new NpgsqlConnection(stack.PostgresConnectionString); await admin.OpenAsync(ct);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await create.ExecuteNonQueryAsync(ct);
        var directory = Path.Combine(Path.GetTempPath(), database); Directory.CreateDirectory(directory);
        try
        {
            var factory = new ControlPlaneFactory(connection);
            await using var db = factory.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync("20260926160000_AddTelemetryOwnership", ct);
            var expected = new List<GoldenReviewEntity>();
            for (var version = 1; version <= 2; version++)
            {
                var row = new GoldenReviewEntity { Id = Guid.NewGuid(), BundleId = Guid.NewGuid(), SchemaVersion = version,
                    OwnerGroup = "A", ReviewerSubject = "old-reviewer", Verdict = ReviewVerdict.Correct,
                    Note = "old-note-" + version, ActualRootCause = "old-cause", CorrectFindingRank = version,
                    CorrectFindingRankAsked = true, ReviewedAt = new DateTimeOffset(2026, 9, 1, 0, 0, version, TimeSpan.Zero) };
                expected.Add(row);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO bizigo.golden_reviews
                    (id,bundle_id,owner_group,verdict,contradicting_evidence,note,actual_root_cause,reviewer_subject,reviewed_at,schema_version,correct_finding_rank,correct_finding_rank_asked)
                    VALUES ({row.Id},{row.BundleId},{row.OwnerGroup},{(int)row.Verdict},0,{row.Note},{row.ActualRootCause},{row.ReviewerSubject},{row.ReviewedAt},{version},{row.CorrectFindingRank},true)
                    """, ct);
            }
            await db.Database.MigrateAsync(ct);
            foreach (var old in expected)
            {
                var actual = await db.GoldenReviews.AsNoTracking().SingleAsync(r => r.Id == old.Id, ct);
                Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(actual));
                Assert.Null(actual.MissingEvidenceKinds); Assert.False(actual.MissingEvidenceAsked);
            }
            foreach (var answer in new string[]?[] { null, [], ["Metric", "Trace"] })
            {
                var row = new GoldenReviewEntity { BundleId = Guid.NewGuid(), OwnerGroup = "A", ReviewerSubject = "new-reviewer",
                    Verdict = ReviewVerdict.Correct, MissingEvidenceKinds = answer,
                    ReviewedAt = new DateTimeOffset(2026, 9, 2, 0, 0, expected.Count, TimeSpan.Zero) };
                db.GoldenReviews.Add(row); expected.Add(row);
            }
            await db.SaveChangesAsync(ct); await db.Database.MigrateAsync(ct); await db.Database.MigrateAsync(ct);
            var bundles = new EvidenceBundleStore(factory);
            foreach (var row in expected)
                await bundles.SaveAsync(new EvidenceBundle { Id = row.BundleId, GatheredAt = row.ReviewedAt,
                    Window = new RcaWindow { From = row.ReviewedAt.AddMinutes(-1), To = row.ReviewedAt,
                        BaselineFrom = row.ReviewedAt.AddMinutes(-2), BaselineTo = row.ReviewedAt.AddMinutes(-1) },
                    Scope = new(["A"], false), Slices = [], Trust = WindowTrust.Unmeasured }, ct);
            var before = await GoldenReviewRestartProbe.ReadAsync(connection);
            var first = await Child("first"); var second = await Child("second");
            Assert.Equal(before, first); Assert.Equal(first, second);
            using var json = JsonDocument.Parse(first);
            Assert.Equal(5, json.RootElement.GetProperty("records").GetArrayLength());
            Assert.Equal(2, json.RootElement.GetProperty("quality").GetProperty("Measured").GetInt64());
            Assert.Equal(3, json.RootElement.GetProperty("quality").GetProperty("Unanswered").GetInt64());
            TelemetryDbFixture.Evidence("s04-migration-restart", new { before, first, second, freshProcessCount = 2 });

            async Task<string> Child(string name)
            {
                var output = Path.Combine(directory, name + ".json");
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                start.ArgumentList.Add(typeof(GoldenReviewRestartProbe).Assembly.Location);
                start.ArgumentList.Add("--s04-golden-restart"); start.ArgumentList.Add(output);
                start.Environment["BIZIGO_S04_RESTART_PG"] = connection;
                using var child = Process.Start(start)!;
                var watch = Stopwatch.StartNew();
                var stdout = child.StandardOutput.ReadToEndAsync(ct); var stderr = child.StandardError.ReadToEndAsync(ct);
                try
                {
                    await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
                    Assert.True(child.ExitCode == 0, await stderr + await stdout);
                    return await File.ReadAllTextAsync(output, ct);
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
                    TelemetryDbFixture.Evidence("s04-migration-child-" + name,
                        new { child.Id, child.ExitCode, elapsedMs = watch.ElapsedMilliseconds,
                            stdout = await stdout, stderr = await stderr, ownedPidAlive = !child.HasExited });
                }
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None); Directory.Delete(directory, true);
        }
    }
}

// A private mode of the test executable, after CLR initialization. This creates no
// containers and cannot activate during an ordinary test run. It exercises
// shipped store/DTO code in a fresh process, not a copied serialization model.
internal static class GoldenReviewRestartProbe
{
    internal static async Task<int> RunAsync(string output)
    {
        try
        {
            Console.Error.WriteLine("restart-entrypoint-ready");
            var result = await ReadAsync(Environment.GetEnvironmentVariable("BIZIGO_S04_RESTART_PG")
                ?? throw new InvalidOperationException("Missing private fixture connection."));
            await File.WriteAllTextAsync(output, result);
            Console.Error.WriteLine("restart-output-written");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name); Console.Error.WriteLine(ex.StackTrace); return 2; }
    }

    internal static async Task<string> ReadAsync(string connection)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var factory = new ControlPlaneFactory(connection);
        await using var db = factory.CreateDbContext();
        Console.Error.WriteLine("restart-migration-start");
        await db.Database.MigrateAsync(budget.Token);
        Console.Error.WriteLine("restart-migration-complete");
        var store = new GoldenReviewStore(factory); var scope = AccessScope.ForGroups("reopen", ["A"]);
        await using var api = await TelemetryApiHost.StartReviewAsync(factory, budget.Token);
        Console.Error.WriteLine("restart-api-ready");
        var records = new List<GoldenReviewEntity>();
        var exports = new List<string>(); var reads = new List<JsonElement>();
        for (var offset = 0; ; offset += 2)
        {
            var page = await db.GoldenReviews.AsNoTracking().OrderBy(r => r.ReviewedAt).ThenBy(r => r.Id)
                .Skip(offset).Take(2).ToArrayAsync(budget.Token);
            if (page.Length == 0) break;
            foreach (var row in page)
            {
                records.Add(Assert.Single(await store.ForBundleAsync(row.BundleId, scope, budget.Token)));
                using var read = await api.GetAsync("/v1/rca/" + row.BundleId);
                Assert.True(read.IsSuccessStatusCode);
                using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync(budget.Token));
                reads.Add(document.RootElement.GetProperty("review").Clone());
                using var export = await api.GetAsync($"/v1/rca/{row.BundleId}/export");
                Assert.True(export.IsSuccessStatusCode);
                exports.Add(await export.Content.ReadAsStringAsync(budget.Token));
            }
        }
        return JsonSerializer.Serialize(new { records, export = exports, reads,
            quality = await store.MissingEvidenceQualityAsync(scope, budget.Token) });
    }
}
