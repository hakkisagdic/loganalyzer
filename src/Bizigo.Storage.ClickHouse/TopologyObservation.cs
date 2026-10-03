using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Storage.ClickHouse;

/// <summary>Envelope-independent, versioned identity for one typed OTLP span.</summary>
public sealed record TopologySpanCandidate(
    string Anchor, string Fingerprint, string TraceId, string SpanId, string ParentSpanId,
    ulong StartNano, ulong EndNano, decimal TraceExpiryNano, decimal ObservedExpiryNano,
    TelemetryRecord Record)
{
    public static TopologySpanCandidate? FromRecord(TelemetryRecord record)
    {
        if (record.Signal != TelemetrySignal.Traces || record.Topology is null) return null;
        if (record.Span is not { ValueKind: JsonValueKind.Object } span
            || record.TopologyBindingsSha256 is not { Length: 64 })
            throw new InvalidDataException("Typed trace lacks its admitted topology snapshot.");
        var snapshot = record.Topology;
        if (snapshot.LeafKey != record.Owner.LeafKey || snapshot.SourceId != record.Owner.SourceId
            || snapshot.OwnerGroup != record.Owner.OwnerGroup
            || snapshot.SourceHistoryRevision != record.Owner.HistoryRevision
            || snapshot.EventTimeUnixNano != record.TimeUnixNano
            || record.RetentionDays is < 1 or > 36500 || record.ObservedRetentionDays is < 1 or > 36500)
            throw new InvalidDataException("Typed span/topology admission snapshot mismatch.");

        var traceId = record.TraceId.ToLowerInvariant();
        var spanId = record.SpanId.ToLowerInvariant();
        if (!HexId(traceId, 32) || !HexId(spanId, 16))
            throw new InvalidDataException("Invalid typed trace/span identity.");
        var parent = String(span, "parentSpanId").ToLowerInvariant();
        if (parent.Length != 0 && !HexId(parent, 16))
            throw new InvalidDataException("Invalid typed parent span identity.");
        var start = Nano(span, "startTimeUnixNano");
        var end = Nano(span, "endTimeUnixNano");
        if (start != record.TimeUnixNano || end < start
            || !string.Equals(String(span, "traceId"), traceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(String(span, "spanId"), spanId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Typed span indexed columns do not match the canonical span.");

        var anchor = TopologyCanonicalIdentity.Hash("trace-span-v1", traceId, spanId);
        var fingerprint = TopologyCanonicalIdentity.Hash("topology-span-v1", traceId, spanId,
            snapshot.SourceId, snapshot.OwnerGroup, Number(snapshot.SourceHistoryRevision),
            snapshot.ServiceNodeId ?? string.Empty, snapshot.InstanceNodeId ?? string.Empty,
            Number(snapshot.ServiceBindingRevision), Number(snapshot.InstanceBindingRevision),
            Number(snapshot.NodeHistoryRevision), snapshot.Reason, parent,
            Number(start), Number(end), record.Kind, Number(record.Status),
            record.ResourceSchemaUrl, record.ScopeSchemaUrl,
            TopologyCanonicalIdentity.Json(record.Resource), TopologyCanonicalIdentity.Json(record.Scope),
            TopologyCanonicalIdentity.Json(span));
        return new(anchor, fingerprint, traceId, spanId, parent, start, end,
            Expiry(start, record.RetentionDays), Expiry(start, record.ObservedRetentionDays), record);
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    private static ulong Nano(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
            || !ulong.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
                NumberStyles.None, CultureInfo.InvariantCulture, out var nano))
            throw new InvalidDataException("Missing or invalid typed span event time.");
        return nano;
    }
    private static bool HexId(string text, int length) => text.Length == length && text.All(Uri.IsHexDigit);
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
    private static decimal Expiry(ulong time, int days) => checked((decimal)time + (decimal)days * 86400000000000m);
}

/// <summary>Canonical tuple length framing prevents delimiter-injection collisions.</summary>
public static class TopologyCanonicalIdentity
{
    public static string Hash(params string[] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static string Json(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var value in element.EnumerateArray()) Write(writer, value);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}

/// <summary>Admission-captured context; never reconstructed from current inventory.</summary>
public sealed record TopologyConflictCandidate(string Fingerprint, string OwnerGroup, string SourceId,
    string? NodeId, ulong EventTimeNano, decimal TraceExpiryNano, decimal ObservedExpiryNano,
    string ParentAnchor, bool IsConflictedAnchor)
{
    // V3 captures the complete admission decision. Earlier marker payloads
    // cannot prove the potential incident edges and require explicit repair.
    public int ContextVersion { get; init; }
    public string Anchor { get; init; } = string.Empty;
    public string ResolutionReason { get; init; } = string.Empty;
}

public sealed record TopologySpanConflict(string Anchor, string FirstFingerprint, string ConflictingFingerprint)
{
    // Includes all fingerprint histories and children referencing this anchor.
    // An old manifest without this property remains explicitly unattributed.
    public IReadOnlyList<TopologyConflictCandidate> Candidates { get; init; } = [];
}

public sealed record TopologyObservedEvent(
    string EdgeId, string TraceId, string ParentSpanId, string ChildSpanId,
    string ParentAnchor, string ChildAnchor, string ParentFingerprint, string ChildFingerprint,
    TopologyLeafBinding Parent, TopologyLeafBinding Child,
    ulong ParentStartNano, ulong ChildStartNano,
    IReadOnlyList<string> ParentOccurrences, IReadOnlyList<string> ChildOccurrences,
    decimal ParentTraceExpiry, decimal ChildTraceExpiry, decimal ObservedExpiry, decimal EffectiveExpiry);

public sealed record TopologyProjectionBatch(string PublicationKey,
    IReadOnlyList<TopologyObservedEvent> Edges, IReadOnlyList<TopologySpanConflict> Conflicts);

/// <summary>Reduces every same-trace typed occurrence before emitting any edge.</summary>
public static class TopologyObservation
{
    private sealed record SpanGroup(TopologySpanCandidate Canonical, IReadOnlyList<string> Occurrences,
        decimal TraceExpiry, decimal ObservedExpiry);

    public static TopologyProjectionBatch Reduce(IReadOnlyList<TelemetryRecord> records)
    {
        var candidates = records.Select(TopologySpanCandidate.FromRecord).Where(x => x is not null)
            .Cast<TopologySpanCandidate>().ToArray();
        if (candidates.Select(x => x.TraceId).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new ArgumentException("Topology reduction requires exactly one trace ID.", nameof(records));
        var conflicts = new List<TopologySpanConflict>();
        var groups = new Dictionary<string, SpanGroup>(StringComparer.Ordinal);
        foreach (var anchor in candidates.GroupBy(x => x.Anchor, StringComparer.Ordinal))
        {
            var fingerprints = anchor.Select(x => x.Fingerprint).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (fingerprints.Length > 1)
            {
                var parentAnchors = anchor.Where(candidate => candidate.ParentSpanId.Length != 0)
                    .Select(candidate => TopologyCanonicalIdentity.Hash("trace-span-v1", candidate.TraceId,
                        candidate.ParentSpanId)).ToHashSet(StringComparer.Ordinal);
                var related = candidates.Where(candidate => candidate.Anchor == anchor.Key
                    || parentAnchors.Contains(candidate.Anchor)
                    || candidate.ParentSpanId.Length != 0 &&
                    TopologyCanonicalIdentity.Hash("trace-span-v1", candidate.TraceId, candidate.ParentSpanId) == anchor.Key);
                var contexts = related.GroupBy(candidate => candidate.Fingerprint, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var candidate = group.OrderBy(x => x.Record.LogicalId, StringComparer.Ordinal).First();
                        var binding = candidate.Record.Topology!;
                        return new TopologyConflictCandidate(candidate.Fingerprint, binding.OwnerGroup,
                            binding.SourceId, binding.NodeId, candidate.StartNano,
                            group.Min(x => x.TraceExpiryNano), group.Min(x => x.ObservedExpiryNano),
                            candidate.ParentSpanId.Length == 0 ? string.Empty :
                                TopologyCanonicalIdentity.Hash("trace-span-v1", candidate.TraceId, candidate.ParentSpanId),
                            candidate.Anchor == anchor.Key)
                        {
                            ContextVersion = 3,
                            Anchor = candidate.Anchor,
                            ResolutionReason = binding.Reason,
                        };
                    }).OrderBy(x => x.Fingerprint, StringComparer.Ordinal).ToArray();
                conflicts.Add(new(anchor.Key, fingerprints[0], fingerprints[1]) { Candidates = contexts });
                continue;
            }
            var ordered = anchor.OrderBy(x => x.Record.LogicalId, StringComparer.Ordinal).ToArray();
            groups.Add(anchor.Key, new(ordered[0], ordered.Select(x => x.Record.LogicalId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                ordered.Min(x => x.TraceExpiryNano), ordered.Min(x => x.ObservedExpiryNano)));
        }

        var edges = new List<TopologyObservedEvent>();
        foreach (var child in groups.Values)
        {
            var childSpan = child.Canonical;
            if (childSpan.ParentSpanId.Length == 0 || !childSpan.Record.Topology!.Resolved) continue;
            var parentAnchor = TopologyCanonicalIdentity.Hash("trace-span-v1", childSpan.TraceId, childSpan.ParentSpanId);
            if (parentAnchor == childSpan.Anchor) continue;
            if (!groups.TryGetValue(parentAnchor, out var parent) || !parent.Canonical.Record.Topology!.Resolved) continue;
            var parentSpan = parent.Canonical;
            var from = parentSpan.Record.Topology!;
            var to = childSpan.Record.Topology!;
            var id = TopologyCanonicalIdentity.Hash("observed-edge-v1", parentSpan.Anchor, childSpan.Anchor,
                parentSpan.Fingerprint, childSpan.Fingerprint, from.NodeId!, to.NodeId!,
                parentSpan.StartNano.ToString(CultureInfo.InvariantCulture),
                childSpan.StartNano.ToString(CultureInfo.InvariantCulture), TopologyEdgeRelations.DependsOn);
            var effective = Math.Min(parent.TraceExpiry, Math.Min(child.TraceExpiry, child.ObservedExpiry));
            edges.Add(new(id, childSpan.TraceId, parentSpan.SpanId, childSpan.SpanId,
                parentSpan.Anchor, childSpan.Anchor, parentSpan.Fingerprint, childSpan.Fingerprint,
                from, to, parentSpan.StartNano, childSpan.StartNano, parent.Occurrences, child.Occurrences,
                parent.TraceExpiry, child.TraceExpiry, child.ObservedExpiry, effective));
        }

        edges.Sort((a, b) => string.CompareOrdinal(a.EdgeId, b.EdgeId));
        conflicts.Sort((a, b) => string.CompareOrdinal(a.Anchor, b.Anchor));
        // A new publication version avoids reusing an immutable v1 manifest
        // whose conflict row lacked admission-captured context.
        var parts = new List<string> { "topology-publication-v3" };
        foreach (var candidate in candidates.DistinctBy(x => (x.Anchor, x.Fingerprint, x.Record.LogicalId))
                     .OrderBy(x => x.Anchor, StringComparer.Ordinal)
                     .ThenBy(x => x.Fingerprint, StringComparer.Ordinal).ThenBy(x => x.Record.LogicalId, StringComparer.Ordinal))
        {
            parts.Add(candidate.Anchor); parts.Add(candidate.Fingerprint); parts.Add(candidate.Record.LogicalId);
        }
        foreach (var edge in edges) parts.Add(edge.EdgeId);
        foreach (var conflict in conflicts)
        { parts.Add(conflict.Anchor); parts.Add(conflict.FirstFingerprint); parts.Add(conflict.ConflictingFingerprint); }
        return new(TopologyCanonicalIdentity.Hash(parts.ToArray()), edges, conflicts);
    }
}
