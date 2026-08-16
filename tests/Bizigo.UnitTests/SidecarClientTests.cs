using System.Net;
using System.Text;
using Bizigo.Ingest.Discovery;

namespace Bizigo.UnitTests;

/// <summary>
/// Sidecar HTTP istemcisi (F1 §9).
///
/// <para>
/// Tek bir davranış hepsinin altında: <b>istemci istisna fırlatmaz</b>. Fırlatsa
/// keşif işçisi düşer, keşif sessizce ölür ve <c>template_id</c>'nin neden boş
/// kaldığını kimse aramaz. Hata bu yüzden veri olarak dönüyor.
/// </para>
/// </summary>
public sealed class SidecarClientTests
{
    private static readonly MineRequest Request =
        new("firewall", [new MineMessage("0", "deny tcp 10.0.0.1")]);

    private static SidecarClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        SidecarOptions? options = null)
    {
        options ??= new SidecarOptions { Timeout = TimeSpan.FromSeconds(2) };
        var http = new HttpClient(new StubHandler(handler))
        {
            BaseAddress = new Uri("http://sidecar.test/"),
        };

        return new SidecarClient(options, http);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Basarili_yanit_ayristiriliyor()
    {
        using var client = Client((_, _) => Task.FromResult(Json(
            """
            {"api_version":"v1","masks_version":1,"cluster_count":3,
             "results":[{"id":"0","template_id":"firewall:2","template":"deny tcp <IPV4>",
                         "is_new":true,"masked":"deny tcp <IPV4>"}]}
            """)));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.NotNull(outcome.Response);
        Assert.Equal(3, outcome.Response.ClusterCount);

        var result = Assert.Single(outcome.Response.Results);
        Assert.Equal("firewall:2", result.TemplateId);
        Assert.True(result.IsNew);
        Assert.Equal("deny tcp <IPV4>", result.Masked);
    }

    [Fact]
    public async Task Surum_uyusmazligi_isaretleniyor()
    {
        using var client = Client((_, _) => Task.FromResult(Json(
            """{"api_version":"v2","masks_version":1,"cluster_count":0,"results":[]}""")));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.VersionMismatch);
        Assert.Null(outcome.Response);
    }

    [Fact]
    public async Task Maske_surumu_uyusmazligi_isaretleniyor()
    {
        // Farklı maske sürümü = farklı imza = yanlış `template_id`.
        var options = new SidecarOptions { MasksVersion = 1, Timeout = TimeSpan.FromSeconds(2) };
        using var client = Client(
            (_, _) => Task.FromResult(Json(
                """{"api_version":"v1","masks_version":9,"cluster_count":0,"results":[]}""")),
            options);

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.VersionMismatch);
    }

    [Fact]
    public async Task HTTP_hatasi_istisna_yerine_sonuc_donuyor()
    {
        using var client = Client((_, _) =>
            Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Response);
        Assert.Contains("500", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Baglanti_hatasi_istisna_yerine_sonuc_donuyor()
    {
        using var client = Client((_, _) =>
            throw new HttpRequestException("Connection refused"));

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Response);
        Assert.False(outcome.TimedOut);
        Assert.Contains("Connection refused", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zaman_asimi_iptal_ediliyor_ve_isaretleniyor()
    {
        // F1 §9: 2 sn; aşan istek iptal edilir. Testte 100 ms.
        var options = new SidecarOptions { Timeout = TimeSpan.FromMilliseconds(100) };
        using var client = Client(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return Json("{}");
            },
            options);

        var outcome = await client.MineAsync(Request, TestContext.Current.CancellationToken);

        Assert.True(outcome.TimedOut);
        Assert.Null(outcome.Response);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
