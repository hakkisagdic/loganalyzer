using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.Extensions.Options;

namespace Bizigo.Evidence.Providers;

public sealed class TraceErrorPropagationProvider(IScopedQuery query, IOptions<TelemetryEvidenceOptions> options) : IEvidenceProvider
{
    public string Id => "traces.error-propagation";
    public EvidenceKind Kind => EvidenceKind.Trace;
    public bool IsAvailable => true;
    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken) =>
        TraceProviderOutput.GatherAsync(query, options.Value, Id, true, window, scope, budget, cancellationToken);
}

public sealed class TraceServiceDependencyProvider(IScopedQuery query, IOptions<TelemetryEvidenceOptions> options) : IEvidenceProvider
{
    public string Id => "traces.service-dependency";
    public EvidenceKind Kind => EvidenceKind.Trace;
    public bool IsAvailable => true;
    public Task<EvidenceSlice> GatherAsync(RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken) =>
        TraceProviderOutput.GatherAsync(query, options.Value, Id, false, window, scope, budget, cancellationToken);
}

internal static class TraceProviderOutput
{
    internal static async Task<EvidenceSlice> GatherAsync(IScopedQuery query, TelemetryEvidenceOptions options, string id, bool errors,
        RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var input = await TelemetryEvidenceReader.ReadAsync(query, TelemetrySignal.Traces, window, scope, options, false, timeout.Token);
        if (input.Status == TelemetryResultStatus.Failed) return new()
        {
            ProviderId = id, Kind = EvidenceKind.Trace, Status = EvidenceStatus.Failed, Detail = input.Reason ?? "QueryFailed",
            Telemetry = new(input.Status, "Failed", []),
        };
        var graph = TraceRelationships.Analyze(input.Records, options.MaxRelations);
        var items = new List<EvidenceItem>();
        void Item(string key, string summary, string json, string trace)
        {
            items.Add(new(RawSignalEnvelope.Hash(System.Text.Encoding.UTF8.GetBytes(id + key)), id,
                EvidenceKind.Trace, window.From, 1, summary, new Dictionary<string, string>
                {
                    ["observation"] = json,
                    ["drilldown"] = "/v1/traces/" + trace + "?from_nano=" + TelemetryEvidenceReader.Nano(window.From).ToString(CultureInfo.InvariantCulture)
                        + "&to_nano=" + TelemetryEvidenceReader.Nano(window.To).ToString(CultureInfo.InvariantCulture),
                }));
        }
        if (errors)
        {
            foreach (var path in graph.ErrorPaths)
                Item(string.Join(",", path), "Related ERROR spans; direction is parent relation, not proof of root cause.",
                    JsonSerializer.Serialize(path), path[0].Split('/')[0]);
        }
        else
        {
            foreach (var edge in graph.Services)
                Item(edge.FromService + "/" + edge.ToService, edge.FromService + " → " + edge.ToService + " observed parent relation",
                    JsonSerializer.Serialize(edge, BundleSerializer.Options), edge.Support[0].TraceId);
        }
        var reasons = graph.Reasons.Concat(input.Partial ? new[] { input.Reason ?? "BudgetExceeded" } : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var partial = input.Partial || graph.Partial || items.Count > budget.MaxItems;
        var status = items.Count > 0 ? EvidenceStatus.Gathered : partial ? EvidenceStatus.Unavailable
            : input.Status == TelemetryResultStatus.NeverFed ? EvidenceStatus.NeverFed : EvidenceStatus.Empty;
        return new()
        {
            ProviderId = id, Kind = EvidenceKind.Trace, Status = status, Items = items.Take(budget.MaxItems).ToArray(),
            Detail = string.Join(",", reasons), Truncated = partial,
            Telemetry = new(input.Status, partial ? "NotComparable" : input.Status == TelemetryResultStatus.NeverFed ? "NotRun" : "Evaluated",
                [new("graph", partial ? "Partial" : "Complete", string.Join(",", reasons), new Dictionary<string, string?>
                {
                    ["error_edges"] = JsonSerializer.Serialize(graph.ErrorEdges, BundleSerializer.Options),
                    ["linked_observations"] = JsonSerializer.Serialize(graph.LinkedObservations, BundleSerializer.Options),
                    ["self_relations"] = graph.SelfRelations.ToString(CultureInfo.InvariantCulture),
                    ["from"] = window.From.ToString("O", CultureInfo.InvariantCulture), ["to"] = window.To.ToString("O", CultureInfo.InvariantCulture),
                })]),
        };
    }
}
