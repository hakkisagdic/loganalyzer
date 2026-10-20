using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b>Kalıcılık.</b> Üretimde bir Coolify dağıtımının <b>sessizce boş</b>
/// başlaması asıl risk: veri dizini derleme çıktısından türetiliyordu, yazım
/// hataları yutuluyordu ve boş depoya sahte bir açılış olayı yazılıyordu.
/// </summary>
public sealed class StorageTests
{
    private static readonly UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    [Fact]
    public void Veri_dizini_varsayilani_app_data()
    {
        var empty = new ConfigurationBuilder().Build();

        Assert.Equal("/app/data", LiteSettings.From(empty).DataDirectory);
    }

    /// <summary>
    /// Yeniden başlatma: olaylar VE alarm ayarı (webhook sırrı dahil) ikinci
    /// uygulama örneğine aynı dizinden geliyor.
    /// </summary>
    [Fact]
    public async Task Olaylar_ve_alarm_ayari_yeniden_baslatmadan_sag_cikiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();

        await using (var first = new LiteAppFactory(dir.DataPath))
        {
            var client = first.CreateClient();
            (await client.SendAsync(Ingest.Error("TRK-PERSIST-1"), ct)).Dispose();
            (await client.SendAsync(Ingest.Error("TRK-PERSIST-2"), ct)).Dispose();
            using var saved = await client.SendAsync(Admin.Post("/api/v1/alerts/config", new
            {
                slackWebhookUrl = WebhookConfigTests.SlackUrl,
                minLevel = "Fatal",
                cooldownSeconds = 120,
            }), ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        await using var second = new LiteAppFactory(dir.DataPath);
        var restarted = second.CreateClient();

        var ids = (await IngestTokenTests.Incidents(restarted, ct))
            .Select(i => i.GetProperty("trackingId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["TRK-PERSIST-1", "TRK-PERSIST-2"], ids);

        var stats = await restarted.GetFromJsonAsync<JsonElement>("/api/v1/stats", ct);
        Assert.Equal(2, stats.GetProperty("activeIncidents").GetInt32());

        var config = await WebhookConfigTests.Config(restarted, ct);
        Assert.True(config.GetProperty("slackConfigured").GetBoolean());
        Assert.Equal("Fatal", config.GetProperty("minLevel").GetString());
        Assert.Equal(120, config.GetProperty("cooldownSeconds").GetInt32());

        // Sır da kalıcı: test çağrısı kaydedilen adrese gidiyor.
        (await restarted.SendAsync(Admin.Post("/api/v1/alerts/test", new { target = "slack" }), ct)).Dispose();
        Assert.Equal(WebhookConfigTests.SlackUrl, Assert.Single(second.Webhooks.Requests).Uri.AbsoluteUri);

        Assert.Contains(second.Logs.Entries, e => e.Message.Contains("Loaded 2 incidents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bos_depo_bos_basliyor_sahte_acilis_olayi_yok()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath);

        Assert.Empty(await IngestTokenTests.Incidents(app.CreateClient(), ct));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")] // Windows'ta SkipWhen ile atlanıyor.
    public async Task Dosyalar_ve_dizin_dunyaya_kapali_gecici_dosya_kalmiyor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix dosya kipleri Windows'ta yok.");

        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();

        await using (var app = new LiteAppFactory(dir.DataPath))
        {
            var client = app.CreateClient();
            (await client.SendAsync(Ingest.Error("TRK-MODE"), ct)).Dispose();
            (await client.SendAsync(Admin.Post("/api/v1/alerts/config", new { slackWebhookUrl = WebhookConfigTests.SlackUrl }), ct)).Dispose();
        }

        var incidents = dir.File(IncidentFlightRecorder.StorageFileName);
        var alerts = dir.File(AlertWebhookNotifier.ConfigFileName);

        // Pozitif kontrol: dosyalar gerçekten yazıldı.
        Assert.True(File.Exists(incidents));
        Assert.True(File.Exists(alerts));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(incidents));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(alerts));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(dir.DataPath));

        Assert.Empty(Directory.EnumerateFiles(dir.DataPath, "*.tmp"));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir.DataPath), f => Path.GetFileName(f).StartsWith('.'));

        // İçerik geçerli JSON (yarım yazım yok).
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(incidents, ct));
        Assert.Equal(1, parsed.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Eski imaj dosyaları root olarak 0644 ve dizini 0777 bırakıyordu.
    /// Sahibi bizsek açılışta daraltılıyor.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")] // Windows'ta SkipWhen ile atlanıyor.
    public async Task Mevcut_genis_izinler_acilista_daraltiliyor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix dosya kipleri Windows'ta yok.");

        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.DataPath);
        File.SetUnixFileMode(dir.DataPath, (UnixFileMode)Convert.ToInt32("777", 8));
        var incidents = dir.File(IncidentFlightRecorder.StorageFileName);
        await File.WriteAllTextAsync(incidents, "[]", ct);
        File.SetUnixFileMode(incidents, (UnixFileMode)Convert.ToInt32("644", 8));

