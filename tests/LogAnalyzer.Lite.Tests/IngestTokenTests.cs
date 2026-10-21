using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LogAnalyzer.Lite.Configuration;
using Microsoft.Extensions.Logging;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Ingest belirteci.</b> Üretimde Gateway sink'i <c>/api/v1/ingest</c>'e iç
/// Docker ağı üzerinden yazıyor; o ağdaki her konteyner aynı uca da
/// yazabiliyordu ve arayüzü sahte olaylarla doldurabiliyordu.
/// </summary>
public sealed class IngestTokenTests
{
    // Açıkça sahte; hiçbir ortamda kullanılmaz.
    private const string Token = "test-ingest-token-not-a-secret";

    private static Dictionary<string, string?> WithToken() => new() { [LiteSettings.IngestTokenKey] = Token };

    [Fact]
    public async Task Belirtec_tanimliyken_basliksiz_ingest_401_ve_olay_yazilmiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        using var response = await client.SendAsync(Ingest.Error("TRK-NO-TOKEN"), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("ingest_token_invalid", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(await Incidents(client, ct));
    }

    [Theory]
    [InlineData("test-ingest-token-not-a-secreT")] // aynı uzunluk, son karakter farklı
    [InlineData("short")]                          // farklı uzunluk
    [InlineData("")]                               // boş
    public async Task Belirtec_tanimliyken_yanlis_belirtec_401(string wrong)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        using var response = await client.SendAsync(Ingest.Error("TRK-WRONG", wrong), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await Incidents(client, ct));
    }

    [Fact]
    public async Task Belirtec_tanimliyken_iki_baslik_degeri_reddediliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        using var request = Ingest.Error("TRK-DOUBLE", Token);
        request.Headers.Add(Ingest.Header, "second-value");
        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Belirtec_tanimliyken_dogru_belirtec_kabul_ediliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        using var response = await client.SendAsync(Ingest.Error("TRK-GOOD", Token), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var incident = Assert.Single(await Incidents(client, ct));
        Assert.Equal("TRK-GOOD", incident.GetProperty("trackingId").GetString());
    }

    /// <summary>
    /// Geçiş uyumluluğu: belirteç tanımlı değilken ingest kabul ediliyor — ama
    /// bu hâl SESSİZ değil, açılışta bir uyarı basılıyor.
    /// </summary>
    [Fact]
    public async Task Belirtec_tanimsizken_ingest_kabul_ediliyor_ve_acilista_uyari_loglaniyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();

        using var response = await client.SendAsync(Ingest.Error("TRK-OPEN"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await Incidents(client, ct));
        Assert.Contains(app.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains(LiteSettings.IngestTokenKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Belirtec_tanimliyken_uyari_basilmiyor()
    {
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        _ = app.CreateClient();

        Assert.DoesNotContain(app.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("is not set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Belirtec_ne_dogru_ne_yanlis_hali_loglara_yaziliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string wrong = "wrong-token-should-not-be-logged";
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        (await client.SendAsync(Ingest.Error("TRK-1", Token), ct)).Dispose();
        (await client.SendAsync(Ingest.Error("TRK-2", wrong), ct)).Dispose();

        // Pozitif kontrol: red gerçekten loglandı — yani boş bir log yığınına
        // bakıp "sır yok" demiyoruz.
        Assert.Contains(app.Logs.Entries, e => e.Message.Contains("Ingest request rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(Token, app.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(wrong, app.Logs.AllText, StringComparison.Ordinal);
    }

    /// <summary>Okuma uçları belirteç istemiyor — arayüz onları kullanıyor.</summary>
    [Fact]
    public async Task Belirtec_okuma_uclarini_etkilemiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, WithToken());
        var client = app.CreateClient();

        using var stats = await client.GetAsync("/api/v1/stats", ct);
        using var incidents = await client.GetAsync("/api/v1/incidents", ct);

        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(HttpStatusCode.OK, incidents.StatusCode);
    }

    internal static async Task<JsonElement[]> Incidents(HttpClient client, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement[]>("/api/v1/incidents?limit=100", ct) ?? [];
}
