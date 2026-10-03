using Microsoft.EntityFrameworkCore;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;

namespace Bizigo.IntegrationTests;

/// <summary>Production endpoint/auth/scope/query registrations with real PG/CH.
/// Only the issuer's verification key is local. Query, publication fence and audit are production registrations; live Keycloak is a separate gate.</summary>
internal sealed class TopologyHttpOracleHost : IAsyncDisposable
{
    private const string Issuer = "https://telemetry-fixture.invalid/issuer";
    private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(64));
    private readonly string prefix = "telemetry-http-" + Guid.NewGuid().ToString("N");
    private CancellationToken requestToken;
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public string Subject(string owner) => prefix + "-" + owner;

    public string ApplicationName => prefix;
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task<TopologyHttpOracleHost> StartAsync(TelemetryDbFixture f, CancellationToken token) =>
        StartAsync(f.Factory, f.Storage.Options, token);

    public static async Task<TopologyHttpOracleHost> StartAsync(
        IDbContextFactory<ControlPlaneDbContext> factory, ClickHouseOptions storage, CancellationToken token)
    {
        var host = new TopologyHttpOracleHost { requestToken = token };
        await using var mappingDb = await factory.CreateDbContextAsync(token);
        foreach (var owner in new[] { "A", "B", "_unassigned" })
            mappingDb.IdpGroupMappings.Add(new IdpGroupMappingEntity { IdpGroup = host.Subject(owner), OwnerGroup = owner });
        await mappingDb.SaveChangesAsync(token);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Enabled"] = "true", ["Auth:Authority"] = Issuer, ["Auth:Audience"] = "bizigo-api",
            ["Auth:McpResource"] = "https://telemetry-fixture.invalid/mcp", ["Auth:RequireHttpsMetadata"] = "false",
        });
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(mappingDb.Database.GetConnectionString()!)
        { ApplicationName = host.ApplicationName };
        builder.Services.AddControlPlane(connection.ConnectionString);
        builder.Services.AddBizigoDataPlane(storage);
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton<TopologyReadCursorCodec>();
        builder.Services.AddOpenApi();
        builder.Services.AddBizigoAuthentication(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer, SigningKeys = { host.key } };
            // JwtBearerPostConfigureOptions has already created a metadata manager
            // from Authority. Replace that manager, not just its unused fallback.
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
        });
        host.App = builder.Build();
        host.App.Use(async (context, next) =>
        {
            using var observation = context.RequestAborted.Register(() => host.CancellationObserved.TrySetResult());
            await next(context);
        });
        host.App.UseAuthentication(); host.App.UseAuthorization();
        host.App.MapTopologyReads(); host.App.MapTopologyWrites(); host.App.MapOpenApi();
        try
        {
            await host.App.StartAsync(token);
            await host.App.Services.GetRequiredService<AccessScopeResolver>().RefreshAsync(token);
            host.Http = new HttpClient { BaseAddress = new(host.App.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            return host;
        }
        catch { await host.App.DisposeAsync(); throw; }
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? owner = "A", string role = "reader", string audience = "bizigo-api") =>
        SendAsync(HttpMethod.Get, path, null, owner, role, audience);

    public Task<HttpResponseMessage> GetCancellableAsync(string path, CancellationToken token) =>
        SendAsync(HttpMethod.Get, path, null, "A", "reader", "bizigo-api", token);

    public Task<HttpResponseMessage> PostAsync(string path, string body, string? owner = "A", string role = "reader") =>
        SendAsync(HttpMethod.Post, path, body, owner, role, "bizigo-api");

    public Task<HttpResponseMessage> PutAsync(string path, string body, string? owner = "A", string role = "reader") =>
        SendAsync(HttpMethod.Put, path, body, owner, role, "bizigo-api");

    public Task<HttpResponseMessage> DeleteAsync(string path, string? owner = "A", string role = "reader") =>
        SendAsync(HttpMethod.Delete, path, null, owner, role, "bizigo-api");

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string? owner, string role, string audience, CancellationToken? token = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        if (owner is not null)
        {
            var jwt = new JwtSecurityToken(Issuer, audience,
                [new Claim("sub", Subject(owner)), new Claim("roles", role), new Claim("groups", "/" + Subject(owner))],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        return await Http.SendAsync(request, token ?? requestToken);
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (App is not null) { await App.StopAsync(CancellationToken.None); await App.DisposeAsync(); }
    }
}