        await using var app = new LiteAppFactory(dir.DataPath);
        _ = app.CreateClient();

        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(dir.DataPath) & GroupOrOther);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(incidents) & GroupOrOther);
    }

    /// <summary>
    /// Bozuk spool'un üstüne yazmak veri kaybı. Kenara alınıyor, uygulama boş
    /// açılıyor ve bunu bir HATA logu söylüyor.
    /// </summary>
    [Fact]
    public async Task Bozuk_spool_kenara_aliniyor_ustune_yazilmiyor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string corrupt = "{ this is not json";
        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.DataPath);
        await File.WriteAllTextAsync(dir.File(IncidentFlightRecorder.StorageFileName), corrupt, ct);

        await using (var app = new LiteAppFactory(dir.DataPath))
        {
            var client = app.CreateClient();
            Assert.Empty(await IngestTokenTests.Incidents(client, ct));
            (await client.SendAsync(Ingest.Error("TRK-AFTER-CORRUPT"), ct)).Dispose();

            Assert.Contains(app.Logs.Entries, e =>
                e.Level == LogLevel.Error && e.Message.Contains("could not be parsed", StringComparison.Ordinal));
        }

        var quarantined = Assert.Single(Directory.EnumerateFiles(dir.DataPath, IncidentFlightRecorder.StorageFileName + ".corrupt-*"));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(quarantined, ct));

        // Yeni spool ayrı ve geçerli.
        using var fresh = JsonDocument.Parse(await File.ReadAllTextAsync(dir.File(IncidentFlightRecorder.StorageFileName), ct));
        Assert.Equal(1, fresh.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Yazılamayan dizin açılışı durduruyor. Eski hâli yazım hatasını yutuyor,
    /// olaylar yeniden başlatmada sessizce kayboluyordu.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")] // Windows'ta SkipWhen ile atlanıyor.
    public async Task Yazilamayan_veri_dizini_acilisi_durduruyor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix dosya kipleri Windows'ta yok.");

        using var dir = new TempDataDirectory();
        Directory.CreateDirectory(dir.DataPath);
        File.SetUnixFileMode(dir.DataPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        // root her dizine yazabilir; o hâlde bu test bir şey ölçmez.
        var probe = Path.Combine(dir.DataPath, "root-probe");
        var writable = true;
        try
        {
            await File.WriteAllTextAsync(probe, "x", TestContext.Current.CancellationToken);
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            writable = false;
        }

        Assert.SkipWhen(writable, "Süreç root olarak koşuyor; izin daraltması ölçülemez.");

        await using var app = new LiteAppFactory(dir.DataPath);
        var error = Record.Exception(() => app.CreateClient());

        Assert.NotNull(error);
        var chain = new List<Exception>();
        for (var e = error; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        Assert.Contains(chain, e => e is InvalidOperationException && e.Message.Contains("not writable", StringComparison.Ordinal));
    }
}
