using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LogAnalyzer.Lite.Models;

namespace LogAnalyzer.Lite.Services;

/// <summary>Yapılandırma güncellemesinin sonucu: ya yeni görünüm ya da bir 400 gerekçesi.</summary>
internal sealed record AlertConfigUpdateResult(AlertConfigView? View, ApiError? Error);

internal sealed class AlertWebhookNotifier
{
    public const string HttpClientName = "lite-alert-webhooks";
    public const string ConfigFileName = "alerts-config.json";
    public const int MaxCustomMessageLength = 500;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] Targets = ["slack", "discord", "all"];
    private static readonly string[] Levels = ["Error", "Fatal", "Warning"];
    private static readonly JsonSerializerOptions StorageJson = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LiteStorage _storage;
    private readonly ILogger<AlertWebhookNotifier> _logger;
    private readonly object _configLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new(StringComparer.Ordinal);
    private AlertWebhookConfig _config;

    public AlertWebhookNotifier(IHttpClientFactory httpClientFactory, LiteStorage storage, ILogger<AlertWebhookNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _storage = storage;
        _logger = logger;
        _config = LoadConfig();
    }

    /// <summary>
    /// Bildirim istemcisinin birincil işleyicisi. Yönlendirme KAPALI: izin
    /// listesindeki bir adres 30x ile iç ağa yönlendiremesin. Çerez yok.
    /// </summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    public AlertConfigView GetConfigView()
    {
        lock (_configLock)
        {
            return ToView(_config);
        }
    }

    public AlertConfigUpdateResult TryUpdate(AlertConfigUpdateRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CustomWebhookUrl))
        {
            return Invalid("custom_webhook_unsupported",
                "Özel webhook desteklenmiyor; yalnızca Slack ve Discord webhook adresleri kullanılabilir.");
        }

        string? minLevel = null;
        if (request.MinLevel is not null)
        {
            minLevel = Levels.FirstOrDefault(l => string.Equals(l, request.MinLevel, StringComparison.OrdinalIgnoreCase));
            if (minLevel is null)
            {
                return Invalid("invalid_min_level", "Minimum seviye Error, Fatal ya da Warning olmalı.");
            }
        }

        if (request.CooldownSeconds is < 0 or > 86_400)
        {
            return Invalid("invalid_cooldown", "Cooldown 0 ile 86400 saniye arasında olmalı.");
        }

        if (request.SlaThresholdMs is { } sla && (double.IsNaN(sla) || sla < 0 || sla > 600_000))
        {
            return Invalid("invalid_sla_threshold", "SLA eşiği 0 ile 600000 ms arasında olmalı.");
        }

        lock (_configLock)
        {
            if (!TryResolveWebhook(WebhookKind.Slack, request.SlackWebhookUrl, request.ClearSlackWebhook, _config.SlackWebhookUrl, out var slack, out var slackError))
            {
                return new AlertConfigUpdateResult(null, slackError);
            }

            if (!TryResolveWebhook(WebhookKind.Discord, request.DiscordWebhookUrl, request.ClearDiscordWebhook, _config.DiscordWebhookUrl, out var discord, out var discordError))
            {
                return new AlertConfigUpdateResult(null, discordError);
            }

            var updated = _config with
            {
                Enabled = request.Enabled ?? _config.Enabled,
                SlackWebhookUrl = slack,
                DiscordWebhookUrl = discord,
                MinLevel = minLevel ?? _config.MinLevel,
                NotifyOnSlaBreach = request.NotifyOnSlaBreach ?? _config.NotifyOnSlaBreach,
                SlaThresholdMs = request.SlaThresholdMs ?? _config.SlaThresholdMs,
                CooldownSeconds = request.CooldownSeconds ?? _config.CooldownSeconds,
            };

            // Önce disk, sonra bellek: yazılamayan bir ayar "kaydedildi" denip
            // yeniden başlatmada kaybolmasın.
            _storage.WriteAtomic(ConfigFileName, JsonSerializer.Serialize(updated, StorageJson));
            _config = updated;

            return new AlertConfigUpdateResult(ToView(updated), null);
        }
    }

    /// <summary>
    /// Bir webhook alanının yeni değeri. Boş / maske → kayıtlı değer korunur;
    /// <paramref name="clear"/> → silinir; başka her şey izin listesinden geçer.
    /// </summary>
    private static bool TryResolveWebhook(
        WebhookKind kind,
        string? submitted,
        bool clear,
        string? stored,
        out string? result,
        out ApiError? error)
    {
        result = stored;
        error = null;

        var keep = string.IsNullOrWhiteSpace(submitted)
            || string.Equals(submitted.Trim(), WebhookUrlPolicy.Mask(kind), StringComparison.Ordinal);

        if (clear)
        {
            if (!keep)
            {
                error = new ApiError
                {
                    Error = kind == WebhookKind.Slack ? "conflicting_slack_webhook" : "conflicting_discord_webhook",
                    Message = "Aynı istekte hem yeni adres hem silme istenemez.",
                };
                return false;
            }

            result = null;
            return true;
        }

        if (keep)
        {
            return true;
        }

        if (!WebhookUrlPolicy.TryValidate(kind, submitted, out var uri))
        {
            error = new ApiError
            {
                Error = kind == WebhookKind.Slack ? "invalid_slack_webhook" : "invalid_discord_webhook",
                Message = WebhookUrlPolicy.Requirement(kind),
            };
            return false;
        }

        result = uri!.AbsoluteUri;
        return true;
    }

    private static AlertConfigUpdateResult Invalid(string code, string message) =>
        new(null, new ApiError { Error = code, Message = message });

    private static AlertConfigView ToView(AlertWebhookConfig config) => new()
    {
        Enabled = config.Enabled,
        SlackConfigured = config.SlackWebhookUrl is not null,
        SlackWebhookUrl = config.SlackWebhookUrl is not null ? WebhookUrlPolicy.SlackMask : null,
        DiscordConfigured = config.DiscordWebhookUrl is not null,
        DiscordWebhookUrl = config.DiscordWebhookUrl is not null ? WebhookUrlPolicy.DiscordMask : null,
        MinLevel = config.MinLevel,
        NotifyOnSlaBreach = config.NotifyOnSlaBreach,
        SlaThresholdMs = config.SlaThresholdMs,
        CooldownSeconds = config.CooldownSeconds,
    };

    /// <summary>
    /// Diskteki yapılandırmayı okur ve <b>adresleri yeniden doğrular</b>.
    /// Eski sürüm her URL'yi kabul ediyordu; birimde kalmış bir iç adres
    /// yükseltmeden sonra da her olayda POST alırdı. İzin listesinden
    /// geçmeyen adres "tanımsız" sayılır ve bir sonraki kayıtta düşer.
    /// </summary>
    private AlertWebhookConfig LoadConfig()
    {
        string? json;

        try
        {
            json = _storage.ReadText(ConfigFileName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Alert configuration '{ConfigFileName}' cannot be read.", error);
        }

        if (json is null)
        {
            return new AlertWebhookConfig();
        }

        AlertWebhookConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<AlertWebhookConfig>(json);
        }
        catch (JsonException)
        {
            var quarantined = _storage.Quarantine(ConfigFileName);
            _logger.LogError("Alert configuration could not be parsed; moved aside as {File}. Alerts start with defaults.", quarantined);
            return new AlertWebhookConfig();
        }

        if (config is null)
        {
            return new AlertWebhookConfig();
        }

        return config with
        {
            SlackWebhookUrl = Revalidate(WebhookKind.Slack, config.SlackWebhookUrl),
            DiscordWebhookUrl = Revalidate(WebhookKind.Discord, config.DiscordWebhookUrl),
            MinLevel = Levels.FirstOrDefault(l => string.Equals(l, config.MinLevel, StringComparison.OrdinalIgnoreCase)) ?? "Error",
        };
    }

    private string? Revalidate(WebhookKind kind, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (WebhookUrlPolicy.TryValidate(kind, stored, out var uri))
        {
            return uri!.AbsoluteUri;
        }

        // Adres bir sır ve izin listesi dışında olduğu için loglanmıyor.
        _logger.LogWarning("Stored {Kind} webhook address is not an allowed {Kind} webhook; it is ignored until reconfigured.", kind, kind);
        return null;
    }

    public void NotifyIncident(IncidentRecord incident)
    {
        var cfg = GetConfigSnapshot();
        if (!cfg.Enabled || (cfg.SlackWebhookUrl is null && cfg.DiscordWebhookUrl is null)) return;

        // Seviye filtresi
        if (string.Equals(cfg.MinLevel, "Fatal", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(incident.Level, "Fatal", StringComparison.OrdinalIgnoreCase) &&
            incident.HttpStatus < 500)
        {
            return;
        }

        // Rate limiting (cooldown by endpoint & error signature)
        var dedupKey = $"{incident.Endpoint}:{incident.HttpStatus}:{incident.ErrorMessage}";
        var now = DateTime.UtcNow;
        if (_lastNotified.TryGetValue(dedupKey, out var lastTime) && (now - lastTime).TotalSeconds < cfg.CooldownSeconds)
        {
            return; // Cooldown süresi içinde tekrar bildirim gönderme (spam önleme)
        }
        _lastNotified[dedupKey] = now;
        PruneCooldowns(now, cfg.CooldownSeconds);

        // Asenkron ateşle (Fire & Forget, ana akışı asla yavaşlatmaz)
        _ = Task.Run(async () =>
        {
            if (cfg.SlackWebhookUrl is { } slack)
            {
                await SendAsync(WebhookKind.Slack, slack, SlackIncidentPayload(incident), CancellationToken.None);
            }

            if (cfg.DiscordWebhookUrl is { } discord)
            {
                await SendAsync(WebhookKind.Discord, discord, DiscordIncidentPayload(incident), CancellationToken.None);
            }
        });
    }

    public async Task<AlertTestResponse> TestAlertAsync(AlertTestRequest request, CancellationToken cancellationToken)
    {
        var cfg = GetConfigSnapshot();
        var target = request.Target;
        var message = string.IsNullOrWhiteSpace(request.CustomMessage)
            ? "Bu LogAnalyzer Incident Flight Recorder tarafından gönderilen bir TEST alarmıdır. Sistem bildirim kanalları kusursuz çalışıyor."
            : request.CustomMessage;

        bool? slackOk = null;
        string? slackErr = null;
        bool? discordOk = null;
        string? discordErr = null;

        if (target is "slack" or "all")
        {
            slackErr = cfg.SlackWebhookUrl is { } slack
                ? await SendAsync(WebhookKind.Slack, slack, new
                {
                    text = $"🔔 *[LogAnalyzer Lite Test Alarmı]*\n{EscapeSlack(message)}\n*Zaman:* {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
                }, cancellationToken)
                : WebhookFailure.NotConfigured;
            slackOk = slackErr is null;
        }

        if (target is "discord" or "all")
        {
            discordErr = cfg.DiscordWebhookUrl is { } discord
                ? await SendAsync(WebhookKind.Discord, discord, new
                {
                    content = "🔔 **[LogAnalyzer Lite Test Alarmı]**",
                    allowed_mentions = new { parse = Array.Empty<string>() },
                    embeds = new[]
                    {
                        new
                        {
                            title = "Kanal Doğrulama Testi",
                            description = Truncate(message, 4000),
                            color = 3447003, // Mavi
                            fields = new[]
                            {
                                new { name = "Durum", value = "Başarılı / Aktif", inline = true },
                                new { name = "Kanal Tipi", value = "Discord Webhook", inline = true },
                                new { name = "Zaman (UTC)", value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), inline = true },
                            },
                            footer = new { text = "LogAnalyzer Observability Platform" },
                        },
                    },
                }, cancellationToken)
                : WebhookFailure.NotConfigured;
            discordOk = discordErr is null;
        }

        return new AlertTestResponse
        {
            SlackSuccess = slackOk,
            SlackError = slackErr,
            DiscordSuccess = discordOk,
            DiscordError = discordErr,
            Message = "Test çağrısı tamamlandı.",
        };
    }

    /// <returns>Geçerli test hedefi değilse 400 gerekçesi, geçerliyse <c>null</c>.</returns>
    public static ApiError? ValidateTestRequest(AlertTestRequest request)
    {
        if (!Targets.Contains(request.Target, StringComparer.Ordinal))
        {
            return new ApiError { Error = "invalid_target", Message = "Hedef slack, discord ya da all olmalı." };
        }

        if (request.CustomMessage is { Length: > MaxCustomMessageLength })
        {
            return new ApiError
            {
                Error = "custom_message_too_long",
                Message = string.Create(CultureInfo.InvariantCulture, $"Test mesajı en fazla {MaxCustomMessageLength} karakter olabilir."),
            };
        }

        return null;
    }

    private AlertWebhookConfig GetConfigSnapshot()
    {
        lock (_configLock)
        {
            return _config;
        }
    }

    /// <summary>
    /// Tek gönderim yolu. Adres son kez izin listesinden geçiyor (bellekteki
    /// değer de diskten gelmiş olabilir). Dönen değer yalnızca bir
    /// <see cref="WebhookFailure"/> kategorisi; ayrıntı — durum metni, istisna
    /// mesajı, adres — ne yanıta ne loga gidiyor: adresin yolu sırrın kendisi.
    /// </summary>
    private async Task<string?> SendAsync(WebhookKind kind, string url, object payload, CancellationToken cancellationToken)
    {
        if (!WebhookUrlPolicy.TryValidate(kind, url, out var uri))
        {
            return WebhookFailure.NotConfigured;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(uri, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            _logger.LogWarning("{Kind} webhook rejected the notification with HTTP {Status}.", kind, (int)response.StatusCode);
            return WebhookFailure.Rejected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{Kind} webhook timed out.", kind);
            return WebhookFailure.Timeout;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException)
        {
            _logger.LogWarning("{Kind} webhook unreachable ({ErrorType}).", kind, error.GetType().Name);
            return WebhookFailure.Unreachable;
        }
    }

    /// <summary>
    /// Benzersiz hata imzaları sınırsız birikmesin: ingest dışarıdan beslenen
    /// bir kanal ve her farklı mesaj bir anahtar.
    /// </summary>
    private void PruneCooldowns(DateTime now, int cooldownSeconds)
    {
        if (_lastNotified.Count <= 1024)
        {
            return;
        }

        foreach (var (key, at) in _lastNotified)
        {
            if ((now - at).TotalSeconds >= cooldownSeconds)
            {
                _lastNotified.TryRemove(key, out _);
            }
        }
    }

    private static object SlackIncidentPayload(IncidentRecord incident)
    {
        var isFatal = incident.HttpStatus >= 500 || incident.Level == "Fatal";
        var icon = isFatal ? "🚨" : "⚠️";

        return new
        {
            text = $"{icon} *[LogAnalyzer Incident]* {EscapeSlack(incident.Level.ToUpperInvariant())} Tespit Edildi!\n" +
                   $"• *Tracking ID:* `{EscapeSlack(incident.TrackingId)}`\n" +
                   $"• *Kaynak:* {EscapeSlack(incident.Source)} | *Durum:* `{incident.HttpStatus.ToString(CultureInfo.InvariantCulture)}` | *Süre:* {Math.Round(incident.DurationMs).ToString(CultureInfo.InvariantCulture)} ms\n" +
                   $"• *Uç Nokta:* `{EscapeSlack(incident.HttpMethod)} {EscapeSlack(incident.Endpoint)}`\n" +
                   $"• *Hata Mesajı:* {EscapeSlack(Truncate(incident.ErrorMessage ?? "Belirtilmedi", 1500))}\n" +
                   $"• *Zaman:* {incident.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC",
        };
    }

    private static object DiscordIncidentPayload(IncidentRecord incident)
    {
        var isFatal = incident.HttpStatus >= 500 || incident.Level == "Fatal";
        var color = isFatal ? 15158332 : 15105570; // Red or Orange

        return new
        {
            content = $"🚨 **[LogAnalyzer Lite Incident Alarmı]** `{Truncate(incident.TrackingId, 100)}`",
            // Olay metni dışarıdan geliyor: @everyone / rol etiketleri ping atmasın.
            allowed_mentions = new { parse = Array.Empty<string>() },
            embeds = new[]
            {
                new
                {
                    title = Truncate($"{incident.Level.ToUpperInvariant()}: {incident.HttpMethod} {incident.Endpoint}", 256),
                    description = Truncate(incident.ErrorMessage ?? "Bilinmeyen Hata", 4000),
                    color,
                    fields = new[]
                    {
                        new { name = "Durum Kodu", value = incident.HttpStatus.ToString(CultureInfo.InvariantCulture), inline = true },
                        new { name = "Kaynak Servis", value = Truncate(incident.Source, 1024), inline = true },
                        new { name = "Gecikme (Latency)", value = $"{Math.Round(incident.DurationMs).ToString(CultureInfo.InvariantCulture)} ms", inline = true },
                        new { name = "Tracking ID", value = Truncate(incident.TrackingId, 1024), inline = true },
                        new { name = "Client IP", value = Truncate(incident.ClientIp ?? "-", 1024), inline = true },
                        new { name = "Zaman (UTC)", value = incident.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), inline = true },
                    },
                    footer = new { text = "LogAnalyzer Incident Flight Recorder" },
                    timestamp = incident.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                },
            },
        };
    }

    /// <summary>Slack mrkdwn'da <c>&amp; &lt; &gt;</c> kontrol karakteri: <c>&lt;!channel&gt;</c> gibi etiketler kaçsın.</summary>
    private static string EscapeSlack(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
