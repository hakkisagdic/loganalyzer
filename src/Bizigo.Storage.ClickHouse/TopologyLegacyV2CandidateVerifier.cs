using System.Globalization;
using Bizigo.Contracts;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Pure compatibility check for a candidate frozen v2 archive. This does NOT
/// prove that the supplied archive was the historical admission corpus. In
/// particular, v2 rows do not bind every captured observed retention value;
/// callers must not use this result to publish v4 or issue a Ready certificate.
/// </summary>
public static class TopologyLegacyV2CandidateVerifier
{
    private sealed record SpanGroup(TopologySpanCandidate Canonical, IReadOnlyList<string> Occurrences,
        decimal TraceExpiry, decimal ObservedExpiry);

    public static void VerifyFrozenCandidate(TopologyProjectionBatch original,
        string originalRowsetSha256, IReadOnlyList<TelemetryRecord> archivedRecords)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalRowsetSha256);
        ArgumentNullException.ThrowIfNull(archivedRecords);
        if (original.ProjectionVersion != 0 || original.ParentResolutions.Count != 0)
            throw new InvalidDataException("Only a frozen v2 topology publication can use this verifier.");
        if (originalRowsetSha256.Length != 64
            || originalRowsetSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid frozen v2 rowset checksum.");
        if (archivedRecords.Count == 0 || archivedRecords.Count > 100_000
            || archivedRecords.Any(record => record.Signal != TelemetrySignal.Traces
                || record.Topology is null || string.IsNullOrWhiteSpace(record.TopologyBindingsSha256))
            || archivedRecords.Select(record => record.LogicalId).Distinct(StringComparer.Ordinal).Count()
                != archivedRecords.Count)
            throw new InvalidDataException("The typed archive candidate set is invalid or duplicated.");

        var frozen = ReconstructFrozenV2(archivedRecords);
        if (!string.Equals(original.PublicationKey, frozen.PublicationKey, StringComparison.Ordinal)
            || original.Edges.Count != frozen.Edges.Count || original.Conflicts.Count != frozen.Conflicts.Count
            || !original.Edges.Select(edge => edge.EdgeId)
                .SequenceEqual(frozen.Edges.Select(edge => edge.EdgeId), StringComparer.Ordinal)
            || !original.Conflicts.Select(conflict => (conflict.Anchor, conflict.FirstFingerprint,
                    conflict.ConflictingFingerprint))
                .SequenceEqual(frozen.Conflicts.Select(conflict => (conflict.Anchor, conflict.FirstFingerprint,
                    conflict.ConflictingFingerprint)))
            || TopologyObservedProjector.ComputeFrozenRowsetHash(original) != originalRowsetSha256
            || TopologyObservedProjector.ComputeFrozenRowsetHash(frozen) != originalRowsetSha256)
            throw new InvalidDataException("Archived typed trace does not reproduce the immutable v2 publication.");

        // A successful comparison proves only that the candidate is
        // compatible with what v2 persisted. It cannot authenticate captured
        // context absent from v2, so there is deliberately no upgrade output.
    }

    /// <summary>Exact d32b3be v2 reduction, including its length-framed key.</summary>
    public static TopologyProjectionBatch ReconstructFrozenV2(IReadOnlyList<TelemetryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var candidates = records.Select(TopologySpanCandidate.FromRecord).Where(candidate => candidate is not null)
            .Cast<TopologySpanCandidate>().ToArray();
        if (candidates.Length == 0 || candidates.Select(candidate => candidate.TraceId)
            .Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("Frozen v2 reconstruction requires one complete typed trace.");

        var conflicts = new List<TopologySpanConflict>();
        var groups = new Dictionary<string, SpanGroup>(StringComparer.Ordinal);
        foreach (var anchor in candidates.GroupBy(candidate => candidate.Anchor, StringComparer.Ordinal))
        {
            var fingerprints = anchor.Select(candidate => candidate.Fingerprint).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            if (fingerprints.Length > 1)
            {
                // This related-candidate rule is deliberately frozen v2, not
                // today's broader v4 conflict attribution.
                var related = candidates.Where(candidate => candidate.Anchor == anchor.Key
                    || candidate.ParentSpanId.Length != 0
                    && TopologyCanonicalIdentity.Hash("trace-span-v1", candidate.TraceId,
                        candidate.ParentSpanId) == anchor.Key);
                var contexts = related.GroupBy(candidate => candidate.Fingerprint, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        var candidate = group.OrderBy(item => item.Record.LogicalId, StringComparer.Ordinal).First();
                        var binding = candidate.Record.Topology!;
                        return new TopologyConflictCandidate(candidate.Fingerprint, binding.OwnerGroup,
                            binding.SourceId, binding.NodeId, candidate.StartNano,
                            group.Min(item => item.TraceExpiryNano), group.Min(item => item.ObservedExpiryNano),
                            candidate.ParentSpanId.Length == 0 ? string.Empty :
                                TopologyCanonicalIdentity.Hash("trace-span-v1", candidate.TraceId,
                                    candidate.ParentSpanId), candidate.Anchor == anchor.Key);
                    }).OrderBy(candidate => candidate.Fingerprint, StringComparer.Ordinal).ToArray();
                conflicts.Add(new(anchor.Key, fingerprints[0], fingerprints[1]) { Candidates = contexts });
                continue;
            }
            var ordered = anchor.OrderBy(candidate => candidate.Record.LogicalId, StringComparer.Ordinal).ToArray();
            groups.Add(anchor.Key, new(ordered[0], ordered.Select(candidate => candidate.Record.LogicalId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                ordered.Min(candidate => candidate.TraceExpiryNano),
                ordered.Min(candidate => candidate.ObservedExpiryNano)));
        }

        var edges = new List<TopologyObservedEvent>();
        foreach (var child in groups.Values)
        {
            var childSpan = child.Canonical;
            if (childSpan.ParentSpanId.Length == 0 || !childSpan.Record.Topology!.Resolved) continue;
            var parentAnchor = TopologyCanonicalIdentity.Hash("trace-span-v1", childSpan.TraceId,
                childSpan.ParentSpanId);
            if (parentAnchor == childSpan.Anchor) continue;
            if (!groups.TryGetValue(parentAnchor, out var parent) || !parent.Canonical.Record.Topology!.Resolved)
                continue;
            var parentSpan = parent.Canonical;
            var from = parentSpan.Record.Topology!;
            var to = childSpan.Record.Topology!;
            var edgeId = TopologyCanonicalIdentity.Hash("observed-edge-v1", parentSpan.Anchor, childSpan.Anchor,
                parentSpan.Fingerprint, childSpan.Fingerprint, from.NodeId!, to.NodeId!,
                parentSpan.StartNano.ToString(CultureInfo.InvariantCulture),
                childSpan.StartNano.ToString(CultureInfo.InvariantCulture), TopologyEdgeRelations.DependsOn);
            var effective = Math.Min(parent.TraceExpiry, Math.Min(child.TraceExpiry, child.ObservedExpiry));
            edges.Add(new(edgeId, childSpan.TraceId, parentSpan.SpanId, childSpan.SpanId,
                parentSpan.Anchor, childSpan.Anchor, parentSpan.Fingerprint, childSpan.Fingerprint,
                from, to, parentSpan.StartNano, childSpan.StartNano, parent.Occurrences, child.Occurrences,
                parent.TraceExpiry, child.TraceExpiry, child.ObservedExpiry, effective));
        }
        edges.Sort((left, right) => string.CompareOrdinal(left.EdgeId, right.EdgeId));
        conflicts.Sort((left, right) => string.CompareOrdinal(left.Anchor, right.Anchor));

        var fields = new List<string> { "topology-publication-v2" };
        foreach (var candidate in candidates.DistinctBy(item =>
                     (item.Anchor, item.Fingerprint, item.Record.LogicalId))
                     .OrderBy(item => item.Anchor, StringComparer.Ordinal)
                     .ThenBy(item => item.Fingerprint, StringComparer.Ordinal)
                     .ThenBy(item => item.Record.LogicalId, StringComparer.Ordinal))
        {
            fields.Add(candidate.Anchor);
            fields.Add(candidate.Fingerprint);
            fields.Add(candidate.Record.LogicalId);
        }
        foreach (var edge in edges) fields.Add(edge.EdgeId);
        foreach (var conflict in conflicts)
        {
            fields.Add(conflict.Anchor);
            fields.Add(conflict.FirstFingerprint);
            fields.Add(conflict.ConflictingFingerprint);
        }
        return new(TopologyCanonicalIdentity.Hash(fields.ToArray()), edges, conflicts);
    }

}
