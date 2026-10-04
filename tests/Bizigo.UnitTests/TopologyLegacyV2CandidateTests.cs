using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

public sealed class TopologyLegacyV2CandidateTests
{
    [Fact]
    public void Complete_candidate_reproduces_frozen_v2_key_and_rowset_without_certifying_v4()
    {
        var trace = Guid.NewGuid().ToString("N");
        var start = 1_800_000_000_000_000_000UL;
        var parent = Span(trace, "0011223344556677", "", "parent", start, 90);
        var child = Span(trace, "8899aabbccddeeff", "0011223344556677", "child", start + 1000, 10);
        var archived = new[] { parent, child };
        var frozen = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2(archived);
        var hash = TopologyObservedProjector.ComputeFrozenRowsetHash(frozen);

        TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(frozen, hash, archived);
        Assert.Equal(0, frozen.ProjectionVersion);
        Assert.Single(frozen.Edges);
        Assert.Empty(frozen.ParentResolutions);
    }

    [Fact]
    public void Missing_archived_occurrence_cannot_reproduce_frozen_v2_key()
    {
        var trace = Guid.NewGuid().ToString("N");
        var parent = Span(trace, "0011223344556677", "", "parent", 1_800_000_000_000_000_000UL, 90);
        var child = Span(trace, "8899aabbccddeeff", "0011223344556677", "child",
            1_800_000_000_000_001_000UL, 90);
        var frozen = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([parent, child]);

        Assert.Throws<InvalidDataException>(() => TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(
            frozen, TopologyObservedProjector.ComputeFrozenRowsetHash(frozen), [child]));
    }

    [Fact]
    public void Changed_archived_retention_keeps_semantic_key_but_fails_frozen_captured_ttl()
    {
        var trace = Guid.NewGuid().ToString("N");
        var parent = Span(trace, "0011223344556677", "", "parent", 1_800_000_000_000_000_000UL, 90);
        var child = Span(trace, "8899aabbccddeeff", "0011223344556677", "child",
            1_800_000_000_000_001_000UL, 90);
        var frozen = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([parent, child]);
        var shortChild = child with { RetentionDays = 10, ObservedRetentionDays = 10 };
        Assert.Equal(frozen.PublicationKey,
            TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([parent, shortChild]).PublicationKey);

        Assert.Throws<InvalidDataException>(() => TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(
            frozen, TopologyObservedProjector.ComputeFrozenRowsetHash(frozen), [parent, shortChild]));
    }

    [Fact]
    public void Captured_child_without_parent_has_uncertified_missing_parent_candidate()
    {
        var child = Span(Guid.NewGuid().ToString("N"), "8899aabbccddeeff", "0011223344556677",
            "child", 1_800_000_000_000_000_000UL, 90);
        var frozen = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([child]);
        TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(frozen,
            TopologyObservedProjector.ComputeFrozenRowsetHash(frozen), [child]);
        var candidate = TopologyObservation.Reduce([child]);

        Assert.Empty(frozen.Edges);
        Assert.Equal("MissingParent", Assert.Single(candidate.ParentResolutions).Reason);
    }

    [Fact]
    public void Conflicting_span_candidate_context_is_not_frozen_v2_authority()
    {
        var trace = Guid.NewGuid().ToString("N");
        var first = Span(trace, "0011223344556677", "", "first", 1_800_000_000_000_000_000UL, 90);
        var second = Span(trace, "0011223344556677", "", "second", 1_800_000_000_000_000_000UL, 90);
        var frozen = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([first, second]);
        TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(frozen,
            TopologyObservedProjector.ComputeFrozenRowsetHash(frozen), [first, second]);
        var candidate = TopologyObservation.Reduce([first, second]);

        Assert.Empty(candidate.Edges);
        var conflict = Assert.Single(candidate.Conflicts);
        Assert.Equal(frozen.Conflicts[0].Anchor, conflict.Anchor);
        Assert.Equal(2, conflict.Candidates.Count);
        Assert.All(conflict.Candidates, candidate =>
        {
            Assert.Equal(3, candidate.ContextVersion);
            Assert.Equal(conflict.Anchor, candidate.Anchor);
            Assert.Equal("Resolved", candidate.ResolutionReason);
        });
    }

