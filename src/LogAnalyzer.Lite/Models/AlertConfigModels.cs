using System.Text.Json.Serialization;

namespace LogAnalyzer.Lite.Models;

/// <summary>
/// <b>Depolama tipi</b> — <c>alerts-config.json</c>'ın şekli. Tel sözleşmesi
/// DEĞİL: webhook adresleri sır, dışarı yalnızca <see cref="AlertConfigView"/>
/// çıkıyor. Alan adları eski dosyayla uyumlu kalsın diye değişmedi; eski
/// sürümün yazdığı <c>customWebhookUrl</c> okunurken yok sayılıyor.
/// </summary>
internal sealed record AlertWebhookConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("slackWebhookUrl")]
    public string? SlackWebhookUrl { get; init; }

    [JsonPropertyName("discordWebhookUrl")]
    public string? DiscordWebhookUrl { get; init; }

    [JsonPropertyName("minLevel")]
    public string MinLevel { get; init; } = "Error"; // Error, Fatal, Warning

    [JsonPropertyName("notifyOnSlaBreach")]
    public bool NotifyOnSlaBreach { get; init; } = true;

    [JsonPropertyName("slaThresholdMs")]
    public double SlaThresholdMs { get; init; } = 3000;

    [JsonPropertyName("cooldownSeconds")]
    public int CooldownSeconds { get; init; } = 60;
}

/// <summary>
/// <c>POST /api/v1/alerts/config</c> gövdesi.
///
/// <para>
/// Webhook alanlarının üç hâli var: boş / <c>null</c> / maske → kayıtlı sır
/// korunur; <c>clear*</c> → silinir; başka bir değer → doğrulanır ve yazılır.
/// Arayüz sırrı hiç görmediği için "değiştirmedim" ile "sildim" ayrı
/// söylenmek zorunda.
/// </para>
/// </summary>
internal sealed record AlertConfigUpdateRequest
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("slackWebhookUrl")]
    public string? SlackWebhookUrl { get; init; }

    [JsonPropertyName("discordWebhookUrl")]
    public string? DiscordWebhookUrl { get; init; }

    [JsonPropertyName("clearSlackWebhook")]
    public bool ClearSlackWebhook { get; init; }

    [JsonPropertyName("clearDiscordWebhook")]
    public bool ClearDiscordWebhook { get; init; }

    /// <summary>
    /// Hiç kullanılmayan eski alan. Boş olmayan değer reddediliyor: sessizce
    /// yok saymak, operatöre "kaydedildi" deyip hiçbir yere bildirim
    /// göndermemek olurdu.
    /// </summary>
    [JsonPropertyName("customWebhookUrl")]
    public string? CustomWebhookUrl { get; init; }

    [JsonPropertyName("minLevel")]
    public string? MinLevel { get; init; }

    [JsonPropertyName("notifyOnSlaBreach")]
    public bool? NotifyOnSlaBreach { get; init; }

    [JsonPropertyName("slaThresholdMs")]
    public double? SlaThresholdMs { get; init; }

    [JsonPropertyName("cooldownSeconds")]
    public int? CooldownSeconds { get; init; }
}

/// <summary><c>GET /api/v1/alerts/config</c> yanıtı — sırlar maskeli.</summary>
internal sealed record AlertConfigView
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>Kayıtlıysa sabit bir maske, değilse <c>null</c>. Sırrın hiçbir parçası değil.</summary>
    [JsonPropertyName("slackWebhookUrl")]
    public string? SlackWebhookUrl { get; init; }

    [JsonPropertyName("slackConfigured")]
    public bool SlackConfigured { get; init; }

    [JsonPropertyName("discordWebhookUrl")]
    public string? DiscordWebhookUrl { get; init; }

    [JsonPropertyName("discordConfigured")]
    public bool DiscordConfigured { get; init; }

    [JsonPropertyName("minLevel")]
    public string MinLevel { get; init; } = "Error";

    [JsonPropertyName("notifyOnSlaBreach")]
    public bool NotifyOnSlaBreach { get; init; }

    [JsonPropertyName("slaThresholdMs")]
    public double SlaThresholdMs { get; init; }

    [JsonPropertyName("cooldownSeconds")]
    public int CooldownSeconds { get; init; }
}

internal sealed record AlertConfigUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("config")]
    public AlertConfigView? Config { get; init; }
}

internal sealed record AlertTestRequest
{
    [JsonPropertyName("target")]
    public string Target { get; init; } = "all"; // slack, discord, all

    [JsonPropertyName("customMessage")]
    public string? CustomMessage { get; init; }
}

/// <summary>
/// Test sonucu. Hata alanları yalnızca <see cref="WebhookFailure"/>
/// kategorilerinden birini taşır: karşı tarafın durum metni, istisna mesajı
/// ya da adres yansıtılmaz — yansıtılan her ayrıntı bir yanıt kâhini olur.
/// </summary>
internal sealed record AlertTestResponse
{
    [JsonPropertyName("slackSuccess")]
    public bool? SlackSuccess { get; init; }

    [JsonPropertyName("slackError")]
    public string? SlackError { get; init; }

    [JsonPropertyName("discordSuccess")]
    public bool? DiscordSuccess { get; init; }

    [JsonPropertyName("discordError")]
    public string? DiscordError { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>Webhook teslim hatasının genel kategorileri.</summary>
internal static class WebhookFailure
{
    public const string NotConfigured = "not_configured";
    public const string Timeout = "timeout";
    public const string Unreachable = "unreachable";
    public const string Rejected = "rejected";
}

internal sealed record ApiError
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
