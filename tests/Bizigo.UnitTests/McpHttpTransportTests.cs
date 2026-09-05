using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Bizigo.Api;
using Bizigo.Mcp;
using Bizigo.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>İkinci taşıma:</b> akışlanabilir HTTP.
///
/// <para>
/// Bitti tanımının 1. maddesi <i>"<c>initialize</c> el sıkışması iki taşımada
/// da çalışıyor"</i> diyor. Birincisi (akış/stdio) <c>McpComplianceTests</c>
/// içinde ölçülüyor; bu dosya ikincisini ölçüyor — <b>gerçek bir HTTP
/// isteğiyle</b>, üretimin kaydettiği uç üzerinden.
/// </para>
///
/// <para>
/// <b>Konteyner yok, soket yok:</b> <c>TestServer</c> süreç içinde koşuyor.
/// Yani bu test <c>Bizigo.UnitTests</c>'te yaşıyor ve ajan da koordinatör de
/// koşturabiliyor (§2).
/// </para>
///
/// <para>
/// <b>Ölçülen şey üretimin kendi kaydı:</b> <c>AddBizigoMcp</c> ve
/// <c>MapBizigoMcp</c> çağrılıyor, ikinci bir kurulum yazılmıyor. Kimlik
/// doğrulaması için asgari bir şema takılıyor çünkü uç
/// <c>RequireAuthorization()</c> taşıyor — ve o şartın <b>gerçekten orada
/// olduğu</b> ayrıca ölçülüyor.
/// </para>
/// </summary>
public sealed class McpHttpTransportTests
{
    private const string TestScheme = "M01Test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebApplication BuildHost()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services
            .AddAuthentication(TestScheme)
            .AddScheme<AuthenticationSchemeOptions, AlwaysAuthenticated>(TestScheme, configureOptions: null);

        // ÜRETİMİN kaydı. İkinci bir kurulum yazmak, ölçülen sunucu ile koşan
        // sunucuyu ayırırdı.
        builder.Services.AddBizigoMcp(builder.Configuration);

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBizigoMcp();

        return app;
    }

    /// <summary>
    /// El sıkışma HTTP üzerinden tamamlanıyor ve <b>aynı araç kümesi</b>
    /// görünüyor.
    ///
    /// <para>
    /// "Aynı küme" iddiası boş değil: iki taşıma da
    /// <c>BizigoMcpServer.Apply</c>'den geçiyor, ve burada teldeki sonuç
    /// karşılaştırılıyor. Kurulumlar bir gün ayrışırsa burası kırmızı yanıyor.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_sikismasi_HTTP_uzerinde_de_calisiyor()
    {
        await using var app = BuildHost();

        await app.StartAsync(Ct);

        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestScheme);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, BizigoMcpServer.HttpPath) },
            http,
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            transport, loggerFactory: NullLoggerFactory.Instance, cancellationToken: Ct);

        Assert.Equal(McpSurfaces.ProductName, client.ServerInfo?.Name);

        var tools = (await client.ListToolsAsync(cancellationToken: Ct))
            .Select(static t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([ServerInfoTool.ToolIdentifier], tools);

        // Araç HTTP üzerinden de gerçekten koşuyor — ilan edilmek ile
        // çağrılabilmek ayrı şeyler.
        var result = await client.CallToolAsync(
            ServerInfoTool.ToolIdentifier, new Dictionary<string, object?>(), cancellationToken: Ct);

        Assert.True(result.IsError is not true);
        Assert.Equal(
            McpRevision.Supported,
            result.StructuredContent!.Value.GetProperty("protocol_revision").GetString());

        await app.StopAsync(Ct);
    }

    /// <summary>
    /// <b>Anonim bir MCP oturumu açılamıyor.</b>
    ///
    /// <para>
    /// MCP istemcisi bir <b>kullanıcı</b> adına konuşuyor (plan §6). Kimliğin
    /// uçtan uca taşınması M08'in işi; buradaki kapı onun ön şartı — servis
    /// hesabıyla ya da kimliksiz koşan bir MCP sunucusu <b>bütün kapsam
    /// kapılarını</b> atlar (K17).
    /// </para>
    ///
    /// <para>
    /// Ayrı bir test, çünkü kaybı ayrı: yukarıdaki test kimlikli koşuyor ve
    /// <c>RequireAuthorization()</c> bir gün silinse bile <b>geçmeye devam
    /// ederdi</b>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Kimliksiz_istek_reddediliyor()
    {
        await using var app = BuildHost();

        await app.StartAsync(Ct);

        using var http = app.GetTestClient();

        using var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await http.PostAsync(new Uri(BizigoMcpServer.HttpPath, UriKind.Relative), body, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await app.StopAsync(Ct);
    }

    /// <summary>
    /// Her isteği kimlikli sayan asgari şema. <b>Yalnızca taşımayı</b> ölçmek
    /// için var; gerçek kimlik akışı Keycloak'ta ve M08'in konusu.
    /// </summary>
    private sealed class AlwaysAuthenticated(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.Count == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "m01-test")], TestScheme);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
