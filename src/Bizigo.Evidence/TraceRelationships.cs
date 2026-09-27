using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Evidence;

public sealed record TraceRelation(string TraceId, string ParentId, string ChildId);
public sealed record TraceServiceRelation(string FromService, string ToService, IReadOnlyList<TraceRelation> Support);
public sealed record TraceRelationshipResult(IReadOnlyList<TraceRelation> ErrorEdges,
    IReadOnlyList<IReadOnlyList<string>> ErrorPaths, IReadOnlyList<TraceServiceRelation> Services,
    IReadOnlyList<TraceRelation> LinkedObservations, int SelfRelations, IReadOnlyList<string> Reasons)
{
    public bool Partial => Reasons.Count != 0;
}

/// <summary>Only supplied, scoped spans can prove a relation. Links are observations, not parent causality.</summary>
public static class TraceRelationships
{
    public static TraceRelationshipResult Analyze(IReadOnlyList<TelemetryRecord> records, int maxRelations = 2000)
    {
        if (maxRelations is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxRelations));
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, TelemetryRecord>(StringComparer.Ordinal);
        foreach (var group in records.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var all = group.ToArray();
            if (all.Any(r => r.Signal != TelemetrySignal.Traces || r.Span is null))
            { reasons.Add("InvalidSpan"); continue; }
            var first = all[0];
            if (all.Any(r => r.Span!.Value.GetRawText() != first.Span!.Value.GetRawText()
                || r.ServiceName != first.ServiceName || r.Owner.OwnerGroup != first.Owner.OwnerGroup))
            { reasons.Add("ConflictingSpanId"); continue; }
            nodes[group.Key] = first;
        }
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var links = new List<TraceRelation>();
        var relationCount = 0;
        foreach (var (key, row) in nodes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var span = row.Span!.Value;
            var parent = Text(span, "parentSpanId");
            if (!string.IsNullOrEmpty(parent))
            {
                if (++relationCount > maxRelations) { reasons.Add("BudgetExceeded"); break; }
                var parentKey = row.TraceId + "/" + parent;
                if (!nodes.ContainsKey(parentKey)) reasons.Add("MissingParent");
                else parents[key] = parentKey;
            }
            if (!span.TryGetProperty("links", out var related)) continue;
            foreach (var link in related.EnumerateArray())
            {
                if (++relationCount > maxRelations) { reasons.Add("BudgetExceeded"); break; }
                var trace = Text(link, "traceId"); var linked = Text(link, "spanId");
                if (nodes.ContainsKey(trace + "/" + linked)) links.Add(new(row.TraceId, row.SpanId, trace + "/" + linked));
                else reasons.Add("UnresolvedReference");
            }
        }
        var cyclic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in parents.Keys)
        {
            var path = new List<string>(); var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = node;
            while (parents.TryGetValue(current, out var parent))
            {
                if (seen.TryGetValue(current, out var begin))
                { cyclic.UnionWith(path.Skip(begin)); reasons.Add("Cycle"); break; }
                seen[current] = path.Count; path.Add(current); current = parent;
            }
        }
        var errors = new List<TraceRelation>();
        var services = new Dictionary<(string From, string To), List<TraceRelation>>();
        var self = 0;
        foreach (var (childKey, parentKey) in parents.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (cyclic.Contains(childKey) || cyclic.Contains(parentKey)) continue;
            var child = nodes[childKey]; var parent = nodes[parentKey];
            var relation = new TraceRelation(parent.TraceId, parent.SpanId, child.SpanId);
            if (parent.Status == 2 && child.Status == 2) errors.Add(relation);
            if (parent.ServiceName == child.ServiceName) { self++; continue; }
            if (string.IsNullOrEmpty(parent.ServiceName) || string.IsNullOrEmpty(child.ServiceName))
            { reasons.Add("MissingService"); continue; }
            var pair = (parent.ServiceName, child.ServiceName);
            if (!services.TryGetValue(pair, out var support)) services[pair] = support = [];
            support.Add(relation);
        }
        errors = errors.OrderBy(e => e.TraceId, StringComparer.Ordinal).ThenBy(e => e.ParentId, StringComparer.Ordinal)
            .ThenBy(e => e.ChildId, StringComparer.Ordinal).ToList();
        var paths = new List<IReadOnlyList<string>>();
        foreach (var trace in errors.GroupBy(e => e.TraceId))
        {
            var children = trace.GroupBy(e => e.ParentId).ToDictionary(g => g.Key, g => g.Select(e => e.ChildId).ToArray(), StringComparer.Ordinal);
            var destinations = trace.Select(e => e.ChildId).ToHashSet(StringComparer.Ordinal);
            foreach (var root in children.Keys.Where(k => !destinations.Contains(k)).Order(StringComparer.Ordinal))
            {
                var work = new Stack<string[]>(); work.Push([root]);
                while (work.TryPop(out var path))
                {
                    if (!children.TryGetValue(path[^1], out var next))
                    { paths.Add(path.Select(id => trace.Key + "/" + id).ToArray()); continue; }
                    foreach (var child in next.Reverse()) work.Push([.. path, child]);
                }
            }
        }
        return new(errors, paths,
            services.OrderBy(p => p.Key.From, StringComparer.Ordinal).ThenBy(p => p.Key.To, StringComparer.Ordinal)
                .Select(p => new TraceServiceRelation(p.Key.From, p.Key.To, p.Value)).ToArray(),
            links.OrderBy(l => l.TraceId, StringComparer.Ordinal).ThenBy(l => l.ParentId, StringComparer.Ordinal)
                .ThenBy(l => l.ChildId, StringComparer.Ordinal).ToArray(), self, reasons.ToArray());
    }

    private static string Key(TelemetryRecord row) => row.TraceId + "/" + row.SpanId;
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
}
