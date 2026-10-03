using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Defensive storage-boundary fixture, not a public-write acceptance claim.
/// Each case owns a separate PG database: only that private history column is
/// widened to create exact ASCII wire sizes beyond the normal 256-char limit.
/// All reads use real JWT, IScopedQuery, publication fence, PG/CH and audit.
/// </summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TopologyHttpBudgetIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string HiddenMarker = "H04-FOREIGN-B-STORAGE-MARKER";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_HTTP_exact_byte_limit_and_one_over_never_return_truncated_record(bool detail)
    {
        await WithPrivateDatabase(async (factory, api) =>
        {
            var (visible, hidden, asOf) = await Seed(factory, 1);
            var path = "/v1/topology/nodes" + (detail ? "/" + visible[0] : "") + "?asOf=" + asOf;
            using var baseline = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);
            var original = await baseline.Content.ReadAsByteArrayAsync(Ct);
            const string initialName = "visible-0";
            var overhead = original.Length - Encoding.UTF8.GetByteCount(initialName);
            var name = new string('x', TopologyResponseBudget.MaximumBytes - overhead);
            await SetName(factory, visible[0], name);
            await SetName(factory, hidden, HiddenMarker + new string('z', 2 * TopologyResponseBudget.MaximumBytes));

            using var exact = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
            var exactBytes = await exact.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(TopologyResponseBudget.MaximumBytes, exactBytes.Length);
            CheckHidden(exactBytes, hidden);
            using (var body = JsonDocument.Parse(exactBytes))
            {
                var row = detail ? body.RootElement.GetProperty("node")
                    : Assert.Single(body.RootElement.GetProperty("nodes").EnumerateArray());
                Assert.Equal(name, row.GetProperty("display_name").GetString());
                Assert.Equal(visible[0], row.GetProperty("id").GetString());
            }

            await SetName(factory, visible[0], name + "x");
            using var over = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
            var errorBytes = await over.Content.ReadAsByteArrayAsync(Ct);
            CheckHidden(errorBytes, hidden);
            using (var error = JsonDocument.Parse(errorBytes))
                Assert.Equal("RecordTooLarge", error.RootElement.GetProperty("reason").GetString());
            Assert.DoesNotContain("\"nodes\"", Encoding.UTF8.GetString(errorBytes), StringComparison.Ordinal);
            Assert.DoesNotContain("\"node\"", Encoding.UTF8.GetString(errorBytes), StringComparison.Ordinal);

            await SetName(factory, visible[0], initialName);
            using var restored = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            TelemetryDbFixture.Evidence("h04-exact-over-" + detail, new
            {
                detail, exactBytes = exactBytes.Length, oneOverCalculatedBytes = overhead + name.Length + 1,
                sha256 = Convert.ToHexString(SHA256.HashData(exactBytes)).ToLowerInvariant(),
                overStatus = (int)over.StatusCode, restoredStatus = (int)restored.StatusCode,
                scope = "private PG database defensive history row; public write max unchanged",
            });
        });
    }

    [Fact]
    public async Task Real_HTTP_aggregate_overflow_continues_with_cursor_and_excludes_hidden_bytes()
    {
        await WithPrivateDatabase(async (factory, api) =>
        {
            var (visible, hidden, asOf) = await Seed(factory, 2);
            foreach (var id in visible) await SetName(factory, id, new string('x', 600_000));
            await SetName(factory, hidden, HiddenMarker + new string('z', 2 * TopologyResponseBudget.MaximumBytes));
            var path = "/v1/topology/nodes?limit=2&asOf=" + asOf;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var sizes = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                using var response = await api.GetAsync(path + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); // aggregate overflow is not a 422
                var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
                Assert.InRange(bytes.Length, 600_000, TopologyResponseBudget.MaximumBytes);
                CheckHidden(bytes, hidden); sizes.Add(bytes.Length);
                using var body = JsonDocument.Parse(bytes);
                var row = Assert.Single(body.RootElement.GetProperty("nodes").EnumerateArray());
                Assert.True(seen.Add(row.GetProperty("id").GetString()!));
                Assert.Equal(600_000, row.GetProperty("display_name").GetString()!.Length);
                cursor = body.RootElement.GetProperty("cursor").GetString();
                Assert.Equal(i == 0, body.RootElement.GetProperty("partial").GetBoolean());
                if (i == 0) Assert.False(string.IsNullOrEmpty(cursor)); else Assert.Null(cursor);
            }
            Assert.Equal(visible.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
            TelemetryDbFixture.Evidence("h04-aggregate-cursor", new { sizes, ids = seen, cursor, hiddenAbsent = true });
        });
    }

    private async Task WithPrivateDatabase(Func<ControlPlaneFactory, TopologyHttpOracleHost, Task> body)
    {
        var database = "s05_h04_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(stack.PostgresConnectionString)
            { Database = database, Pooling = false }.ConnectionString;
        await using var admin = new NpgsqlConnection(stack.PostgresConnectionString);
        await admin.OpenAsync(Ct);
        await using (var create = new NpgsqlCommand("CREATE DATABASE " + database, admin))
            await create.ExecuteNonQueryAsync(Ct);
        try
        {
            var factory = new ControlPlaneFactory(connection);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.MigrateAsync(Ct);
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE bizigo.topology_node_history ALTER COLUMN display_name TYPE text", Ct);
            }
            using var storage = await DevStackSetup.ClickHouseAsync(stack, Ct);
            await using var api = await TopologyHttpOracleHost.StartAsync(factory, storage.Options, Ct);
            await body(factory, api);
        }
        finally
        {
            // Drop the entire owned database after the host is disposed. No
            // narrowing conversion over oversized rows, no shared schema edit.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var drop = new NpgsqlCommand("DROP DATABASE " + database + " WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(cleanup.Token);
            TelemetryDbFixture.Evidence("h04-cleanup-" + database, new { database, dropped = true });
        }
    }

    private static async Task<(string[] Visible, string Hidden, string AsOf)> Seed(ControlPlaneFactory factory, int count)
    {
        var registry = new TopologyRegistry(factory);
        var scope = AccessScope.ForGroups("h04-seed", ["A", "B"]);
        var visible = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var node = await registry.CreateAsync(scope, true,
                new(TopologyNodeKind.Service, "visible-" + i, "A", true, []), Ct);
            Assert.Equal(201, node.Status); visible.Add(node.Node!.Id);
        }
        var hidden = await registry.CreateAsync(scope, true,
            new(TopologyNodeKind.Service, HiddenMarker, "B", true, []), Ct);
        Assert.Equal(201, hidden.Status);
        return (visible.ToArray(), hidden.Node!.Id,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
    }

    private static async Task SetName(ControlPlaneFactory factory, string id, string name)
    {
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE bizigo.topology_node_history SET display_name={name} WHERE node_id={id}", Ct));
    }

    private static void CheckHidden(byte[] bytes, string hidden)
    {
        var body = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(HiddenMarker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(hidden, body, StringComparison.Ordinal);
    }
}
