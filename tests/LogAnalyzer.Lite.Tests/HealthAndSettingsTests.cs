using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogAnalyzer.Lite.Configuration;
using LogAnalyzer.Lite.Services;
using Microsoft.Extensions.Configuration;

namespace LogAnalyzer.Lite.Tests;

/// <summary>
/// <b><c>/health</c> hangi kodun koştuğunu söylüyor</b> — ve başka hiçbir şeyi.
/// Dağıtım doğrulaması "doğru commit canlıda mı" sorusunu buradan okuyor.
/// </summary>
public sealed partial class HealthTests
{
    [GeneratedRegex("^([0-9a-f]{7,40}|unknown)$")]
    private static partial Regex CommitShape();

    [Fact]
    public async Task Health_surum_ve_ortamdaki_commiti_donuyor()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.CommitKey] = "0123ABCdef",
        });

        var health = await app.CreateClient().GetFromJsonAsync<JsonElement>("/health", ct);

        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        Assert.Equal("0123abcdef", health.GetProperty("commit").GetString());
        Assert.Equal(BuildInfo.Version, health.GetProperty("version").GetString());
        Assert.Equal("1.1.0", health.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("not-a-sha; rm -rf /")]
    [InlineData("abc")]               // çok kısa
    [InlineData("<script>")]
    public async Task Gecersiz_commit_yansitilmiyor(string raw)
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.CommitKey] = raw,
        });

        var commit = (await app.CreateClient().GetFromJsonAsync<JsonElement>("/health", ct))
            .GetProperty("commit").GetString();

        // Derleme kendi SHA'sını taşıyabilir (yerel git); ortamdaki metin ise asla.
        Assert.NotEqual(raw, commit);
        Assert.Matches(CommitShape(), commit!);
    }

    [Fact]
    public async Task Health_yalnizca_beyan_edilen_alanlari_tasiyor_sir_yok()
    {
        var ct = TestContext.Current.CancellationToken;
        const string token = "health-must-not-echo-this-token";
        using var dir = new TempDataDirectory();
        await using var app = new LiteAppFactory(dir.DataPath, new Dictionary<string, string?>
        {
            [LiteSettings.IngestTokenKey] = token,
            [LiteSettings.AllowedOriginsKey] = "https://ops.example.test",
        });

        var raw = await app.CreateClient().GetStringAsync("/health", ct);
        var names = JsonDocument.Parse(raw).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["commit", "component", "product", "status", "timestamp", "version"], names);
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("ops.example.test", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(dir.DataPath, raw, StringComparison.Ordinal);
    }
}

public sealed class LiteSettingsTests
{
    private static LiteSettings From(params (string Key, string? Value)[] pairs) =>
        LiteSettings.From(new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build());

    [Theory]
    [InlineData("https://ops.example.test", "https://ops.example.test")]
    [InlineData("https://OPS.example.test/", "https://ops.example.test")]
    [InlineData("https://ops.example.test:443", "https://ops.example.test")]
    [InlineData("http://localhost:5060", "http://localhost:5060")]
    public void Koken_normalize_ediliyor(string raw, string expected)
    {
        Assert.True(LiteSettings.TryNormalizeOrigin(raw, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("https://ops.example.test/path")]
    [InlineData("https://user@ops.example.test")]
    [InlineData("ftp://ops.example.test")]
    [InlineData("ops.example.test")]
    [InlineData("")]
    public void Gecersiz_koken_reddediliyor(string raw) =>
        Assert.False(LiteSettings.TryNormalizeOrigin(raw, out _));

    [Fact]
    public void Bos_belirtec_tanimsiz_sayiliyor()
    {
        Assert.Null(From((LiteSettings.IngestTokenKey, "   ")).IngestToken);
        Assert.Null(From().IngestToken);
        Assert.Equal("x", From((LiteSettings.IngestTokenKey, " x ")).IngestToken);
    }

    [Fact]
    public void Gecersiz_girdiler_uyari_uretiyor_belirtec_asla()
    {
        var settings = From(
            (LiteSettings.IngestTokenKey, "token-must-not-appear-in-warnings"),
            (LiteSettings.AllowedOriginsKey, "*,https://ok.example.test"),
            (LiteSettings.FrameAncestorsKey, "https:"));

        Assert.Equal(["https://ok.example.test"], settings.AllowedOrigins);
        Assert.Empty(settings.FrameAncestors);
        Assert.Equal(2, settings.Warnings.Count);
        Assert.DoesNotContain(settings.Warnings, w => w.Contains("token-must-not-appear", StringComparison.Ordinal));
    }
}
