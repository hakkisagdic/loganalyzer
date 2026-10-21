using System.Net;
using LogAnalyzer.Lite.Configuration;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Demo ucu üretim deposuna sahte olay yazıyor</b> (sahte VKN'li fatura
/// yükü, sahte ödeme hatası). Varsayılan kapalı; yalnızca
/// <c>LITE_ENABLE_DEMO=true</c> açıyor.
/// </summary>
public sealed class DemoEndpointTests
{
    private const string Path = "/api/v1/demo/simulate-incident";

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("1")]   // yalnızca "true" açar
    [InlineData("yes")]
    public async Task Demo_varsayilan_ve_true_disinda_404(string? flag)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.EnableDemoKey] = flag,
        });
        var client = app.CreateClient();

        // Yönetim başlığı olsa da olmasa da: uç yok.
        using var withHeader = await client.SendAsync(Admin.Post(Path), ct);
        using var withoutHeader = await client.SendAsync(Admin.Post(Path, withHeader: false), ct);

        Assert.Equal(HttpStatusCode.NotFound, withHeader.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withoutHeader.StatusCode);
        Assert.Empty(await IngestTokenTests.Incidents(client, ct));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public async Task Demo_acikken_olay_yaziyor_ama_yonetim_kapisindan_geciyor(string flag)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.EnableDemoKey] = flag,
        });
        var client = app.CreateClient();

        using var rejected = await client.SendAsync(Admin.Post(Path, withHeader: false), ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Empty(await IngestTokenTests.Incidents(client, ct));

        using var accepted = await client.SendAsync(Admin.Post(Path), ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var incident = Assert.Single(await IngestTokenTests.Incidents(client, ct));
        Assert.Equal("true", incident.GetProperty("metadata").GetProperty("simulation").GetString());
    }
}
