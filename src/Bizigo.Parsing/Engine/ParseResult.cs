using Bizigo.Contracts;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Engine;

/// <summary>
/// Parser motorunun çıktısı. Burada <b>bilerek</b> <see cref="LogEvent"/> yok:
/// <c>core</c> → OCSF/OTel türetmesi ve olay nesnesinin kurulması T07'nin işi.
/// Motor "alan sözlüğü üretir", depolama şemasını tanımaz.
/// </summary>
public sealed record ParseResult
{
    public required string ParserId { get; init; }

    public required string ParserVersion { get; init; }

    public required ParseStatus Status { get; init; }

    /// <summary>Boru hattının ürettiği ham alan sözlüğü.</summary>
    public required IReadOnlyDictionary<string, object?> Fields { get; init; }

    /// <summary><c>map.core</c> sonucu — sıcak kolonların karşılığı.</summary>
    public IReadOnlyDictionary<string, object?> Core { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Ocsf { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Otel { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<ParseIssue> Issues { get; init; } = [];

    /// <summary><c>date</c> adımının çözdüğü olay zamanı.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// En az bir grok adımı zaman aşımına uğradı. Çağıran taraf bunu
    /// <see cref="ParserQuarantine"/>'e bildirmelidir (F1 §4.1 kademe 3).
    /// </summary>
    public bool TimedOut { get; init; }

    public static ParseResult Failure(string parserId, string parserVersion, string reason) => new()
    {
        ParserId = parserId,
        ParserVersion = parserVersion,
        Status = ParseStatus.Failed,
        Fields = new Dictionary<string, object?>(StringComparer.Ordinal),
        Issues = [new ParseIssue("engine", reason)],
    };
}
