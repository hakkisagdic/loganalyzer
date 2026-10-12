using System;
using System.Text.Json.Serialization;

namespace LogAnalyzer.Lite.Models;

public record AlertWebhookConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("slackWebhookUrl")]
    public string? SlackWebhookUrl { get; init; }

    [JsonPropertyName("discordWebhookUrl")]
    public string? DiscordWebhookUrl { get; init; }

    [JsonPropertyName("customWebhookUrl")]
    public string? CustomWebhookUrl { get; init; }

    [JsonPropertyName("minLevel")]
    public string MinLevel { get; init; } = "Error"; // Error, Fatal, Warning

    [JsonPropertyName("notifyOnSlaBreach")]
    public bool NotifyOnSlaBreach { get; init; } = true;

    [JsonPropertyName("slaThresholdMs")]
    public double SlaThresholdMs { get; init; } = 3000;

    [JsonPropertyName("cooldownSeconds")]
    public int CooldownSeconds { get; init; } = 60;
}

public record AlertTestRequest
{
    [JsonPropertyName("target")]
    public string Target { get; init; } = "all"; // slack, discord, all

    [JsonPropertyName("customMessage")]
    public string? CustomMessage { get; init; }
}

public record AlertTestResponse
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
