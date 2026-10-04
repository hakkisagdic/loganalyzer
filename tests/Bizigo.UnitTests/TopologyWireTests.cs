using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.AspNetCore.DataProtection;

namespace Bizigo.UnitTests;

public sealed class TopologyWireTests
{
    [Fact]
    public void Json_and_typescript_exactness()
    {
        var node = new TopologyNodeDto("service:id", "service", "visible", "A", true,
            "9007199254740993", "1760000000000000001", null);
        var body = new TopologyNodePageDto([node], null, false, null, "9007199254740995");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.String, root.GetProperty("published_sequence").ValueKind);
        Assert.Equal("9007199254740995", root.GetProperty("published_sequence").GetString());
        var first = root.GetProperty("nodes")[0];
        Assert.Equal("9007199254740993", first.GetProperty("version").GetString());
        Assert.Equal("1760000000000000001", first.GetProperty("valid_from_unix_nano").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("valid_to_unix_nano").ValueKind);
        Assert.False(root.TryGetProperty("publishedSequence", out _));
    }

    [Fact]
    public void Cursor_scope_binding_rejects_identity_changes()
    {
        var codec = new TopologyReadCursorCodec(new EphemeralDataProtectionProvider());
        var scope = AccessScope.ForGroups("reader-A", ["A"]);
        var state = State(scope);
        var encoded = codec.Encode(state);
        Assert.DoesNotContain("reader-A", encoded, StringComparison.Ordinal);
        var decoded = codec.Decode(encoded);
        TopologyReadCursorCodec.EnsureBound(decoded, "nodes", null, scope);
        Assert.Throws<TopologyCursorWireException>(() => TopologyReadCursorCodec.EnsureBound(
            decoded, "nodes", null, AccessScope.ForGroups("reader-B", ["A"])));
        Assert.Throws<TopologyCursorWireException>(() => TopologyReadCursorCodec.EnsureBound(
            decoded, "nodes", null, AccessScope.ForGroups("reader-A", ["B"])));
        Assert.Throws<TopologyCursorWireException>(() => TopologyReadCursorCodec.EnsureBound(
            decoded, "edges", null, scope));
    }

    [Fact]
    public void Tampered_cursor_is_rejected_before_query()
    {
        var codec = new TopologyReadCursorCodec(new EphemeralDataProtectionProvider());
        var cursor = codec.Encode(State(AccessScope.ForGroups("reader-A", ["A"])));
        var broken = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];
        Assert.Throws<TopologyCursorWireException>(() => codec.Decode(broken));
    }

    [Fact]
    public void Historical_declared_detail_continuation_ignores_valid_to_but_observed_ttl_expires()
    {
        const decimal now = 3000m;
        const decimal historicalValidTo = 2000m;
        const decimal normalCursorExpiry = 3300m;
        var declared = new TopologyEdgeProjection("history", "source:a", "service:b",
            TopologyRelation.DependsOn, TopologyProvenance.Declared, true, 1m, "A", "A",
            TopologyEdgeVisibility.SameOwner, 1000m, 1000m, historicalValidTo, 7, 1, false);
        var observed = declared with { Provenance = TopologyProvenance.Observed };
        Assert.Null(TopologyReadCursorCodec.EvidenceExpiry(declared));
        Assert.Equal(historicalValidTo, TopologyReadCursorCodec.EvidenceExpiry(observed));

        var scope = AccessScope.ForGroups("reader-A", ["A"]);
        var codec = new TopologyReadCursorCodec(new EphemeralDataProtectionProvider());
        TopologyReadCursorState State(decimal validUntil) => new("edge",
            TopologyReadCursorCodec.ScopeBinding("edge", declared.Id, scope.Subject,
                scope.IsUnrestricted, scope.OwnerGroups), "next-evidence", 7, 11,
            1999m, validUntil, 1, null, null, null, null, null, [], 1000m, 1999m);
        var declaredCursor = codec.Decode(codec.Encode(State(
            Math.Min(normalCursorExpiry, TopologyReadCursorCodec.EvidenceExpiry(declared)
                ?? normalCursorExpiry))));
        TopologyReadCursorCodec.EnsureBound(declaredCursor, "edge", declared.Id, scope);
        TopologyCursorFence.EnsureCurrent(new(declaredCursor.PostgresEpoch,
            declaredCursor.ClickHouseWatermark, DateTimeOffset.MaxValue)
        { ExactValidUntilUnixNano = declaredCursor.ValidUntilUnixNano }, new(7, 11), now);

        var observedCursor = codec.Decode(codec.Encode(State(
            Math.Min(normalCursorExpiry, TopologyReadCursorCodec.EvidenceExpiry(observed)!.Value))));
        Assert.Throws<TopologyRestartRequiredException>(() => TopologyCursorFence.EnsureCurrent(
            new(observedCursor.PostgresEpoch, observedCursor.ClickHouseWatermark, DateTimeOffset.MaxValue)
            { ExactValidUntilUnixNano = observedCursor.ValidUntilUnixNano }, new(7, 11), now));
    }

    [Fact]
    public void Competing_eligible_edge_shortens_cursor_before_selected_proof_expires()
    {
        const decimal selectedProofExpiry = 1500m;
        const decimal competingEligibleExpiry = 1100m;
        const decimal normalCursorExpiry = 2000m;
        var deadline = TopologyReadCursorCodec.EarliestExpiry(selectedProofExpiry,
            competingEligibleExpiry);
        Assert.Equal(competingEligibleExpiry, deadline);
        Assert.Equal(selectedProofExpiry, TopologyReadCursorCodec.EarliestExpiry(
            selectedProofExpiry, null));
        Assert.Equal(competingEligibleExpiry, TopologyReadCursorCodec.EarliestExpiry(
            null, competingEligibleExpiry));

        var codec = new TopologyReadCursorCodec(new EphemeralDataProtectionProvider());
        var scope = AccessScope.ForGroups("reader-A", ["A"]);
        var cursor = codec.Decode(codec.Encode(State(scope) with
        {
            ValidUntilUnixNano = Math.Min(normalCursorExpiry, deadline!.Value),
        }));
        TopologyCursorFence.EnsureCurrent(new(cursor.PostgresEpoch,
            cursor.ClickHouseWatermark, DateTimeOffset.MaxValue)
        { ExactValidUntilUnixNano = cursor.ValidUntilUnixNano }, new(7, 11), 1099m);
        Assert.Throws<TopologyRestartRequiredException>(() => TopologyCursorFence.EnsureCurrent(
            new(cursor.PostgresEpoch, cursor.ClickHouseWatermark, DateTimeOffset.MaxValue)
            { ExactValidUntilUnixNano = cursor.ValidUntilUnixNano }, new(7, 11), competingEligibleExpiry));
    }

    private static TopologyReadCursorState State(AccessScope scope) => new("nodes",
        TopologyReadCursorCodec.ScopeBinding("nodes", null, scope.Subject, scope.IsUnrestricted, scope.OwnerGroups),
        "inner", 7, 11, 1760000000000000000m, 1760000000000001000m, 2,
        null, null, null, null, null, [], null, null);
}
