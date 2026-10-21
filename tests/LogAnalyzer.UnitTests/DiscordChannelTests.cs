using System.Net;
using System.Text.Json;
using LogAnalyzer.Alerting;
using LogAnalyzer.Alerting.Notifications;
using LogAnalyzer.ControlPlane;

namespace LogAnalyzer.UnitTests;

/// <summary>
/// Discord embed gövdesi. 80f0aeb'de özet satırlarını ayıran <c>"\n"</c> kaçışları
/// kaynakta gerçek satır sonuna dönüşmüş ve Alerting projesi derlenmez olmuştu;
/// bu bekçiler gövdenin biçimini ve zaman aralığının UTC yazılmasını sabitliyor.
/// </summary>
public sealed class DiscordChannelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string DiscordHook =
        "https://discord.com/api/webhooks/123456789/XyZgizliDISCORDjetonu9876";

    [Fact]
    public async Task Ozet_satirlari_satir_sonuyla_ayriliyor_ve_besten_fazlasi_ozetleniyor()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var channel = new DiscordChannel(new CapturingHttpClientFactory(handler), Options());

        var lines = Enumerable.Range(1, 7)
            .Select(i => new NotificationLine(string.Empty, i, 1, $"satır {i}"))
            .ToList();

        var result = await channel.SendAsync(Message(lines), Channel(DiscordHook), Token);

        Assert.True(result.Ok);
        using var body = JsonDocument.Parse(handler.Body!);
        var embed = body.RootElement.GetProperty("embeds")[0];
        var summary = embed.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "Olay Ozeti")
            .GetProperty("value").GetString();

        Assert.Equal(
            "• satır 1\n• satır 2\n• satır 3\n• satır 4\n• satır 5\n... ve 2 tetiklenme daha.",
            summary);
    }

    [Fact]
    public async Task Zaman_araligi_kultur_ve_ofsetten_bagimsiz_UTC_yaziliyor()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        var channel = new DiscordChannel(new CapturingHttpClientFactory(handler), Options());
        var from = new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.FromHours(3));

        var message = new NotificationMessage(
            "sınama kuralı",
            AlertRuleType.Threshold,
            "network/core",
            from,
            from.AddMinutes(5),
            [new NotificationLine(string.Empty, 5, 1, "5 dk içinde 120 olay")],
            null);

        await channel.SendAsync(message, Channel(DiscordHook), Token);

        using var body = JsonDocument.Parse(handler.Body!);
        var description = body.RootElement.GetProperty("embeds")[0]
            .GetProperty("description").GetString();

        Assert.Equal("**Zaman Araligi:** 2026-10-19 12:00:00 - 2026-10-19 12:05:00 UTC", description);
    }

    [Fact]
    public async Task Discord_500_donunce_hata_adres_sizdirmiyor_ve_yeniden_denenebilir()
    {
        var channel = new DiscordChannel(
            new CapturingHttpClientFactory(new CapturingHandler(HttpStatusCode.InternalServerError)),
            Options());

        var result = await channel.SendAsync(
            Message([new NotificationLine(string.Empty, 5, 1, "olay")]), Channel(DiscordHook), Token);

        Assert.False(result.Ok);
        Assert.True(result.Retryable);
        Assert.DoesNotContain("XyZgizliDISCORDjetonu9876", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("discord.com", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static AlertingOptions Options() => new()
    {
        ProductBaseUrl = "https://loganalyzer.example",
        ChannelTimeout = TimeSpan.FromSeconds(5),
    };

    private static NotificationMessage Message(IReadOnlyList<NotificationLine> lines) => new(
        "sınama kuralı",
        AlertRuleType.Threshold,
        "network/core",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(5),
        lines,
        null);

    private static ResolvedChannel Channel(string secret) => new(
        new NotificationChannelEntity
        {
            Name = "sınama",
            OwnerGroup = "network/core",
            ChannelType = NotificationChannelType.Discord,
        },
        new ChannelSettings(),
        secret);

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(status);
        }
    }
}
