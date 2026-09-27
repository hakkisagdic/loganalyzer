using System.Net;
using System.Text.Json;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: running these cases proves the eleven real HTTP read
/// routes use production JWT authorization, PG ownership mapping/audit, and CH reads.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TelemetryApiIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Trace = "00112233445566778899aabbccddeeff";
    private static string Window(TelemetryDbFixture f) => $"?from_nano={f.Now - 10000000000UL}&to_nano={f.Now + 10000000000UL}";

    [Fact]
    public async Task Eleven_read_routes_authenticate_scope_and_preserve_typed_values()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("api-A", "A"); await f.SourceAsync("api-B", "B");
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        foreach (var source in new[] { "api-A", "api-B", "api-unknown" })
        {
            await f.EmitAsync(ingest, TelemetryDbFixture.Metrics(source, f.Now), TelemetrySignal.Metrics);
            await f.EmitAsync(ingest, TelemetryDbFixture.Traces(source, f.Now, 2), TelemetrySignal.Traces);
        }
        var rows = (await f.Query.SearchTelemetryAsync(f.Window(TelemetrySignal.Metrics), AccessScope.System("seed"), Ct)).Records;
        var visible = rows.First(r => r.Owner.OwnerGroup == "A" && r.Kind == "Gauge");
        var hidden = rows.First(r => r.Owner.OwnerGroup == "B");
        await using var api = await TelemetryApiHost.StartAsync(f, Ct);
        var paths = new[] { "/v1/metrics", "/v1/metrics/points/" + Uri.EscapeDataString(visible.LogicalId),
            "/v1/metrics/count", "/v1/metrics/summary", "/v1/metrics/feed", "/v1/traces", "/v1/traces/" + Trace,
            "/v1/traces/" + Trace + "/spans/0000000000000001", "/v1/traces/count", "/v1/traces/summary", "/v1/traces/feed" };
        foreach (var path in paths)
        {
            using var anonymous = await api.GetAsync(path + Window(f), null);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var ingestRole = await api.GetAsync(path + Window(f), "A", "ingest");
            Assert.Equal(HttpStatusCode.Forbidden, ingestRole.StatusCode);
            using var wrongAudience = await api.GetAsync(path + Window(f), "A", audience: "unrelated-api");
            Assert.Equal(HttpStatusCode.Unauthorized, wrongAudience.StatusCode);
            using var allowed = await api.GetAsync(path + Window(f));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        var point = await Json(api, paths[1] + Window(f));
        Assert.Equal("9007199254740993", point.GetProperty("metric").GetProperty("data_points")[0].GetProperty("value").GetProperty("value").GetProperty("value").GetString());
        Assert.Equal("A", point.GetProperty("owner_group").GetString());
        var span = (await Json(api, paths[7] + Window(f))).GetProperty("span");
        Assert.Equal("9999999999999999", span.GetProperty("parent_span_id").GetString());
        Assert.Equal("7", span.GetProperty("events")[0].GetProperty("dropped_attributes_count").GetString());
        Assert.Equal("8", span.GetProperty("links")[0].GetProperty("dropped_attributes_count").GetString());
        using var invisible = await api.GetAsync("/v1/metrics/points/" + Uri.EscapeDataString(hidden.LogicalId) + Window(f));
        using var absent = await api.GetAsync("/v1/metrics/points/missing" + Window(f));
        Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode); Assert.Equal(absent.StatusCode, invisible.StatusCode);
        Assert.Equal(await absent.Content.ReadAsStringAsync(Ct), await invisible.Content.ReadAsStringAsync(Ct));
        var narrowed = await Json(api, "/v1/metrics" + Window(f) + "&owner_groups=B");
        Assert.Empty(narrowed.GetProperty("records").EnumerateArray());
        Assert.Equal("5", (await Json(api, "/v1/metrics/count" + Window(f))).GetProperty("count").GetString());
        Assert.Equal("15", (await Json(api, "/v1/metrics/count" + Window(f), "admin", "admin")).GetProperty("count").GetString());
        Assert.Equal("5", (await Json(api, "/v1/metrics/count" + Window(f), "_unassigned")).GetProperty("count").GetString());
        var audit = await f.Db.AuditLog.AsNoTracking().Where(r => r.Subject == api.Subject("A")).ToArrayAsync(Ct);
        Assert.True(audit.Length >= 15);
        Assert.All(audit, row => { Assert.True(row.DurationMs >= 0); Assert.DoesNotContain("secret-for-api-B", row.Details, StringComparison.Ordinal); });
        TelemetryDbFixture.Evidence("s04-http-auth-wire", new { routeCount = paths.Length, point, span, audit });
    }

    [Fact]
    // kapsam: CreateTelemetryCursor
    public async Task Stable_pagination_rejects_cross_scope_cursor_and_invalid_query_before_audit()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("pages", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("pages", f.Now), TelemetrySignal.Metrics);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct);
        var path = "/v1/metrics" + Window(f) + "&limit=2";
        var first = await Json(api, path); var cursor = first.GetProperty("cursor").GetString()!;
        Assert.True(first.GetProperty("partial").GetBoolean()); Assert.NotEmpty(cursor);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var page = first;
        while (true)
        {
            foreach (var row in page.GetProperty("records").EnumerateArray()) Assert.True(ids.Add(row.GetProperty("logical_id").GetString()!));
            if (page.GetProperty("cursor").ValueKind == JsonValueKind.Null) break;
            page = await Json(api, path + "&cursor=" + Uri.EscapeDataString(page.GetProperty("cursor").GetString()!));
            Assert.True(ids.Count <= 5, "Continuation must terminate without repeating records.");
        }
        Assert.Equal(5, ids.Count);
        var before = await f.Db.AuditLog.CountAsync(r => r.Subject == api.Subject("A"), Ct);
        foreach (var invalid in new[] { "?from_nano=1&to_nano=2&limit=1001", "?from_nano=1e0&to_nano=2", "?from_nano=1&to_nano=2&sql=x",
            Window(f) + "&limit=3&cursor=" + Uri.EscapeDataString(cursor) })
        {
            using var response = await api.GetAsync("/v1/metrics" + invalid); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var crossScope = await api.GetAsync(path + "&cursor=" + Uri.EscapeDataString(cursor), "B");
        Assert.Equal(HttpStatusCode.BadRequest, crossScope.StatusCode);
        Assert.Equal(before, await f.Db.AuditLog.CountAsync(r => r.Subject == api.Subject("A"), Ct));
    }

    [Fact]
    public async Task Actual_database_and_audit_failures_are_503_not_empty_success()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("failure", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("failure", f.Now, false), TelemetrySignal.Metrics);
        await using var api = await TelemetryApiHost.StartAsync(f, Ct);
        await f.SqlAsync("RENAME TABLE metric_points TO hidden_metric_points");
        try
        {
            using var response = await api.GetAsync("/v1/metrics" + Window(f));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("Failed", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("count").ValueKind);
        }
        finally { await f.SqlAsync("RENAME TABLE hidden_metric_points TO metric_points"); }
        await f.Db.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION bizigo.s04_audit_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture audit unavailable'; END $$;
            CREATE TRIGGER s04_audit_fault BEFORE INSERT ON bizigo.audit_log FOR EACH ROW EXECUTE FUNCTION bizigo.s04_audit_fault();
            """, Ct);
        try
        {
            using var response = await api.GetAsync("/v1/metrics/count" + Window(f));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { await f.Db.Database.ExecuteSqlRawAsync("DROP TRIGGER s04_audit_fault ON bizigo.audit_log; DROP FUNCTION bizigo.s04_audit_fault();", Ct); }
        Assert.Equal("1", (await Json(api, "/v1/metrics/count" + Window(f))).GetProperty("count").GetString());
    }

    internal static async Task<JsonElement> Json(TelemetryApiHost api, string path, string owner = "A", string role = "reader")
    {
        using var response = await api.GetAsync(path, owner, role);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
    }
}
