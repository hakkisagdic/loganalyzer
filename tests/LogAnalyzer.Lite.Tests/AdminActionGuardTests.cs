using System.Globalization;
using System.Net;
using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Services;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Yönetim eylemlerinin CSRF / aynı köken kapısı.</b>
///
/// <para>
/// Üretimde arayüz Traefik BasicAuth arkasında ve tarayıcı o kimliği her
/// isteğe kendisi ekliyor. Kapı olmadan, operatörün açık bir sekmesi varken
/// herhangi bir site <c>/api/v1/logs/purge</c>'e form gönderip olayları
/// silebilir, webhook adresini değiştirebilirdi.
/// </para>
/// </summary>
public sealed class AdminActionGuardTests
{
    public static TheoryData<string> AdminPaths =>
    [
        "/api/v1/logs/purge",
        "/api/v1/alerts/config",
        "/api/v1/alerts/test",
    ];

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task Basliksiz_yonetim_istegi_403(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().SendAsync(Admin.Post(path, withHeader: false), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("admin_request_rejected", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task Yanlis_baslik_degeri_403(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        // Klasik XHR işareti; bizim değerimiz değil.
        using var response = await app.CreateClient().SendAsync(Admin.Post(path, headerValue: "XMLHttpRequest"), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task Capraz_site_istegi_baslik_olsa_da_403(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().SendAsync(Admin.Post(path, secFetchSite: "cross-site"), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example.test")]
    [InlineData("http://localhost.evil.example.test")]
    [InlineData("http://localhost:8081")] // aynı ana makine, farklı port = farklı köken
    [InlineData("null")]                  // korumalı iframe / file://
    public async Task Yabanci_koken_403(string origin)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().SendAsync(Admin.Post("/api/v1/alerts/config", origin: origin), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task Ayni_koken_ve_baslik_kabul_ediliyor(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        // TestServer'ın ana makinesi `localhost`; tarayıcı aynı kökenden `Origin` gönderir.
        using var response = await app.CreateClient().SendAsync(
            Admin.Post(path, origin: "http://localhost", secFetchSite: "same-origin"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Izinli_listedeki_koken_kabul_ediliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.AllowedOriginsKey] = "https://ops.example.test",
        });

        using var response = await app.CreateClient().SendAsync(
            Admin.Post("/api/v1/alerts/config", origin: "https://ops.example.test", secFetchSite: "same-site"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <c>Sec-Fetch-Site: cross-site</c> izin listesindeki bir kökeni de
    /// geçirmiyor: liste CORS'u ve aynı SİTE'deki kökenleri (ör. Gateway'in alt
    /// alan adı) açıyor, gerçekten çapraz siteden yönetim eylemini değil.
    /// </summary>
    [Fact]
    public async Task Izinli_koken_capraz_siteden_gelirse_403()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.AllowedOriginsKey] = "https://ops.example.test",
        });

        using var response = await app.CreateClient().SendAsync(
            Admin.Post("/api/v1/alerts/config", origin: "https://ops.example.test", secFetchSite: "cross-site"), ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Gateway'in <c>/loganalyzer</c> vekili bugün yalnızca <c>Content-Type</c>'ı
    /// iletiyor; Gateway tarafı <c>X-Requested-With</c>'i ekleyecek ama
    /// <c>Origin</c>/<c>Sec-Fetch-*</c> iletmeyebilir. O hâlde istek başlıkla
    /// geçmeli — tarayıcı dışı bir çağıran özel başlığı zaten koyabiliyor, kapı
    /// tarayıcının CSRF'ine karşı.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdminPaths))]
    public async Task Vekil_Origin_iletmeden_basligi_iletince_kabul(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().SendAsync(Admin.Post(path), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// TLS Traefik'te sonlanıyor: iç istekte şema <c>http</c>, ana makine
    /// tarayıcının gördüğü. Kök isteğin <c>X-Forwarded-Host</c>'u da aynı
    /// köken sayılıyor.
    /// </summary>
    [Fact]
    public async Task Vekil_arkasinda_ayni_koken_kabul_ediliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var request = Admin.Post("/api/v1/alerts/config", origin: "https://loglar.example.test");
        request.Headers.Add("X-Forwarded-Host", "loglar.example.test");
        using var response = await app.CreateClient().SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Kapının yalnızca durum kodu değil <b>etkisi</b>: reddedilen purge hiçbir
    /// şey silmiyor, kabul edilen siliyor. Diskte iki gün önceki bir olay var.
    /// </summary>
    [Fact]
    public async Task Reddedilen_purge_olay_silmiyor_kabul_edilen_siliyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.DataPath);
        var old = DateTime.UtcNow.AddDays(-2).ToString("o", CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(
            dir.File(IncidentFlightRecorder.StorageFileName),
            $$"""[{"id":"old1","trackingId":"TRK-OLD","timestamp":"{{old}}","level":"Error","source":"S","endpoint":"/x","httpMethod":"POST","httpStatus":500,"durationMs":1}]""",
            ct);

        await using var app = new LiteAppFactory(dir.DataPath);
        var client = app.CreateClient();
        var body = new { keepLastHours = 1, resetStats = false };

        using var rejected = await client.SendAsync(Admin.Post("/api/v1/logs/purge", body, withHeader: false), ct);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Single(await IngestTokenTests.Incidents(client, ct));

        using var accepted = await client.SendAsync(Admin.Post("/api/v1/logs/purge", body), ct);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Empty(await IngestTokenTests.Incidents(client, ct));
    }

    /// <summary>Okuma uçları ve ingest kapıya takılmıyor — arayüz ve Gateway onları başlıksız çağırıyor.</summary>
    [Theory]
    [InlineData("/api/v1/stats")]
    [InlineData("/api/v1/incidents")]
    [InlineData("/api/v1/alerts/config")]
    [InlineData("/health")]
    public async Task Okuma_uclari_basliksiz_calisiyor(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        using var response = await app.CreateClient().GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
