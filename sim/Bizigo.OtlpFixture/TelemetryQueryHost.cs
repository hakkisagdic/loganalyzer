using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

/// <summary>Test executable adapter only. Queries and audit go through the same
/// production composition/IScopedQuery as consumers; no parallel query implementation.</summary>
internal static class TelemetryQueryHost
{
    internal sealed record Configuration(int Port, string Root, string Gate, Dictionary<string, string> Tokens);

    public static async Task RunAsync(string path)
    {
        var config = JsonSerializer.Deserialize<Configuration>(await File.ReadAllBytesAsync(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing query fixture configuration.");
        if (config.Tokens.Count != 4 || !new[] { "A", "B", "_unassigned", "admin" }.All(config.Tokens.ContainsValue))
            throw new InvalidDataException("Fixture requires four distinct server-owned scopes.");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:" + config.Port);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        builder.Services.AddControlPlane(builder.Configuration.GetConnectionString("ControlPlane")!);
        builder.Services.AddBizigoDataPlane(new ClickHouseOptions { ConnectionString = builder.Configuration.GetConnectionString("ClickHouse")!, MigrationsDirectory = "db/clickhouse" });
        builder.Services.Configure<RawStoreOptions>(builder.Configuration.GetSection(RawStoreOptions.SectionName));
        builder.Services.Configure<RawStoreOptions>(o => o.SegmentRetention = TimeSpan.Zero);
        builder.Services.AddSingleton<IRawObjectStore, S3RawObjectStore>();
        builder.Services.AddSingleton<OtlpTelemetryDecoder>();
        builder.Services.Configure<SignalOptions>(o => { o.Directory = config.Root; o.RetryInterval = TimeSpan.FromMilliseconds(100); });
        builder.Services.Configure<WalOptions>(o => { o.Directory = config.Root; o.MaxSegmentBytes = 1024; });
        builder.Services.Configure<IngestOptions>(o => o.MaxRequestBytes = 10 * 1024 * 1024);
        builder.Services.AddSingleton<ISignalCheckpoints>(new Gates(config.Root, config.Gate));
        builder.Services.AddSingleton<SignalIngest>(sp => ActivatorUtilities.CreateInstance<SignalIngest>(sp, sp.GetRequiredService<ITelemetrySink>()));
        builder.Services.AddHostedService<SignalIngestService>();
        builder.Services.AddSingleton(config);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, FixtureUser>();
        builder.Services.AddAuthentication("telemetry-fixture").AddScheme<AuthenticationSchemeOptions, Auth>("telemetry-fixture", _ => { });
        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy(BizigoAuthPolicies.Read, p => p.RequireAuthenticatedUser());
            o.AddPolicy(BizigoAuthPolicies.Admin, p => p.RequireRole("admin"));
            o.AddPolicy(BizigoAuthPolicies.Ingest, p => p.RequireRole("admin"));
        });
        var app = builder.Build();
        await app.Services.MigrateControlPlaneAsync(); await app.Services.MigrateDataPlaneAsync();
        app.UseAuthentication(); app.UseAuthorization(); app.MapSources(); app.MapOtlpTelemetry();
        app.MapGet("/fixture/ready", (SignalIngest ingest) => Results.Json(new { ready = ingest.Ready })).RequireAuthorization(BizigoAuthPolicies.Read);
        app.MapPost("/fixture/query/{operation}", async (string operation, HttpContext http, IScopedQuery query, ICurrentUser user, CancellationToken token) =>
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<TelemetryQuery>(http.Request.Body, RawSignalCodec.Json, token)
                    ?? throw new ArgumentException("Missing bounded query.");
                object result = operation switch
                {
                    "list" => await query.SearchTelemetryAsync(request, user.Scope, token),
                    "trace" => await query.GetTraceAsync(request, user.Scope, token),
                    "count" => await query.CountTelemetryAsync(request, user.Scope, token),
                    "outside-count" => await query.CountOutOfScopeTelemetryAsync(request, user.Scope, token),
                    "summary" => await query.SummarizeTelemetryAsync(request, user.Scope, token),
                    "feed" => await query.GetTelemetryFeedAsync(request.Signal, request.ResourceId, user.Scope, token),
                    _ => throw new ArgumentException("Unsupported fixture operation."),
                };
                return Results.Json(result, RawSignalCodec.Json);
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException) { return Results.BadRequest(new { error = ex.GetType().Name }); }
        }).RequireAuthorization(BizigoAuthPolicies.Read);
        app.MapGet("/fixture/metric", async (string id, IScopedQuery query, ICurrentUser user, CancellationToken token) =>
            Results.Json(await query.GetMetricPointAsync(id, user.Scope, token), RawSignalCodec.Json)).RequireAuthorization(BizigoAuthPolicies.Read);
        app.MapGet("/fixture/audit", async (string subject, ControlPlaneDbContext db, CancellationToken token) =>
            Results.Json(await db.AuditLog.AsNoTracking().Where(a => a.Subject == subject).OrderBy(a => a.Id).ToArrayAsync(token), RawSignalCodec.Json)).RequireAuthorization(BizigoAuthPolicies.Admin);
        app.MapGet("/fixture/history", async (string source, ControlPlaneDbContext db, CancellationToken token) =>
            Results.Json(await db.SourceOwnershipHistory.AsNoTracking().Where(h => h.SourceId == source).OrderBy(h => h.EffectiveFromNano).ToArrayAsync(token), RawSignalCodec.Json)).RequireAuthorization(BizigoAuthPolicies.Admin);
        app.MapPost("/fixture/replay", async (SignalIngest ingest, CancellationToken token) =>
        { await ingest.ReplayArchiveAsync(token); return Results.Ok(new { envelopes = ingest.Archive.Manifests().Count() }); }).RequireAuthorization(BizigoAuthPolicies.Admin);
        app.MapPost("/fixture/sweep", async (SignalIngest ingest, CancellationToken token) =>
        { await ingest.SweepAsync(token); return Results.Ok(new { segments = ingest.Wal.ListSealedSegments().Count }); }).RequireAuthorization(BizigoAuthPolicies.Admin);
        await app.RunAsync();
    }

    private sealed class FixtureUser(IHttpContextAccessor accessor) : ICurrentUser
    {
        public ClaimsPrincipal Principal => accessor.HttpContext!.User;
        public string Subject => Principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        public AccessScope Scope => Principal.IsInRole("admin") ? AccessScope.System(Subject)
            : AccessScope.ForGroups(Subject, Principal.FindAll("owner_group").Select(c => c.Value));
        public bool IsAdmin => Principal.IsInRole("admin");
    }

    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, Configuration config) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !config.Tokens.TryGetValue(header[7..], out var owner))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fixture-" + owner) };
            claims.Add(owner == "admin" ? new(ClaimTypes.Role, "admin") : new("owner_group", owner));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