    [Fact]
    public void Same_frozen_key_and_rows_do_not_bind_missing_parent_observed_expiry()
    {
        var trace = Guid.NewGuid().ToString("N");
        const ulong start = 1_800_000_000_000_000_000UL;
        var parent = Span(trace, "0011223344556677", "ffeeddccbbaa9988", "parent", start, 90);
        var child = Span(trace, "8899aabbccddeeff", "0011223344556677", "child", start + 1000, 90);
        var shortObserved = parent with { ObservedRetentionDays = 1,
            TopologyBindingsSha256 = BindingHash(parent, 1) };
        var longObserved = parent with { ObservedRetentionDays = 90,
            TopologyBindingsSha256 = BindingHash(parent, 90) };
        var original = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([shortObserved, child]);
        var alternative = TopologyLegacyV2CandidateVerifier.ReconstructFrozenV2([longObserved, child]);
        var frozenHash = TopologyObservedProjector.ComputeFrozenRowsetHash(original);

        Assert.Equal(original.PublicationKey, alternative.PublicationKey);
        Assert.Equal(frozenHash, TopologyObservedProjector.ComputeFrozenRowsetHash(alternative));
        Assert.NotEqual(shortObserved.TopologyBindingsSha256, longObserved.TopologyBindingsSha256);
        TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(original, frozenHash, [shortObserved, child]);
        TopologyLegacyV2CandidateVerifier.VerifyFrozenCandidate(original, frozenHash, [longObserved, child]);
        var shortDecision = TopologyObservation.Reduce([shortObserved, child]).ParentResolutions
            .Single(item => item.ChildAnchor == TopologyCanonicalIdentity.Hash("trace-span-v1", trace,
                "0011223344556677"));
        var longDecision = TopologyObservation.Reduce([longObserved, child]).ParentResolutions
            .Single(item => item.ChildAnchor == shortDecision.ChildAnchor);
        Assert.Equal("MissingParent", shortDecision.Reason);
        Assert.Equal("MissingParent", longDecision.Reason);
        Assert.NotEqual(shortDecision.ChildExpiryNano, longDecision.ChildExpiryNano);
    }

    private static TelemetryRecord Span(string traceId, string spanId, string parentSpanId,
        string leafKey, ulong start, int retentionDays)
    {
        var envelopeId = Guid.NewGuid();
        var source = "source-" + leafKey;
        var node = TopologyIdentity.Node(TopologyNodeKind.Service, Guid.NewGuid());
        var span = Json($$"""
            {"traceId":"{{traceId}}","spanId":"{{spanId}}","parentSpanId":"{{parentSpanId}}",
             "startTimeUnixNano":"{{start.ToString(CultureInfo.InvariantCulture)}}",
             "endTimeUnixNano":"{{(start + 1).ToString(CultureInfo.InvariantCulture)}}",
             "name":"op","kind":2}
            """);
        var owner = new TelemetryOwnerBinding(leafKey, source, "A", 7, start, "known");
        var binding = new TopologyLeafBinding(leafKey, start, source, "A", 7,
            node, null, 5, null, 9, "display", "Resolved");
        return new(1, envelopeId, envelopeId.ToString("N") + "/" + leafKey, TelemetrySignal.Traces,
            new string('a', 64), new string('b', 64), owner, start, "op", "svc", "Client", "", 0, false,
            traceId, spanId, 1, new string('c', 64), Json("{}"), Json("{}"), "", "", null, span)
        {
            RetentionDays = retentionDays,
            ObservedRetentionDays = retentionDays,
            Topology = binding,
            TopologyBindingsSha256 = new string('d', 64),
        };
    }

    private static string BindingHash(TelemetryRecord record, int observedRetentionDays)
    {
        var payload = Encoding.UTF8.GetBytes("{}");
        var envelope = new RawSignalEnvelope(RawSignalEnvelope.CurrentVersion, record.EnvelopeId,
            TelemetrySignal.Traces, "application/json", DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(payload), payload, 1, [record.Owner.LeafKey], 0)
        {
            OwnerBindings = [record.Owner], TopologyBindings = [record.Topology!],
            RetentionDays = record.RetentionDays, ObservedRetentionDays = observedRetentionDays,
        };
        envelope = envelope with { OwnerBindingsSha256 = envelope.ComputeOwnerBindingsHash() };
        return envelope.ComputeTopologyBindingsHash();
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
