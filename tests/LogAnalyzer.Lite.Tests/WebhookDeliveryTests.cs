using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogAnalyzer.Lite.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Webhook teslimi: genel hata kategorileri, yönlendirme yok, sır loglanmıyor.</b>
///
/// <para>
/// Eski test ucu karşı tarafın durum kodunu + durum metnini ve istisna
/// mesajını olduğu gibi geri döndürüyordu. Herhangi bir adrese POST atabilen
/// bir uçla birleşince bu bir <b>yanıt kâhini</b>: iç ağdaki bir servisin
/// var olup olmadığı, hangi hatayı verdiği dışarıdan okunabiliyordu.
/// </para>
/// </summary>
public sealed class WebhookDeliveryTests
{
    private const string TestPath = "/api/v1/alerts/test";

    private static async Task<HttpClient> Configured(LiteAppFactory app, CancellationToken ct)
    {
        var client = app.CreateClient();
        using var saved = await client.SendAsync(Admin.Post("/api/v1/alerts/config", new
        {
            enabled = true,
            slackWebhookUrl = WebhookConfigTests.SlackUrl,
            discordWebhookUrl = WebhookConfigTests.DiscordUrl,
            cooldownSeconds = 0,
        }), ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return client;
    }

    [Fact]
    public async Task Basarili_test_kayitli_adrese_json_gonderiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = await Configured(app, ct);

        using var response = await client.SendAsync(Admin.Post(TestPath, new { target = "all" }), ct);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.GetProperty("slackSuccess").GetBoolean());
        Assert.True(result.GetProperty("discordSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("slackError").ValueKind);

        Assert.Collection(
            app.Webhooks.Requests,
            slack => Assert.Equal(WebhookConfigTests.SlackUrl, slack.Uri.AbsoluteUri),
            discord =>
            {
                Assert.Equal(WebhookConfigTests.DiscordUrl, discord.Uri.AbsoluteUri);
                // Test mesajı da dahil: @everyone ping atılmasın.
                var payload = JsonDocument.Parse(discord.Body).RootElement;
                Assert.Equal(0, payload.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
            });
    }

    public static TheoryData<string, Func<HttpRequestMessage, HttpResponseMessage>, string> Failures => new()
    {
        {
            "rejected",
            static _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { ReasonPhrase = "internal-reason-leak-canary" },
            "internal-reason-leak-canary"
        },
        {
            "rejected",
            static _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("http://10.11.12.13/admin");
                return redirect;
            },
            "10.11.12.13"
        },
        {
            "unreachable",
            static _ => throw new HttpRequestException("Connection refused (10.20.30.40:5060) leak-canary"),
            "10.20.30.40"
        },
        {
            "timeout",
            static _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout leak-canary", new TimeoutException()),
            "leak-canary"
        },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Hata_genel_kategoriyle_donuyor_ayrinti_sizmiyor(
        string category,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string canary)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = await Configured(app, ct);
        app.Webhooks.Respond = respond;

        using var response = await client.SendAsync(Admin.Post(TestPath, new { target = "slack" }), ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var result = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(result.GetProperty("slackSuccess").GetBoolean());
        Assert.Equal(category, result.GetProperty("slackError").GetString());

        // Pozitif kontrol: istek gerçekten sağlayıcıya ulaştı (sahte işleyici
        // çağrıldı) — yani kategori, hiç denenmemiş bir gönderimin değil.
        Assert.Single(app.Webhooks.Requests);

        Assert.DoesNotContain(canary, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookConfigTests.SlackSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, app.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookConfigTests.SlackSecret, app.Logs.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"target":"http://10.0.0.1/"}""", "invalid_target")]
    [InlineData("""{"target":"teams"}""", "invalid_target")]
    public async Task Gecersiz_test_istegi_400(string json, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var request = Admin.Post(TestPath);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await app.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(app.Webhooks.Requests);
    }

    [Fact]
    public async Task Cok_uzun_test_mesaji_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = await Configured(app, ct);

        using var response = await client.SendAsync(Admin.Post(TestPath, new
        {
            target = "slack",
            customMessage = new string('x', AlertWebhookNotifier.MaxCustomMessageLength + 1),
        }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(app.Webhooks.Requests);
    }

    /// <summary>
    /// Gerçek akış: ingest edilen hata olayı iki kanala da bildiriliyor.
    /// Olay metni dışarıdan geldiği için Slack etiketleri kaçırılıyor ve
    /// Discord ping'leri kapalı.
    /// </summary>
    [Fact]
    public async Task Ingest_edilen_olay_iki_kanala_da_bildiriliyor_ve_etiketler_etkisiz()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = await Configured(app, ct);

        using var ingest = await client.SendAsync(Ingest.Error("TRK-NOTIFY", errorMessage: "<!channel> boom @everyone"), ct);
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        await app.Webhooks.WaitForRequestsAsync(2, ct);

        var slack = Assert.Single(app.Webhooks.Requests, r => r.Uri.AbsoluteUri == WebhookConfigTests.SlackUrl);
        var discord = Assert.Single(app.Webhooks.Requests, r => r.Uri.AbsoluteUri == WebhookConfigTests.DiscordUrl);

        var slackText = JsonDocument.Parse(slack.Body).RootElement.GetProperty("text").GetString();
        Assert.Contains("TRK-NOTIFY", slackText, StringComparison.Ordinal);
        Assert.Contains("&lt;!channel&gt;", slackText, StringComparison.Ordinal);
        Assert.DoesNotContain("<!channel>", slackText, StringComparison.Ordinal);

        var discordPayload = JsonDocument.Parse(discord.Body).RootElement;
        Assert.Equal(0, discordPayload.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    /// <summary>
    /// ÜRETİMİN istemcisi — sahte işleyici devrede değil. Zaman aşımı var,
    /// birincil işleyici yönlendirme izlemiyor.
    /// </summary>
    [Fact]
    public async Task Uretim_istemcisi_zaman_asimli_ve_yonlendirme_izlemiyor()
    {
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, realWebhookHandler: true);

        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(AlertWebhookNotifier.HttpClientName);
        Assert.Equal(AlertWebhookNotifier.RequestTimeout, client.Timeout);
        Assert.True(client.Timeout <= TimeSpan.FromSeconds(30));

        HttpMessageHandler handler = app.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(AlertWebhookNotifier.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            // Varsayılan HttpClientFactory log işleyicisi tam URL'yi yazıyor;
            // webhook yolunda sır var. Zincirde OLMAMALI.
            Assert.DoesNotContain("Logging", delegating.GetType().Name, StringComparison.Ordinal);
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }
}
