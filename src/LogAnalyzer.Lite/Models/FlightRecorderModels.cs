using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LogAnalyzer.Lite.Models;

public record IncidentRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("trackingId")]
    public string TrackingId { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "Error"; // Error, Fatal, Warning, SLA_Breach

    [JsonPropertyName("source")]
    public string Source { get; init; } = "IntegrationGateway";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("httpMethod")]
    public string HttpMethod { get; init; } = "POST";

    [JsonPropertyName("httpStatus")]
    public int HttpStatus { get; init; } = 500;

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("requestPayload")]
    public string? RequestPayload { get; init; }

    [JsonPropertyName("responsePayload")]
    public string? ResponsePayload { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public record IngestRequest
{
    [JsonPropertyName("trackingId")]
    public string? TrackingId { get; init; }

    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    [JsonPropertyName("httpMethod")]
    public string? HttpMethod { get; init; }

    [JsonPropertyName("httpStatus")]
    public int? HttpStatus { get; init; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("requestPayload")]
    public string? RequestPayload { get; init; }

    [JsonPropertyName("responsePayload")]
    public string? ResponsePayload { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

public record IngestResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; } = true;

    [JsonPropertyName("trackingId")]
    public string TrackingId { get; init; } = string.Empty;

    [JsonPropertyName("isIncident")]
    public bool IsIncident { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

public record FlightRecorderStats
{
    [JsonPropertyName("totalIngested")]
    public long TotalIngested { get; init; }

    [JsonPropertyName("successfulRequests")]
    public long SuccessfulRequests { get; init; }

    [JsonPropertyName("errorCount")]
    public long ErrorCount { get; init; }

    [JsonPropertyName("activeIncidents")]
    public int ActiveIncidents { get; init; }

    [JsonPropertyName("purgedCount")]
    public long PurgedCount { get; init; }

    [JsonPropertyName("avgDurationMs")]
    public double AvgDurationMs { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; init; }

    [JsonPropertyName("lastIncidentTime")]
    public DateTime? LastIncidentTime { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "Lite (Incident Flight Recorder)";
}

public record PurgeRequest
{
    [JsonPropertyName("keepLastHours")]
    public int KeepLastHours { get; init; } = 24;

    [JsonPropertyName("resetStats")]
    public bool ResetStats { get; init; } = false;
}

public record PurgeResponse
{
    [JsonPropertyName("purgedIncidents")]
    public int PurgedIncidents { get; init; }

    [JsonPropertyName("remainingIncidents")]
    public int RemainingIncidents { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
