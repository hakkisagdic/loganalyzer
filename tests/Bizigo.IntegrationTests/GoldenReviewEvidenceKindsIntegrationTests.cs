using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: both production capture routes persist nullable answers
/// into actual PostgreSQL, then reopen/read/export/measure the same answers.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GoldenReviewEvidenceKindsIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] public Task Review_capture_unanswered_is_not_measured() => Capture(false, false);
    [Fact] public Task Close_capture_unanswered_is_not_measured() => Capture(true, false);
    [Fact] public Task Review_capture_nonempty_persists() => Capture(false, true);
    [Fact] public Task Close_capture_nonempty_persists() => Capture(true, true);

    private async Task Capture(bool close, bool answered)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        foreach (var field in answered ? new[] { ",\"missing_evidence_kinds\":[]", ",\"missing_evidence_kinds\":[\"Trace\",\"Metric\"]" }
            : new[] { "", ",\"missing_evidence_kinds\":null" })
        {
            var seed = await Seed(f, close);
            using var response = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\"" + field + "}");
            Assert.Equal(close ? HttpStatusCode.OK : HttpStatusCode.Created, response.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal(3, body.GetProperty("schema_version").GetInt32());
            Assert.Equal(answered, body.GetProperty("missing_evidence_asked").GetBoolean());
            await using var reopened = await f.Factory.CreateDbContextAsync(Ct);
            var row = await reopened.GoldenReviews.SingleAsync(r => r.BundleId == seed.BundleId, Ct);
            Assert.Equal(3, row.SchemaVersion); Assert.Equal(answered, row.MissingEvidenceAsked);
            if (!answered) Assert.Null(row.MissingEvidenceKinds);
            else Assert.Equal(field.Contains("Trace", StringComparison.Ordinal) ? ["Metric", "Trace"] : Array.Empty<string>(), row.MissingEvidenceKinds);
            Assert.Equal(api.Subject("A"), row.ReviewerSubject);
            if (close) Assert.Equal(AlertTriggerState.Closed, (await reopened.AlertTriggers.SingleAsync(t => t.Id == seed.TriggerId, Ct)).State);
            var report = await TelemetryApiIntegrationTests.Json(api, "/v1/rca/" + seed.BundleId);
            Assert.Equal(answered, report.GetProperty("review").GetProperty("missing_evidence_asked").GetBoolean());
            using var export = await api.GetAsync($"/v1/rca/{seed.BundleId}/export");
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            var markdown = await export.Content.ReadAsStringAsync(Ct);
            Assert.Contains("missing_evidence_kinds", markdown, StringComparison.Ordinal);
            Assert.Contains(answered ? "\"missing_evidence_asked\":true" : "\"missing_evidence_asked\":false", markdown, StringComparison.Ordinal);
            using var deniedRead = await api.GetAsync("/v1/rca/" + seed.BundleId, "B");
            Assert.Equal(HttpStatusCode.NotFound, deniedRead.StatusCode);
            using var deniedWrite = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\",\"missing_evidence_kinds\":[]}", "B");
            Assert.Equal(close ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound, deniedWrite.StatusCode);
            Assert.Equal(1, await reopened.GoldenReviews.CountAsync(r => r.BundleId == seed.BundleId, Ct));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_capture_routes_reject_invalid_answers_before_any_persistence(bool close)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        var seed = await Seed(f, close);
        foreach (var field in new[] { "\"missing_evidence_kinds\":[\"Metric\",\"Metric\"]", "\"missing_evidence_kinds\":[\"Future\"]",
            "\"missing_evidence_kinds\":[null]", "\"missing_evidence_kinds\":[1]", "\"missing_evidence_kinds\":[\"metric\"]",
            "\"missing_evidence_kinds\":[\"Log\",\"Change\",\"Metric\",\"Trace\",\"Topology\",\"Log\"]", "\"schema_version\":2" })
        {
            using var response = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\"," + field + "}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await using var db = await f.Factory.CreateDbContextAsync(Ct);
            Assert.False(await db.GoldenReviews.AnyAsync(r => r.BundleId == seed.BundleId, Ct));
            if (close) Assert.Equal(AlertTriggerState.Open, (await db.AlertTriggers.SingleAsync(t => t.Id == seed.TriggerId, Ct)).State);
        }
        using var unauthorized = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\"}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var wrongRole = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\"}", "A", "ingest");
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Golden_missing_kind_fixed_denominator_through_both_routes(bool close)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        // This is the disposable test PostgreSQL database; reset only review rows
        // belonging to this fixture's fixed A principal before its six-row oracle.
        await f.Db.GoldenReviews.Where(r => r.OwnerGroup == "A").ExecuteDeleteAsync(Ct);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct, includeEvidence: true);
        for (var version = 1; version <= 2; version++)
        {
            var seed = await Seed(f, close);
            f.Db.GoldenReviews.Add(new() { BundleId = seed.BundleId, SchemaVersion = version, OwnerGroup = "A",
                ReviewerSubject = "historical", Verdict = ReviewVerdict.Correct, Note = "preserve-v" + version });
        }
        await f.Db.SaveChangesAsync(Ct);
        foreach (var field in new[] { "null", "[]", "[\"Metric\"]", "[\"Trace\",\"Metric\"]" })
        {
            var seed = await Seed(f, close);
            using var response = await api.PostAsync(seed.Path, "{\"verdict\":\"correct\",\"missing_evidence_kinds\":" + field + "}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        }
        var quality = await TelemetryApiIntegrationTests.Json(api, "/v1/rca/quality");
        var missing = quality.GetProperty("missing_evidence");
        Assert.Equal(6, missing.GetProperty("total").GetInt64()); Assert.Equal(3, missing.GetProperty("measured").GetInt64());
        Assert.Equal(3, missing.GetProperty("unanswered").GetInt64()); Assert.Equal(2, missing.GetProperty("missing_any").GetInt64());
        Assert.Equal(2, missing.GetProperty("metric").GetInt64()); Assert.Equal(1, missing.GetProperty("trace").GetInt64());
        Assert.Equal(2d / 3, missing.GetProperty("missing_any_ratio").GetDouble());
        Assert.Equal(2d / 3, missing.GetProperty("metric_ratio").GetDouble()); Assert.Equal(1d / 3, missing.GetProperty("trace_ratio").GetDouble());
        Assert.Equal(1d, quality.GetProperty("accuracy").GetDouble());
        var snapshot = await f.Db.GoldenReviews.AsNoTracking().Where(r => r.OwnerGroup == "A").OrderBy(r => r.Id).ToArrayAsync(Ct);
        await f.Db.Database.MigrateAsync(Ct); await f.Db.Database.MigrateAsync(Ct);
        await using var reopened = await f.Factory.CreateDbContextAsync(Ct);
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(await reopened.GoldenReviews.AsNoTracking().Where(r => r.OwnerGroup == "A").OrderBy(r => r.Id).ToArrayAsync(Ct)));
        await reopened.GoldenReviews.Where(r => r.OwnerGroup == "A" && r.MissingEvidenceKinds != null).ExecuteDeleteAsync(Ct);
        var unasked = (await TelemetryApiIntegrationTests.Json(api, "/v1/rca/quality")).GetProperty("missing_evidence");
        Assert.Equal(0, unasked.GetProperty("measured").GetInt64()); Assert.Equal(JsonValueKind.Null, unasked.GetProperty("missing_any_ratio").ValueKind);
        TelemetryDbFixture.Evidence("s04-golden-quality-" + close, new { quality, unasked, snapshot });
    }

    private static async Task<(Guid BundleId, Guid? TriggerId, string Path)> Seed(TelemetryDbFixture f, bool close)
    {
        var now = DateTimeOffset.UtcNow;
        var bundle = new EvidenceBundle { Id = Guid.NewGuid(), GatheredAt = now,
            Window = new RcaWindow { From = now.AddMinutes(-1), To = now, BaselineFrom = now.AddMinutes(-2), BaselineTo = now.AddMinutes(-1) },
            Scope = new(["A"], false), Slices = [], Trust = WindowTrust.Unmeasured };
        await new EvidenceBundleStore(f.Factory).SaveAsync(bundle, Ct);
        if (!close) return (bundle.Id, null, $"/v1/rca/{bundle.Id}/review");
        var trigger = new AlertTriggerEntity { RuleId = Guid.NewGuid(), OwnerGroup = "A", SourceId = "golden-source",
            FiredAt = now, WindowFrom = bundle.Window.From, WindowTo = bundle.Window.To, Summary = "fixture" };
        f.Db.AlertTriggers.Add(trigger); await f.Db.SaveChangesAsync(Ct);
        return (bundle.Id, trigger.Id, $"/v1/alerts/triggers/{trigger.Id}/close");
    }
}
