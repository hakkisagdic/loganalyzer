using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;
using Microsoft.AspNetCore.DataProtection;

namespace Bizigo.Api;

/// <summary>
/// The HTTP continuation is authenticated and opaque. The inner query cursor
/// carries a stable key; this envelope binds that key to the caller, filters,
/// two-store revision and the exact nanosecond expiry boundary.
/// </summary>
public sealed class TopologyReadCursorCodec(IDataProtectionProvider protection)
{
    private const int LegacyVersion = 1;
    private const int Version = 2;
    // Keep the existing protector purpose so a valid v1 envelope can be
    // distinguished from tampering and reported as a revision restart.
    private readonly IDataProtector _protector = protection.CreateProtector("Bizigo.Topology.Cursor.v1");

    /// <summary>
    /// A declared edge's effective end is its historical validTo, not an
    /// observed-evidence TTL. Only observed expiry can shorten a continuation.
    /// </summary>
    public static decimal? EvidenceExpiry(TopologyEdgeProjection edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Provenance == TopologyProvenance.Observed ? edge.EffectiveExpiry : null;
    }

    /// <summary>
    /// A competing eligible observed edge can change the answer before the
    /// selected proof expires. Neither deadline is exposed on the wire.
    /// </summary>
    public static decimal? EarliestExpiry(decimal? selectedProof, decimal? eligibleSnapshot) =>
        selectedProof is decimal proof
            ? eligibleSnapshot is decimal eligible ? Math.Min(proof, eligible) : proof
            : eligibleSnapshot;

    public string Encode(TopologyReadCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Inner)) throw new ArgumentException("Missing inner topology cursor.");
        if (state.FormatVersion != Version || !ValidStamp(state))
            throw new ArgumentException("Invalid topology cursor repair revision.");
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
            if (payload is null || payload.State is null
                || string.IsNullOrWhiteSpace(payload.State.Inner)) throw new TopologyCursorWireException();
            if (payload.Version is not LegacyVersion and not Version) throw new TopologyCursorWireException();
            // Scope/route mismatch must be 400 even for an otherwise valid v1
            // cursor. Defer the version/revision restart to EnsureBound.
            return payload.State with { FormatVersion = payload.Version };
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
        if (state.FormatVersion != Version || !ValidStamp(state))
            throw new TopologyRestartRequiredException("Topology cursor predates the current repair revision.");
    }

    private sealed record Payload(int Version, TopologyReadCursorState State);

    private static bool ValidStamp(TopologyReadCursorState state) => state.ReadMode switch
    {
        TopologyReadMode.DeclaredOnly => state.RepairStamp is null,
        TopologyReadMode.ObservedOrMixed => state.RepairStamp is { Generation: >= 0 } stamp
            && !string.IsNullOrWhiteSpace(stamp.CertificateDigest),
        _ => false,
    };
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
    decimal? ToUnixNano)
{
    public TopologyReadMode ReadMode { get; init; } = TopologyReadMode.ObservedOrMixed;
    public TopologyRepairReadStamp? RepairStamp { get; init; }

    [JsonIgnore]
    public int FormatVersion { get; init; } = 2;
}

public sealed class TopologyCursorWireException : ArgumentException
{
    public TopologyCursorWireException() : base("Invalid topology cursor.") { }
}
