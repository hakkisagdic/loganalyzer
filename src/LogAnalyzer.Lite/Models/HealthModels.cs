using System.Text.Json.Serialization;

namespace LogAnalyzer.Lite.Models;

/// <summary>
/// <c>/health</c> yanıtı. Sürüm ve commit dağıtımın hangi kodu koşturduğunu
/// doğrulamak için; başka hiçbir yapılandırma (belirteç, köken, dizin)
/// burada yansıtılmaz.
/// </summary>
internal sealed record HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "Healthy";

    [JsonPropertyName("product")]
    public string Product { get; init; } = "LogAnalyzer Lite";

    [JsonPropertyName("component")]
    public string Component { get; init; } = "Incident Flight Recorder";

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("commit")]
    public string Commit { get; init; } = "unknown";

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }
}
