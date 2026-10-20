using System.Net;
using LogAnalyzer.Lite.Configuration;
using Microsoft.Extensions.Logging;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>CORS ve çerçeveleme.</b> Eski hâli <c>AllowAnyOrigin</c> ve
/// <c>frame-ancestors *</c> idi: her site API yanıtlarını (olay yükleri,
/// yığın izleri) okuyabiliyor ve arayüzü çerçeveleyip tıklatabiliyordu.
/// </summary>
public sealed class BrowserPolicyTests
{
    private static HttpRequestMessage Get(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-requested-with");
        return request;
    }

    [Fact]
    public async Task Varsayilan_hicbir_capraz_kokene_cors_basligi_yok()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var get = await client.SendAsync(Get("/api/v1/incidents", "https://evil.example.test"), ct);
        using var preflight = await client.SendAsync(Preflight("/api/v1/logs/purge", "https://evil.example.test"), ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.False(get.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Izinli_koken_yansitiliyor_digerleri_ve_joker_yok()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.AllowedOriginsKey] = "https://ops.example.test, https://second.example.test:8443, *, https://x.example.test/path",
        });
        var client = app.CreateClient();

        using var allowed = await client.SendAsync(Get("/api/v1/incidents", "https://ops.example.test"), ct);
        using var allowedPort = await client.SendAsync(Get("/api/v1/incidents", "https://second.example.test:8443"), ct);
        using var foreign = await client.SendAsync(Get("/api/v1/incidents", "https://evil.example.test"), ct);
        using var preflight = await client.SendAsync(Preflight("/api/v1/alerts/config", "https://ops.example.test"), ct);

        Assert.Equal("https://ops.example.test", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("https://second.example.test:8443", Assert.Single(allowedPort.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"));

        var allowHeaders = string.Join(',', preflight.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-requested-with", allowHeaders, StringComparison.OrdinalIgnoreCase);

        // `*` ve yollu girdi yok sayıldı — ve bu sessiz değil.
        Assert.Contains(app.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("'*'", StringComparison.Ordinal));
        Assert.Contains(app.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("https://x.example.test/path", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/api/v1/stats")]
    [InlineData("/health")]
    public async Task Varsayilan_yalnizca_ayni_koken_cerceveleyebilir(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("frame-ancestors 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("SAMEORIGIN", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Ek_cerceve_kaynaklari_eklenir_joker_ve_sema_reddedilir()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.FrameAncestorsKey] = "https://gateway.example.test * https: https://*.ops.example.test 'unsafe-inline';",
        });

        using var response = await app.CreateClient().GetAsync("/", ct);
        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Equal("frame-ancestors 'self' https://gateway.example.test https://*.ops.example.test", csp);
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }
}
