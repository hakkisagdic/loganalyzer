using System.Globalization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LogAnalyzer.Lite.Models;

namespace LogAnalyzer.Lite.Services;

public class AlertWebhookNotifier
{
    private readonly HttpClient _httpClient;
    private readonly string _configFile;
    private readonly object _configLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastNotified = new();
    private AlertWebhookConfig _config;

    public AlertWebhookNotifier(HttpClient? httpClient = null, string? storagePath = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var dir = storagePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _configFile = Path.Combine(dir, "alerts-config.json");

        _config = LoadConfig();
    }

    public AlertWebhookConfig GetConfig()
    {
        lock (_configLock)
        {
            return _config;
        }
    }

    public void UpdateConfig(AlertWebhookConfig newConfig)
    {
        lock (_configLock)
        {
            _config = newConfig;
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configFile, json);
            }
            catch
            {
                // File write error fallback
            }
        }
    }

    private AlertWebhookConfig LoadConfig()
    {
        try
        {
            if (File.Exists(_configFile))
            {
                var json = File.ReadAllText(_configFile);
                var cfg = JsonSerializer.Deserialize<AlertWebhookConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Fallback to default
        }

        return new AlertWebhookConfig();
    }

    public void NotifyIncident(IncidentRecord incident)
    {
        var cfg = GetConfig();
        if (!cfg.Enabled) return;

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

        // Asenkron ateşle (Fire & Forget, ana akışı asla yavaşlatmaz)
        _ = Task.Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(cfg.SlackWebhookUrl))
            {
                await SendSlackIncidentAsync(cfg.SlackWebhookUrl, incident);
            }

            if (!string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
            {
                await SendDiscordIncidentAsync(cfg.DiscordWebhookUrl, incident);
            }
        });
    }

    public async Task<AlertTestResponse> TestAlertAsync(AlertTestRequest request)
    {
        var cfg = GetConfig();
        bool? slackOk = null;
        string? slackErr = null;
        bool? discordOk = null;
        string? discordErr = null;

        var message = request.CustomMessage ?? "Bu LogAnalyzer Incident Flight Recorder tarafından gönderilen bir TEST alarmıdır. Sistem bildirim kanalları kusursuz çalışıyor.";

        if ((request.Target == "slack" || request.Target == "all") && !string.IsNullOrWhiteSpace(cfg.SlackWebhookUrl))
        {
            try
            {
                var payload = new { text = $"🔔 *[LogAnalyzer Lite Test Alarmı]*\n{message}\n*Zaman:* {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC" };
                var res = await _httpClient.PostAsync(cfg.SlackWebhookUrl, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                slackOk = res.IsSuccessStatusCode;
                if (!slackOk.Value) slackErr = $"HTTP {(int)res.StatusCode}: {res.ReasonPhrase}";
            }
            catch (Exception ex)
            {
                slackOk = false;
                slackErr = ex.Message;
            }
        }

        if ((request.Target == "discord" || request.Target == "all") && !string.IsNullOrWhiteSpace(cfg.DiscordWebhookUrl))
        {
            try
            {
                var payload = new
                {
                    content = "🔔 **[LogAnalyzer Lite Test Alarmı]**",
                    embeds = new[]
                    {
                        new
                        {
                            title = "Kanal Doğrulama Testi",
                            description = message,
                            color = 3447003, // Mavi
                            fields = new[]
                            {
                                new { name = "Durum", value = "Başarılı / Aktif", inline = true },
                                new { name = "Kanal Tipi", value = "Discord Webhook", inline = true },
                                new { name = "Zaman (UTC)", value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), inline = true }
                            },
                            footer = new { text = "LogAnalyzer Observability Platform" }
                        }
                    }
                };
                var res = await _httpClient.PostAsync(cfg.DiscordWebhookUrl, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                discordOk = res.IsSuccessStatusCode;
                if (!discordOk.Value) discordErr = $"HTTP {(int)res.StatusCode}: {res.ReasonPhrase}";
            }
            catch (Exception ex)
            {
                discordOk = false;
                discordErr = ex.Message;
            }
        }

        return new AlertTestResponse
        {
            SlackSuccess = slackOk,
            SlackError = slackErr,
            DiscordSuccess = discordOk,
            DiscordError = discordErr,
            Message = "Test çağrısı tamamlandı."
        };
    }

    private async Task SendSlackIncidentAsync(string url, IncidentRecord incident)
    {
        try
        {
            var isFatal = incident.HttpStatus >= 500 || incident.Level == "Fatal";
            var icon = isFatal ? "🚨" : "⚠️";
            var payload = new
            {
                text = $"{icon} *[LogAnalyzer Incident]* {incident.Level.ToUpperInvariant()} Tespit Edildi!\n" +
                       $"• *Tracking ID:* `{incident.TrackingId}`\n" +
                       $"• *Kaynak:* {incident.Source} | *Durum:* `{incident.HttpStatus}` | *Süre:* {Math.Round(incident.DurationMs)} ms\n" +
                       $"• *Uç Nokta:* `{incident.HttpMethod} {incident.Endpoint}`\n" +
                       $"• *Hata Mesajı:* {incident.ErrorMessage ?? "Belirtilmedi"}\n" +
                       $"• *Zaman:* {incident.Timestamp:yyyy-MM-dd HH:mm:ss} UTC"
            };

            await _httpClient.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Webhook send failure should never throw
        }
    }

    private async Task SendDiscordIncidentAsync(string url, IncidentRecord incident)
    {
        try
        {
            var isFatal = incident.HttpStatus >= 500 || incident.Level == "Fatal";
            var color = isFatal ? 15158332 : 15105570; // Red or Orange
            var payload = new
            {
                content = $"🚨 **[LogAnalyzer Lite Incident Alarmı]** `{incident.TrackingId}`",
                embeds = new[]
                {
                    new
                    {
                        title = $"{incident.Level.ToUpperInvariant()}: {incident.HttpMethod} {incident.Endpoint}",
                        description = incident.ErrorMessage ?? "Bilinmeyen Hata",
                        color = color,
                        fields = new[]
                        {
                            new { name = "Durum Kodu", value = incident.HttpStatus.ToString(CultureInfo.InvariantCulture), inline = true },
                            new { name = "Kaynak Servis", value = incident.Source, inline = true },
                            new { name = "Gecikme (Latency)", value = $"{Math.Round(incident.DurationMs)} ms", inline = true },
                            new { name = "Tracking ID", value = incident.TrackingId, inline = true },
                            new { name = "Client IP", value = incident.ClientIp ?? "127.0.0.1", inline = true },
                            new { name = "Zaman (UTC)", value = incident.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), inline = true }
                        },
                        footer = new { text = "LogAnalyzer Incident Flight Recorder" },
                        timestamp = incident.Timestamp.ToString("o", CultureInfo.InvariantCulture)
                    }
                }
            };

            await _httpClient.PostAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        }
        catch
        {
            // Webhook send failure should never throw
        }
    }
}
