using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence;

public sealed record TelemetryEvaluation(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("values")] IReadOnlyDictionary<string, string?> Values);

public sealed record TelemetryEvidence(
    [property: JsonPropertyName("feed")] TelemetryResultStatus Feed,
    [property: JsonPropertyName("evaluation")] string Evaluation,
    [property: JsonPropertyName("decisions")] IReadOnlyList<TelemetryEvaluation> Decisions,
    [property: JsonPropertyName("policy_hash")] string? PolicyHash = null);

public sealed record TelemetryInputs(TelemetryResultStatus Status, IReadOnlyList<TelemetryRecord> Records,
    bool Partial, string? Reason);

public sealed class TelemetryEvidenceOptions
{
    public int MaxRecords { get; set; } = 1000;
    public int MaxRelations { get; set; } = 2000;
    public int MaxPages { get; set; } = 10;
    public int MaxBytes { get; set; } = 4 * 1024 * 1024;
    public int TimeoutSeconds { get; set; } = 10;
    public bool Valid() => MaxRecords is >= 1 and <= 5000 && MaxRelations is >= 1 and <= 10000
        && MaxPages is >= 1 and <= 50 && MaxBytes is >= 1 and <= 16 * 1024 * 1024 && TimeoutSeconds is >= 1 and <= 30;
}

/// <summary>Bounded reads shared by the four providers; all access goes through the audited scope gate.</summary>
public static class TelemetryEvidenceReader
{
    public static decimal Nano(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100m;
    public static AccessScope Narrow(AccessScope scope, IReadOnlyList<string> groups) => groups.Count == 0 ? scope
        : AccessScope.ForGroups(scope.Subject, groups.Where(scope.Allows));

    public static async Task<TelemetryInputs> ReadAsync(IScopedQuery query, TelemetrySignal signal, RcaWindow window,
        AccessScope scope, TelemetryEvidenceOptions options, bool baseline, CancellationToken token)
    {
        if (!options.Valid()) throw new ArgumentException("Invalid telemetry evidence budget.", nameof(options));
        var rows = new List<TelemetryRecord>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0; var bytes = 0; var anyFed = false;
        var sources = window.SourceIds.Count == 0 ? new string?[] { null }
            : window.SourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Cast<string?>().ToArray();
        foreach (var source in sources)
        {
            var request = new TelemetryQuery
            {
                Signal = signal, FromNano = Nano(baseline ? window.BaselineFrom : window.From),
                ToNano = Nano(baseline ? window.BaselineTo : window.To), ResourceId = source,
                OwnerGroups = window.OwnerGroups, Limit = Math.Min(100, options.MaxRecords),
            };
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (pages >= options.MaxPages) return new(rows.Count > 0 ? TelemetryResultStatus.Data : TelemetryResultStatus.Empty, rows, true, "BudgetExceeded");
                var result = await query.SearchTelemetryAsync(request, scope, token); pages++;
                if (result.Status == TelemetryResultStatus.Failed) return new(TelemetryResultStatus.Failed, rows, true, result.Error ?? "QueryFailed");
                anyFed |= result.Status != TelemetryResultStatus.NeverFed;
                foreach (var row in result.Records)
                {
                    if (!seen.Add(row.LogicalId)) continue;
                    var size = JsonSerializer.SerializeToUtf8Bytes(row, RawSignalCodec.Json).Length;
                    if (rows.Count >= options.MaxRecords || size > options.MaxBytes - bytes)
                        return new(TelemetryResultStatus.Data, rows, true, "BudgetExceeded");
                    bytes += size; rows.Add(row);
                }
                if (!result.Partial) break;
                if (result.Cursor is null) return new(result.Status, rows, true, result.Error ?? "BudgetExceeded");
                request = request with { Cursor = result.Cursor };
            }
        }
        return new(rows.Count > 0 ? TelemetryResultStatus.Data : anyFed ? TelemetryResultStatus.Empty : TelemetryResultStatus.NeverFed,
            rows, false, null);
    }

    public static string Series(TelemetryRecord row) => row.SeriesKey + "/" + RawSignalEnvelope.Hash(
        JsonSerializer.SerializeToUtf8Bytes(new { row.Resource, row.Scope, row.Kind, row.Unit, row.Temporality, row.Monotonic }, RawSignalCodec.Json));
}
