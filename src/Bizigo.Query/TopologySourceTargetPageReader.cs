using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// Bounded historical Contains enumeration for internal RCA mapping. This
/// reader never projects an unauthorized target: it reads one raw historical
/// candidate at a time and turns a hidden branch into a terminal Hidden, not
/// a falsely complete source. The keyset survives page boundaries without
/// rebuilding an unbounded graph in memory. Legal Contains diamonds emit each
/// raw chain separately; the provider charges every visit and chooses one
/// canonical target only after the source's terminal Complete chunk.
/// </summary>
public sealed class TopologySourceTargetPageReader(
    IDbContextFactory<ControlPlaneDbContext> factory,
    TopologyPublicationFence fence,
    int maxRawEdgesPerSource = 4096)
{
    private const int MaxPageSize = 200;
    private const int CursorVersion = 2;
    private static readonly byte[] CursorKey = RandomNumberGenerator.GetBytes(32);
    private readonly int _maxRawEdgesPerSource = maxRawEdgesPerSource > 0
        ? maxRawEdgesPerSource : throw new ArgumentOutOfRangeException(nameof(maxRawEdgesPerSource));

    public Task<TopologySourceTargetsPage> ReadPageAsync(IReadOnlyList<string> sourceIds,
        decimal asOfUnixNano, AccessScope scope, int pageSize, string? cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(scope);
        if (sourceIds.Count is < 1 or > 20 || sourceIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid mapped source set.", nameof(sourceIds));
        if (pageSize is < 1 or > MaxPageSize) throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (asOfUnixNano < 0 || asOfUnixNano != decimal.Truncate(asOfUnixNano))
            throw new ArgumentOutOfRangeException(nameof(asOfUnixNano));
        var sources = sourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var fingerprint = Fingerprint(sources, asOfUnixNano, scope);
        var continuation = cursor is null ? null : Decode(cursor, fingerprint);
        return fence.ExecuteAsync(async (revision, token) =>
        {
            // The page DTO intentionally carries only the PG/CH pair; the
            // encrypted continuation additionally binds the Ready certificate.
            // A signed pre-repair cursor cannot be used to cross this boundary.
            var repairStamp = revision.RepairStamp ?? throw new TopologyObservedRepairUnavailableException();
            if (continuation is not null &&
                (continuation.Version != CursorVersion
                || continuation.PostgresEpoch != revision.PostgresEpoch
                || continuation.ClickHouseWatermark != revision.ClickHouseWatermark
                || continuation.RepairStamp != repairStamp))
                throw new TopologyRestartRequiredException("Topology mapping revision changed; restart the query.");
            if (continuation is not null && continuation.SourceIndex >= sources.Length)
                throw new TopologyCursorException("Invalid topology mapping cursor.");
            var state = continuation ?? new CursorState(CursorVersion, fingerprint, revision.PostgresEpoch,
                revision.ClickHouseWatermark, 0, string.Empty, 0, string.Empty,
                string.Empty, 0, Phase.Start, 0, repairStamp);
            await using var db = await factory.CreateDbContextAsync(token);
            var items = new List<TopologySourceTargetChunk>(pageSize);
            while (items.Count < pageSize && state.SourceIndex < sources.Length)
            {
                var sourceId = sources[state.SourceIndex];
                switch (state.Phase)
                {
                    case Phase.Start:
                    {
                        var identities = await db.TopologyNodes.AsNoTracking()
                            .Where(node => node.SourceId == sourceId && node.Kind == TopologyNodeKind.Source)
                            .Take(2).ToArrayAsync(token);
                        if (identities.Length != 1)
                        {
                            EmitTerminal(TopologySourceTargetStatus.Missing, "SourceUnresolved", string.Empty);
                            break;
                        }
                        var root = await AtAsync(db, identities[0].Id, asOfUnixNano, token);
                        if (root is null || !root.Enabled)
                        {
                            EmitTerminal(TopologySourceTargetStatus.Missing, "SourceUnresolved", string.Empty);
                            break;
                        }
                        if (!CanRead(scope, root.OwnerGroup))
                        {
                            EmitTerminal(TopologySourceTargetStatus.Hidden, "HiddenBoundary", string.Empty);
                            break;
                        }
                        state = state with { RootId = identities[0].Id, Phase = Phase.First };
                        break;
                    }
                    case Phase.First:
                    {
                        var next = await NextEdgeAsync(db, state.RootId, state.FirstRevision,
                            asOfUnixNano, token);
                        if (next is null)
                        {
                            EmitTerminal(TopologySourceTargetStatus.Complete, null, state.RootId);
                            break;
                        }
                        if (state.RawEdges >= _maxRawEdgesPerSource)
                        {
                            EmitTerminal(TopologySourceTargetStatus.Truncated, "MappingTruncated", state.RootId);
                            break;
                        }
                        state = state with { FirstRevision = next.Revision,
                            FirstEdgeId = next.EdgeId.ToString("D"), SecondRevision = 0,
                            RawEdges = state.RawEdges + 1 };
                        var target = await CheckTargetAsync(db, next, state.RootId,
                            TopologyNodeKind.Service, asOfUnixNano, scope, token);
                        if (target.Status is not null)
                        {
                            EmitTerminal(target.Status.Value, target.Reason, state.RootId);
                            break;
                        }
                        state = state with { ServiceId = next.ToNodeId, Phase = Phase.Second };
                        items.Add(new(sourceId, state.RootId,
                            new(next.ToNodeId, [next.EdgeId.ToString("D")],
                                [state.RootId, next.ToNodeId]), null, null));
                        break;
                    }
                    case Phase.Second:
                    {
                        var next = await NextEdgeAsync(db, state.ServiceId, state.SecondRevision,
                            asOfUnixNano, token);
                        if (next is null)
                        {
                            state = state with { Phase = Phase.First, ServiceId = string.Empty,
                                FirstEdgeId = string.Empty,
                                SecondRevision = 0 };
                            break;
                        }
                        if (state.RawEdges >= _maxRawEdgesPerSource)
                        {
                            EmitTerminal(TopologySourceTargetStatus.Truncated, "MappingTruncated", state.RootId);
                            break;
                        }
                        state = state with { SecondRevision = next.Revision,
                            RawEdges = state.RawEdges + 1 };
                        var target = await CheckTargetAsync(db, next, state.ServiceId,
                            TopologyNodeKind.ServiceInstance, asOfUnixNano, scope, token);
                        if (target.Status is not null)
                        {
                            EmitTerminal(target.Status.Value, target.Reason, state.RootId);
                            break;
                        }
                        items.Add(new(sourceId, state.RootId, new(next.ToNodeId,
                            [state.FirstEdgeId, next.EdgeId.ToString("D")],
                            [state.RootId, state.ServiceId, next.ToNodeId]), null, null));
                        break;
                    }
                    default: throw new TopologyCursorException("Invalid topology mapping cursor phase.");
                }
            }
            var nextCursor = state.SourceIndex < sources.Length ? Encode(state) : null;
            return new TopologySourceTargetsPage(items, nextCursor,
                revision.PostgresEpoch, revision.ClickHouseWatermark);

            void EmitTerminal(TopologySourceTargetStatus status, string? reason, string rootId)
            {
                items.Add(new(sources[state.SourceIndex], rootId, null, status, reason));
                state = state with { SourceIndex = state.SourceIndex + 1, RootId = string.Empty,
                    FirstRevision = 0, FirstEdgeId = string.Empty, ServiceId = string.Empty,
                    SecondRevision = 0,
                    Phase = Phase.Start, RawEdges = 0 };
            }
        }, cancellationToken);
    }

    private static async Task<(TopologySourceTargetStatus? Status, string? Reason)> CheckTargetAsync(
        ControlPlaneDbContext db, TopologyDeclaredEdgeHistoryEntity edge, string expectedFrom,
        TopologyNodeKind expectedKind, decimal asOf, AccessScope scope, CancellationToken token)
    {
        if (edge.FromNodeId != expectedFrom || !edge.Directed || edge.Provenance != "declared")
            return (TopologySourceTargetStatus.Ambiguous, "MappingUnresolved");
        // The edge stores both captured owners; history verifies those
        // snapshots rather than trusting a mutable current owner field.
        var from = await AtAsync(db, edge.FromNodeId, asOf, token);
        var to = await AtAsync(db, edge.ToNodeId, asOf, token);
        if (from is null || to is null || !from.Enabled || !to.Enabled)
            return (TopologySourceTargetStatus.Ambiguous, "MappingUnresolved");
        if (from.OwnerGroup != edge.FromOwnerGroup || to.OwnerGroup != edge.ToOwnerGroup)
            return (TopologySourceTargetStatus.Ambiguous, "MappingUnresolved");
        if (!CanRead(scope, from.OwnerGroup) || !CanRead(scope, to.OwnerGroup))
            return (TopologySourceTargetStatus.Hidden, "HiddenBoundary");
        var identities = await db.TopologyNodes.AsNoTracking()
            .Where(node => node.Id == edge.ToNodeId).Select(node => node.Kind)
            .Take(2).ToArrayAsync(token);
        if (identities.Length != 1 || identities[0] != expectedKind)
            return (TopologySourceTargetStatus.Ambiguous, "MappingUnresolved");
        return (null, null);
    }

    private static Task<TopologyNodeHistoryEntity?> AtAsync(ControlPlaneDbContext db,
        string nodeId, decimal asOf, CancellationToken token) =>
        db.TopologyNodeHistory.AsNoTracking().Where(row => row.NodeId == nodeId
            && row.FromNano <= asOf && (row.ToNano == null || asOf < row.ToNano))
            .SingleOrDefaultAsync(token);

    private static Task<TopologyDeclaredEdgeHistoryEntity?> NextEdgeAsync(ControlPlaneDbContext db,
        string fromNodeId, long afterRevision, decimal asOf, CancellationToken token) =>
        db.TopologyDeclaredEdgeHistory.AsNoTracking().Where(edge => edge.FromNodeId == fromNodeId
            && edge.Relation == "contains" && edge.Revision > afterRevision
            && edge.FromNano <= asOf && (edge.ToNano == null || asOf < edge.ToNano)
            && edge.DeletedAt == null).OrderBy(edge => edge.FromNodeId).ThenBy(edge => edge.Relation).ThenBy(edge => edge.Revision)
            .TagWith("topology.mapping.next-edge").FirstOrDefaultAsync(token);

    private static bool CanRead(AccessScope scope, string owner) =>
        owner != "_unassigned" ? scope.Allows(owner) : scope.IsUnrestricted;

    private static string Fingerprint(IReadOnlyList<string> sources, decimal asOf, AccessScope scope)
    {
        var value = string.Join('\u001e', [asOf.ToString(CultureInfo.InvariantCulture), scope.Subject,
            scope.IsUnrestricted ? "1" : "0", string.Join('\u001f', scope.OwnerGroups.Order(StringComparer.Ordinal)),
            string.Join('\u001f', sources)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Encode(CursorState state)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(CursorKey, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
        var bytes = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(bytes, 0);
        tag.CopyTo(bytes, nonce.Length);
        cipher.CopyTo(bytes, nonce.Length + tag.Length);
        return Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    private static CursorState Decode(string encoded, string fingerprint)
    {
        if (encoded.Length > 4096) throw new TopologyCursorException("Invalid topology mapping cursor.");
        try
        {
            var value = encoded.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
            if (bytes.Length < 29) throw new TopologyCursorException("Invalid topology mapping cursor.");
            var plain = new byte[bytes.Length - 28];
            using (var aes = new AesGcm(CursorKey, 16))
                aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain);
            var state = JsonSerializer.Deserialize<CursorState>(plain);
            if (state is null || state.Version is not (1 or CursorVersion) || state.Fingerprint != fingerprint
                || state.SourceIndex < 0 || state.FirstRevision < 0 || state.SecondRevision < 0
                || state.RawEdges < 0 || !Enum.IsDefined(state.Phase)
                || (state.Phase == Phase.Second && string.IsNullOrWhiteSpace(state.FirstEdgeId))
                || (state.Version == CursorVersion && state.RepairStamp is null))
                throw new TopologyCursorException("Invalid topology mapping cursor.");
            return state;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
        { throw new TopologyCursorException("Invalid topology mapping cursor.", ex); }
    }

    private enum Phase { Start = 0, First = 1, Second = 2 }

    private sealed record CursorState(int Version, string Fingerprint, long PostgresEpoch,
        ulong ClickHouseWatermark, int SourceIndex, string RootId, long FirstRevision,
        string FirstEdgeId, string ServiceId, long SecondRevision, Phase Phase, int RawEdges,
        TopologyRepairReadStamp? RepairStamp = null);
}
