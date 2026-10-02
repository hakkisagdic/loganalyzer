using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
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

    private static TopologyReadCursorState State(AccessScope scope) => new("nodes",
        TopologyReadCursorCodec.ScopeBinding("nodes", null, scope.Subject, scope.IsUnrestricted, scope.OwnerGroups),
        "inner", 7, 11, 1760000000000000000m, 1760000000000001000m, 2,
        null, null, null, null, null, [], null, null);
}
