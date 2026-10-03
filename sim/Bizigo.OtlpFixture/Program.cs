using System.Security.Claims;
using System.Text.Encodings.Web;
using Bizigo.Api;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Bizigo.Ingest.Wal;
using Bizigo.Storage.Raw;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Explicit test executable. No fault gates or filesystem object adapters are
// registered by the production API. All addresses bind only to loopback.
if (args.Length == 2 && args[0] == "--topology-crash")
{
    await TopologyCrashHost.RunAsync(args[1]);
    return;
}
if (args.Length == 2 && args[0] == "--evidence-summary")
{
    var bundle = Bizigo.Evidence.BundleSerializer.Deserialize(await File.ReadAllTextAsync(args[1]));
    var step = new Bizigo.ScenarioPlugin.ScenarioStep { Id = "live-summary", Task = "Inspect persisted evidence",
        Input = ["evidence.summary"], Output = new Bizigo.ScenarioPlugin.ScenarioOutput { Schema = "rca.v1" } };
    var view = Bizigo.Rca.Reasoning.StepEvidenceView.For(step, bundle,
        new Dictionary<string, Bizigo.Rca.Reasoning.ScenarioOutputDocument>());
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(view.Summaries));
    return;
}
if (args.Length >= 3 && args[0] == "--legacy-replay")
{
    await LegacyReplayHost.RunAsync(args[1..]);
    return;
}
if (args.Length == 2 && args[0] == "--telemetry-query")
{
    await TelemetryQueryHost.RunAsync(args[1]);
    return;
}
if (args.Length == 3 && args[0] == "--read-archive")
{
    var bytes = await File.ReadAllBytesAsync(args[1]);
    var length = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(RawObjectBuilder.ExtractLine(bytes, 0, length).Span));
    return;
}
var root = Path.GetFullPath(args[0]);
var port = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var gate = args.Length > 2 ? args[2] : "none";
Directory.CreateDirectory(root);
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
builder.Services.AddDbContextFactory<ControlPlaneDbContext>(o => o.UseInMemoryDatabase("fixture"));
builder.Services.AddSingleton<SourceDirectory>();
builder.Services.AddSingleton<IRawObjectStore>(new FileObjects(Path.Combine(root, "objects")));
builder.Services.Configure<SignalOptions>(o => { o.Directory = root; o.ChannelCapacity = 8; });
builder.Services.Configure<WalOptions>(o => { o.Directory = root; o.MaxTotalBytes = 10000000; o.FlushToDisk = false; });
builder.Services.Configure<RawStoreOptions>(o => o.SegmentRetention = TimeSpan.FromDays(1));
builder.Services.Configure<IngestOptions>(o => o.MaxRequestBytes = 1024 * 1024);
builder.Services.AddSingleton<OtlpTelemetryDecoder>();
builder.Services.AddSingleton<ISignalCheckpoints>(new Gates(root, gate));
builder.Services.AddSingleton<IWalDurability>(new Flush(root, gate));
builder.Services.AddSingleton<SignalIngest>();
builder.Services.AddHostedService<SignalIngestService>();
builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuth>("fixture", _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy(BizigoAuthPolicies.Ingest, p => p.RequireAuthenticatedUser().RequireRole("ingest")));
var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>().CreateDbContextAsync())
{
    db.Sources.AddRange(new SourceEntity { SourceId = "known-a", OwnerGroup = "team-a" },
        new SourceEntity { SourceId = "known-b", OwnerGroup = "team-b" });
    await db.SaveChangesAsync();
}
app.UseAuthentication(); app.UseAuthorization(); app.MapOtlpTelemetry();
app.MapPost("/fixture/replay", async (SignalIngest ingest, CancellationToken token) =>
{
    await ingest.ReplayArchiveAsync(token);
    return Results.Ok(new { status = "Completed", envelopes = ingest.Archive.Manifests().Count() });
}).RequireAuthorization(BizigoAuthPolicies.Ingest);
await app.RunAsync();

sealed class FixtureAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
        Request.Headers.Authorization == "Bearer fixture-token"
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "ingest")], "fixture")), Scheme.Name))
            : AuthenticateResult.NoResult());
}

sealed class Gates(string root, string stage) : ISignalCheckpoints
{
    public Task ReachAsync(string point, CancellationToken token) => point == stage ? Wait(root, point, token) : Task.CompletedTask;
    public static async Task Wait(string root, string point, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(root, point + ".entered"), "entered", token);
        while (!File.Exists(Path.Combine(root, point + ".release"))) await Task.Delay(20, token);
    }
}
sealed class Flush(string root, string stage) : IWalDurability
{
    public async ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (stage == "fsync") await Gates.Wait(root, stage, cancellationToken);
        if (stage == "fsync-fail") throw new IOException("fixture fsync failure");
        stream.Flush(true);
    }
}
sealed class FileObjects(string root) : IRawObjectStore
{
    public Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        DurableFile.WriteAsync(Path.Combine(root, key), content, cancellationToken);
    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        File.Exists(Path.Combine(root, key)) ? await File.ReadAllBytesAsync(Path.Combine(root, key), cancellationToken) : null;
    public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Path.Combine(root, key)) ? new RawObjectInfo(key, new FileInfo(Path.Combine(root, key)).Length) : null);
}
