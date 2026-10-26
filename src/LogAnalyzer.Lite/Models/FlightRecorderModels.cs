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

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "Inbound";

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "Info"; // Info, Warning, Error, Fatal

    [JsonPropertyName("isIncident")]
    public bool IsIncident { get; init; }

    [JsonPropertyName("isBusinessFailure")]
    public bool IsBusinessFailure { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "IntegrationGateway";

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = string.Empty;

    [JsonPropertyName("httpMethod")]
    public string HttpMethod { get; init; } = "POST";

    [JsonPropertyName("httpStatus")]
    public int HttpStatus { get; init; } = 200;

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("requestHeaders")]
    public string? RequestHeaders { get; init; }

    [JsonPropertyName("requestPayload")]
    public string? RequestPayload { get; init; }

    [JsonPropertyName("responsePayload")]
    public string? ResponsePayload { get; init; }

    [JsonPropertyName("requestBytes")]
    public long RequestBytes { get; init; }

    [JsonPropertyName("responseBytes")]
    public long ResponseBytes { get; init; }

    [JsonPropertyName("requestSha256")]
    public string? RequestSha256 { get; init; }

    [JsonPropertyName("responseSha256")]
    public string? ResponseSha256 { get; init; }

    [JsonPropertyName("requestCaptureComplete")]
    public bool RequestCaptureComplete { get; init; } = true;

    [JsonPropertyName("responseCaptureComplete")]
    public bool ResponseCaptureComplete { get; init; } = true;

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public record IngestRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; init; }

    [JsonPropertyName("trackingId")]
    public string? TrackingId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

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

    [JsonPropertyName("isBusinessFailure")]
    public bool IsBusinessFailure { get; init; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("requestHeaders")]
    public string? RequestHeaders { get; init; }

    [JsonPropertyName("requestPayload")]
    public string? RequestPayload { get; init; }

    [JsonPropertyName("responsePayload")]
    public string? ResponsePayload { get; init; }

    [JsonPropertyName("requestBytes")]
    public long RequestBytes { get; init; }

    [JsonPropertyName("responseBytes")]
    public long ResponseBytes { get; init; }

    [JsonPropertyName("requestSha256")]
    public string? RequestSha256 { get; init; }

    [JsonPropertyName("responseSha256")]
    public string? ResponseSha256 { get; init; }

    [JsonPropertyName("requestCaptureComplete")]
    public bool? RequestCaptureComplete { get; init; }

    [JsonPropertyName("responseCaptureComplete")]
    public bool? ResponseCaptureComplete { get; init; }

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

    [JsonPropertyName("storedEvents")]
    public int StoredEvents { get; init; }

    [JsonPropertyName("inboundCount")]
    public long InboundCount { get; init; }

    [JsonPropertyName("outboundCount")]
    public long OutboundCount { get; init; }

    [JsonPropertyName("purgedCount")]
    public long PurgedCount { get; init; }

    [JsonPropertyName("diskWriteFailures")]
    public long DiskWriteFailures { get; init; }

    [JsonPropertyName("retentionHours")]
    public int RetentionHours { get; init; }

    [JsonPropertyName("maxEvents")]
    public int MaxEvents { get; init; }

    [JsonPropertyName("avgDurationMs")]
    public double AvgDurationMs { get; init; }

    [JsonPropertyName("uptimeSeconds")]
    public long UptimeSeconds { get; init; }

    [JsonPropertyName("lastIncidentTime")]
    public DateTime? LastIncidentTime { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "Lite (Full Traffic & Incident Flight Recorder - Spool Active)";
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
