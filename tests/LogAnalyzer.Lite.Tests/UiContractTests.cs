using System.Net;
using System.Text.RegularExpressions;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Arayüz sunucunun kapılarıyla konuşabiliyor mu.</b>
///
/// <para>
/// Sunucu yönetim isteklerinde <c>X-Requested-With: loganalyzer-lite</c>
/// istiyor; arayüz göndermezse "Süpür", "Kaydet", "Test" düğmeleri 403 alır ve
/// birim testleri yeşil kalır. Bu bekçi iki tarafı birbirine bağlıyor.
/// Betik ayrıca CI'da <c>node --check</c> ile ayrıştırılıyor — satır içi hâli
/// bir sözdizimi hatasıyla tamamen ölüydü.
/// </para>
/// </summary>
public sealed partial class UiContractTests
{
    private static readonly string[] AdminPaths =
    [
        "/api/v1/logs/purge",
        "/api/v1/alerts/config",
        "/api/v1/alerts/test",
        "/api/v1/demo/simulate-incident",
    ];

    [GeneratedRegex(@"<script>(?<body>[\s\S]*?)</script>")]
    private static partial Regex InlineScript();

    private static async Task<(string Html, string Script)> Load(CancellationToken ct)
    {
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var script = await client.GetAsync("/app.js", ct);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Contains("javascript", script.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

        return (await client.GetStringAsync("/", ct), await script.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Sayfa_betigi_ayri_dosyadan_yukluyor()
    {
        var (html, _) = await Load(TestContext.Current.CancellationToken);

        Assert.Contains("<script src=\"app.js\"></script>", html, StringComparison.Ordinal);

        // Kalan tek satır içi betik Tailwind yapılandırması; uygulama kodu yok.
        foreach (Match inline in InlineScript().Matches(html))
        {
            Assert.DoesNotContain("fetch(", inline.Groups["body"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Yonetim_cagrilari_yalnizca_basligi_ekleyen_yardimcidan_geciyor()
    {
        var (_, script) = await Load(TestContext.Current.CancellationToken);

        Assert.Contains("'X-Requested-With': 'loganalyzer-lite'", script, StringComparison.Ordinal);

        foreach (var path in AdminPaths)
        {
            Assert.Contains($"adminPost('{path}'", script, StringComparison.Ordinal);
        }

        // Her POST fetch'i başlığı taşıyor. Pozitif kontrol: en az bir POST
        // fetch'i var — yoksa kural boş bir kümeye bakıp yeşil yanardı.
        var posts = FetchCall().Matches(script)
            .Select(m => m.Groups["args"].Value)
            .Where(args => args.Contains("'POST'", StringComparison.Ordinal) || args.Contains("\"POST\"", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(posts);
        Assert.All(posts, args => Assert.Contains("headers: ADMIN_HEADERS", args, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"fetch\((?<args>[^;]*?)\);", RegexOptions.Singleline)]
    private static partial Regex FetchCall();

    /// <summary>
    /// Olay alanları dışarıdan geliyor (ingest). <c>innerHTML</c> şablonuna
    /// ham <c>${item.…}</c> girerse kayıtlı XSS — ve XSS, CSRF kapısını aynı
    /// kökenden aşar.
    /// </summary>
    [Fact]
    public async Task Olay_alanlari_html_e_kacirilmadan_girmiyor()
    {
        var (_, script) = await Load(TestContext.Current.CancellationToken);

        Assert.Contains("function esc(", script, StringComparison.Ordinal);
        Assert.Contains("${esc(item.errorMessage", script, StringComparison.Ordinal);
        Assert.DoesNotContain("${item.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=\"openDetail(", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_sirri_forma_geri_yazilmiyor()
    {
        var (_, script) = await Load(TestContext.Current.CancellationToken);

        // Eski hâli GET'ten gelen adresi `.value`'ya yazıyordu.
        Assert.DoesNotContain(".value = cfg.slackWebhookUrl", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".value = cfg.discordWebhookUrl", script, StringComparison.Ordinal);
        Assert.Contains("clearSlackWebhook", script, StringComparison.Ordinal);
        Assert.Contains("clearDiscordWebhook", script, StringComparison.Ordinal);
    }
}
