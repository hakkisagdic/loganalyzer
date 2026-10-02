using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace Bizigo.Api;

/// <summary>
/// The HTTP continuation is authenticated and opaque. The inner query cursor
/// carries a stable key; this envelope binds that key to the caller, filters,
/// two-store revision and the exact nanosecond expiry boundary.
/// </summary>
public sealed class TopologyReadCursorCodec(IDataProtectionProvider protection)
{
    private const int Version = 1;
    private readonly IDataProtector _protector = protection.CreateProtector("Bizigo.Topology.Cursor.v1");

    public string Encode(TopologyReadCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Inner)) throw new ArgumentException("Missing inner topology cursor.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(Version, state));
        return Convert.ToBase64String(_protector.Protect(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public TopologyReadCursorState Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        if (encoded.Length > 4096) throw new TopologyCursorWireException();
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(bytes));
            if (payload is null || payload.Version != Version || payload.State is null
                || string.IsNullOrWhiteSpace(payload.State.Inner)) throw new TopologyCursorWireException();
            return payload.State;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        { throw new TopologyCursorWireException(); }
    }

    public static string ScopeBinding(string route, string? routeId, string subject,
        bool unrestricted, IEnumerable<string> groups)
    {
        var input = string.Join('\u001e', [route, routeId ?? "", subject, unrestricted ? "1" : "0",
            string.Join('\u001f', groups.Order(StringComparer.Ordinal))]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static void EnsureBound(TopologyReadCursorState state, string route, string? routeId,
        AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(scope);
        var expected = ScopeBinding(route, routeId, scope.Subject, scope.IsUnrestricted, scope.OwnerGroups);
        if (!string.Equals(state.Route, route, StringComparison.Ordinal)
            || !string.Equals(state.ScopeBinding, expected, StringComparison.Ordinal))
            throw new TopologyCursorWireException();
    }

    private sealed record Payload(int Version, TopologyReadCursorState State);
}

public sealed record TopologyReadCursorState(
    string Route,
    string ScopeBinding,
    string Inner,
    long PostgresEpoch,
    ulong ClickHouseWatermark,
    decimal AsOfUnixNano,
    decimal ValidUntilUnixNano,
    int Limit,
    string? Kind,
    string? Relation,
    string? Provenance,
    string? FromNode,
    string? ToNode,
    IReadOnlyList<string> Targets,
    decimal? FromUnixNano,
    decimal? ToUnixNano);

public sealed class TopologyCursorWireException : ArgumentException
{
    public TopologyCursorWireException() : base("Invalid topology cursor.") { }
}
