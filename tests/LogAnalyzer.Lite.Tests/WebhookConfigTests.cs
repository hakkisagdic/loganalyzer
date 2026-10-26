using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogAnalyzer.Lite.Services;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Webhook yapılandırması: izin listesi, maskeleme, sırrı koruma.</b>
///
/// <para>
/// Eski hâli herhangi bir URL'yi kabul ediyor, <c>GET</c> ile adresleri
/// (yani sırları) olduğu gibi döndürüyor ve hiç kullanılmayan bir
/// <c>customWebhookUrl</c> saklıyordu.
/// </para>
/// </summary>
public sealed class WebhookConfigTests
{
    // Biçimce geçerli, içerikçe sahte adresler.
    internal const string SlackUrl = "https://hooks.slack.com/services/TFAKE000/BFAKE000/fakeSlackSecret123";
    internal const string SlackSecret = "fakeSlackSecret123";
    internal const string DiscordUrl = "https://discord.com/api/webhooks/123456789/fakeDiscordSecret-456";
    internal const string DiscordSecret = "fakeDiscordSecret-456";

    private const string ConfigPath = "/api/v1/alerts/config";

    [Theory]
    [InlineData("http://hooks.slack.com/services/T0/B0/x")]                 // şema
    [InlineData("https://hooks.slack.com.evil.example.test/services/T0/B0/x")] // ana makine öneki
    [InlineData("https://evil.example.test/services/T0/B0/x")]
    [InlineData("https://hooks.slack.com/api/T0/B0/x")]                     // yol
    [InlineData("https://user:pw@hooks.slack.com/services/T0/B0/x")]        // kullanıcı bilgisi
    [InlineData("https://hooks.slack.com:8443/services/T0/B0/x")]           // port
    [InlineData("https://hooks.slack.com/services/T0/B0/x?next=http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]                // bulut meta veri
    [InlineData("http://loganalyzer-lite:5060/api/v1/logs/purge")]          // komşu konteyner
    [InlineData("https://hooks.slack.com/services/********x")]             // maskeden türetilmiş
    [InlineData("https://hooks.slack.com./services/T0/B0/x")]              // sondaki nokta
    [InlineData("https://52.1.2.3/services/T0/B0/x")]                      // IP sabiti
    [InlineData("not a url")]
    public async Task Gecersiz_slack_adresi_400_ve_kayit_degismiyor(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var response = await client.SendAsync(Admin.Post(ConfigPath, new { slackWebhookUrl = url }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("invalid_slack_webhook", error.GetProperty("error").GetString());
        Assert.Contains("hooks.slack.com/services", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False((await Config(client, ct)).GetProperty("slackConfigured").GetBoolean());
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/not-numeric/token")]
    [InlineData("https://discord.gg/api/webhooks/1/token")]
    [InlineData("https://discord.com.evil.example.test/api/webhooks/1/token")]
    [InlineData("http://discord.com/api/webhooks/1/token")]
    [InlineData("https://ptb.discord.com/api/webhooks/1/token")]
    [InlineData("https://discord.com/api/webhooks/1/token/../../../users/@me")]
    public async Task Gecersiz_discord_adresi_400(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var response = await client.SendAsync(Admin.Post(ConfigPath, new { discordWebhookUrl = url }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("invalid_discord_webhook", error.GetProperty("error").GetString());
        Assert.False((await Config(client, ct)).GetProperty("discordConfigured").GetBoolean());
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456789/fakeDiscordSecret-456")]
    [InlineData("https://discordapp.com/api/webhooks/123456789/fakeDiscordSecret-456")]
    public async Task Gecerli_discord_adresleri_kabul_ediliyor(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var response = await client.SendAsync(Admin.Post(ConfigPath, new { discordWebhookUrl = url }), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await Config(client, ct)).GetProperty("discordConfigured").GetBoolean());
    }

    [Fact]
    public async Task Bos_olmayan_customWebhookUrl_reddediliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var rejected = await client.SendAsync(Admin.Post(ConfigPath, new { customWebhookUrl = "https://example.test/hook" }), ct);
        using var empty = await client.SendAsync(Admin.Post(ConfigPath, new { customWebhookUrl = "" }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("custom_webhook_unsupported", await rejected.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
    }

    /// <summary>
    /// GET sırrın hiçbir parçasını döndürmüyor: sabit maske + "kayıtlı" bayrağı.
    /// Pozitif kontrol: aynı adres kaydedildikten sonra bakılıyor.
    /// </summary>
    [Fact]
    public async Task Get_maskeli_ve_bayrakli_sir_donmuyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var saved = await client.SendAsync(Admin.Post(ConfigPath, new { slackWebhookUrl = SlackUrl, discordWebhookUrl = DiscordUrl }), ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var response = await client.GetAsync(ConfigPath, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var config = JsonDocument.Parse(raw).RootElement;

        Assert.True(config.GetProperty("slackConfigured").GetBoolean());
        Assert.True(config.GetProperty("discordConfigured").GetBoolean());
        Assert.Equal(WebhookUrlPolicy.SlackMask, config.GetProperty("slackWebhookUrl").GetString());
        Assert.Equal(WebhookUrlPolicy.DiscordMask, config.GetProperty("discordWebhookUrl").GetString());
        Assert.False(config.TryGetProperty("customWebhookUrl", out _));
        Assert.DoesNotContain(SlackSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("TFAKE000", raw, StringComparison.Ordinal);

        // POST yanıtı da aynı görünümü taşıyor; sır orada da yok.
        Assert.DoesNotContain(SlackSecret, await saved.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(WebhookUrlPolicy.SlackMask)]
    public async Task Bos_ya_da_maskeli_deger_kayitli_sirri_koruyor(string? submitted)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        (await client.SendAsync(Admin.Post(ConfigPath, new { slackWebhookUrl = SlackUrl }), ct)).Dispose();

        // Arayüzün "yalnızca cooldown'u değiştirdim" kaydı.
        using var update = await client.SendAsync(Admin.Post(ConfigPath, new { slackWebhookUrl = submitted, cooldownSeconds = 5 }), ct);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var config = await Config(client, ct);
        Assert.True(config.GetProperty("slackConfigured").GetBoolean());
        Assert.Equal(5, config.GetProperty("cooldownSeconds").GetInt32());

        // Kayıtlı adres gerçekten ilk adres mi: test çağrısı oraya gidiyor.
        using var test = await client.SendAsync(Admin.Post("/api/v1/alerts/test", new { target = "slack" }), ct);
        var sent = Assert.Single(app.Webhooks.Requests);
        Assert.Equal(SlackUrl, sent.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Acik_silme_adresi_kaldiriyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        (await client.SendAsync(Admin.Post(ConfigPath, new { slackWebhookUrl = SlackUrl, discordWebhookUrl = DiscordUrl }), ct)).Dispose();

        using var cleared = await client.SendAsync(Admin.Post(ConfigPath, new { clearSlackWebhook = true }), ct);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        var config = await Config(client, ct);
        Assert.False(config.GetProperty("slackConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, config.GetProperty("slackWebhookUrl").ValueKind);
        Assert.True(config.GetProperty("discordConfigured").GetBoolean());

        using var test = await client.SendAsync(Admin.Post("/api/v1/alerts/test", new { target = "slack" }), ct);
        var result = await test.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(result.GetProperty("slackSuccess").GetBoolean());
        Assert.Equal("not_configured", result.GetProperty("slackError").GetString());
        Assert.Empty(app.Webhooks.Requests);
    }

    [Fact]
    public async Task Silme_ve_yeni_adres_ayni_istekte_400()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().SendAsync(
            Admin.Post(ConfigPath, new { slackWebhookUrl = SlackUrl, clearSlackWebhook = true }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("conflicting_slack_webhook", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"minLevel":"Debug"}""", "invalid_min_level")]
    [InlineData("""{"cooldownSeconds":-1}""", "invalid_cooldown")]
    [InlineData("""{"slaThresholdMs":-5}""", "invalid_sla_threshold")]
    public async Task Gecersiz_alanlar_400(string json, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var request = Admin.Post(ConfigPath);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await app.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// Eski sürümün birime yazdığı adresler yükseltmeden sonra da
    /// kullanılmamalı: izin listesi DİSKTEN okunan değere de uygulanıyor.
    /// </summary>
    [Fact]
    public async Task Eski_surumden_kalan_izinsiz_adres_kullanilmiyor_ve_loglanmiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string internalUrl = "http://169.254.169.254/latest/meta-data/iam/";
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.DataPath);
        await File.WriteAllTextAsync(
            dir.File(AlertWebhookNotifier.ConfigFileName),
            $$"""{"enabled":true,"slackWebhookUrl":"{{internalUrl}}","discordWebhookUrl":"{{DiscordUrl}}","customWebhookUrl":"http://internal.example.test/hook","minLevel":"Error","cooldownSeconds":60}""",
            ct);

        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        var config = await Config(client, ct);
        Assert.False(config.GetProperty("slackConfigured").GetBoolean());
        Assert.True(config.GetProperty("discordConfigured").GetBoolean());

        using var test = await client.SendAsync(Admin.Post("/api/v1/alerts/test", new { target = "slack" }), ct);
        var result = await test.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("not_configured", result.GetProperty("slackError").GetString());
        Assert.Empty(app.Webhooks.Requests);

        Assert.Contains(app.Logs.Entries, e => e.Message.Contains("Stored Slack webhook address is not an allowed", StringComparison.Ordinal));
        Assert.DoesNotContain("169.254.169.254", app.Logs.AllText, StringComparison.Ordinal);
    }

    internal static async Task<JsonElement> Config(HttpClient client, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement>(ConfigPath, ct);
}

/// <summary>İzin listesinin kendisi — sunucusuz, tüm sınır durumlarıyla.</summary>
public sealed class WebhookUrlPolicyTests
{
    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXX")]
    [InlineData("https://HOOKS.SLACK.COM/services/T000/B000/XXXX")]
    [InlineData("https://hooks.slack.com:443/services/T000/B000/XXXX")]
    [InlineData("  https://hooks.slack.com/services/T000/B000/XXXX  ")]
    public void Slack_gecerli(string url) => Assert.True(WebhookUrlPolicy.TryValidate(WebhookKind.Slack, url, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://hooks.slack.com/services/")]
    [InlineData("https://hooks.slack.com/services/T000/B000/X%2F..%2F")]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXX#frag")]
    [InlineData("https://hooks.slack.com/services/T000 /B000/XXXX")]
    [InlineData("https://hooks.slack.com/services/../../admin")]
    [InlineData("https://hooks.slack.com\\@evil.example.test/services/T0")]
    [InlineData("https://xn--hooks-slack-com.example.test/services/T0")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://hooks.slack.com/services/T0/B0/X")]
    [InlineData("https://hooks.slack.com/services/T0/B0/X\nY")]
    [InlineData("https://hooks.slack.com./services/T000/B000/XXXX")]        // sondaki nokta: aynı ad, farklı dizge
    [InlineData("https://hooks.slack.com.:443/services/T000/B000/XXXX")]
    [InlineData("https://54.230.10.1/services/T000/B000/XXXX")]             // IP sabiti
    [InlineData("https://[::1]/services/T000/B000/XXXX")]
    [InlineData("https://0x7f000001/services/T000/B000/XXXX")]              // onaltılık IPv4
    [InlineData("https://hooks.slack.com:80/services/T000/B000/XXXX")]      // 443 dışı port
    [InlineData("https://hooks.slack.com@evil.example.test/services/T0/B0/X")]
    public void Slack_gecersiz(string? url) => Assert.False(WebhookUrlPolicy.TryValidate(WebhookKind.Slack, url, out _));

    [Fact]
    public void Cok_uzun_adres_reddediliyor() =>
        Assert.False(WebhookUrlPolicy.TryValidate(
            WebhookKind.Slack,
            "https://hooks.slack.com/services/" + new string('a', WebhookUrlPolicy.MaxLength),
            out _));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc")]
    [InlineData("https://discordapp.com/api/webhooks/1234567890/abc_DEF-123")]
    public void Discord_gecerli(string url) => Assert.True(WebhookUrlPolicy.TryValidate(WebhookKind.Discord, url, out _));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/abc?wait=true")]
    [InlineData("https://discord.com/api/webhooks/1")]
    [InlineData("https://discord.com/api/webhooks/1/abc/extra")]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXX")] // yanlış tür
    [InlineData("https://discord.com./api/webhooks/1/abc")]          // sondaki nokta
    [InlineData("https://discordapp.com./api/webhooks/1/abc")]
    [InlineData("https://162.159.128.233/api/webhooks/1/abc")]       // IP sabiti
    [InlineData("https://discord.com:8443/api/webhooks/1/abc")]      // 443 dışı port
    [InlineData("https://user@discord.com/api/webhooks/1/abc")]      // kullanıcı bilgisi
    [InlineData("https://canary.discord.com/api/webhooks/1/abc")]    // alt alan adı
    public void Discord_gecersiz(string url) => Assert.False(WebhookUrlPolicy.TryValidate(WebhookKind.Discord, url, out _));

    [Fact]
    public void Maskeler_hicbir_zaman_gecerli_adres_degil()
    {
        Assert.False(WebhookUrlPolicy.TryValidate(WebhookKind.Slack, WebhookUrlPolicy.SlackMask, out _));
        Assert.False(WebhookUrlPolicy.TryValidate(WebhookKind.Discord, WebhookUrlPolicy.DiscordMask, out _));
    }
}
