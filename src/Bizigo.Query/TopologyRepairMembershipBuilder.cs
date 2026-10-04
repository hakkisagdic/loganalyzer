using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Query;

/// <summary>
/// A receipt/manifest-prefix item already verified by the repair caller. The
/// builder checks completeness and canonical membership, but cannot establish
/// the historical origin of a legacy conversion digest.
/// </summary>
public sealed record TopologyRepairMembershipPublication(
    long Sequence, string PublicationKey, bool RequiresLegacyConversion, string? ConversionDigest);

public sealed record TopologyRepairMembershipPending(
    long Sequence, string PublicationKey, bool RequiresLegacyConversion, string? ConversionDigest);

public sealed record TopologyRepairConversionMember(
    long Sequence, string PublicationKey, string ConversionDigest);

/// <summary>
/// Pure, unsealed candidate. It is not a PG member set, certificate, original
/// admission witness, or authorization to publish legacy data.
/// </summary>
public sealed record TopologyRepairMemberSetCandidate(
    IReadOnlyList<TopologyRepairConversionMember> Members,
    string CanonicalSha256,
    int CanonicalByteLength)
{
    public int Count => Members.Count;
}

/// <summary>
/// Builds a bounded, deterministic explicit conversion-member candidate from
/// a complete verified receipt prefix and one captured pending state. A native
/// v4 publication needs no member. SQL seal/immutability/Ready CAS and trusted
/// original spool custody are deliberately outside this pure function.
/// </summary>
public static class TopologyRepairMembershipBuilder
{
    public const int DefaultMaxConversionMembers = 4096;
    public const int DefaultMaxCanonicalMembershipBytes = 1_048_576;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("bizigo-topology-members-v2");

    public static TopologyRepairMemberSetCandidate Build(
        IReadOnlyList<TopologyRepairMembershipPublication> completeVerifiedPrefix,
        long publishedSequence,
        TopologyRepairMembershipPending? capturedPending,
        int maxConversionMembers = DefaultMaxConversionMembers,
        int maxCanonicalMembershipBytes = DefaultMaxCanonicalMembershipBytes)
    {
        ArgumentNullException.ThrowIfNull(completeVerifiedPrefix);
        if (publishedSequence < 0 || maxConversionMembers <= 0 || maxCanonicalMembershipBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(publishedSequence));
        if (completeVerifiedPrefix.Count != publishedSequence)
            throw new InvalidDataException("Verified publication prefix is incomplete.");

        var bySequence = new Dictionary<long, TopologyRepairMembershipPublication>();
        foreach (var publication in completeVerifiedPrefix)
        {
            if (publication.Sequence is <= 0 || publication.Sequence > publishedSequence
                || !bySequence.TryAdd(publication.Sequence, publication))
                throw new InvalidDataException("Publication prefix contains a gap, duplicate or invalid sequence.");
            Validate(publication.PublicationKey, publication.RequiresLegacyConversion, publication.ConversionDigest);
        }

        var members = bySequence.Values.Where(item => item.RequiresLegacyConversion)
            .Select(item => new TopologyRepairConversionMember(
                item.Sequence, item.PublicationKey, item.ConversionDigest!)).ToList();
        if (capturedPending is not null)
        {
            Validate(capturedPending.PublicationKey, capturedPending.RequiresLegacyConversion,
                capturedPending.ConversionDigest);
            if (capturedPending.Sequence == publishedSequence)
            {
                if (!bySequence.TryGetValue(capturedPending.Sequence, out var receipted)
                    || !string.Equals(receipted.PublicationKey, capturedPending.PublicationKey,
                        StringComparison.Ordinal)
                    || receipted.RequiresLegacyConversion != capturedPending.RequiresLegacyConversion
                    || !string.Equals(receipted.ConversionDigest, capturedPending.ConversionDigest,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("Receipted pending residue diverges from its publication.");
                // Captured pending converted to a receipt remains represented
                // by the committed member exactly once, including after DELETE.
            }
            else if (capturedPending.Sequence == checked(publishedSequence + 1))
            {
                if (capturedPending.RequiresLegacyConversion)
                    members.Add(new(capturedPending.Sequence, capturedPending.PublicationKey,
                        capturedPending.ConversionDigest!));
            }
            else throw new InvalidDataException("Pending publication is not the receipt residue or next sequence.");
        }

        if (members.Count > maxConversionMembers)
            throw new InvalidDataException("Complete conversion membership exceeds its member cap.");
        members.Sort(static (left, right) =>
        {
            var bySequence = left.Sequence.CompareTo(right.Sequence);
            return bySequence != 0 ? bySequence
                : StringComparer.Ordinal.Compare(left.PublicationKey, right.PublicationKey);
        });
        using var bytes = new MemoryStream();
        EnsureSpace(bytes, checked(4 + Magic.Length + 4 + 4), maxCanonicalMembershipBytes);
        WriteInt32(bytes, Magic.Length);
        bytes.Write(Magic);
        WriteInt32(bytes, 2);
        WriteInt32(bytes, members.Count);
        foreach (var member in members)
        {
            // Both hashes are validated lowercase ASCII SHA-256 values. Every
            // string remains length-framed so no field-boundary ambiguity can
            // turn a different member set into the same canonical byte stream.
            EnsureSpace(bytes, 8 + 4 + 64 + 4 + 64, maxCanonicalMembershipBytes);
            WriteInt64(bytes, member.Sequence);
            WriteInt32(bytes, 64);
            bytes.Write(Encoding.ASCII.GetBytes(member.PublicationKey));
            WriteInt32(bytes, 64);
            bytes.Write(Encoding.ASCII.GetBytes(member.ConversionDigest));
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        return new(Array.AsReadOnly(members.ToArray()), digest, checked((int)bytes.Length));
    }

    private static void Validate(string key, bool requiresLegacyConversion, string? digest)
    {
        RequireLowerSha256(key);
        if (requiresLegacyConversion)
        {
            if (digest is null) throw new InvalidDataException("Legacy publication has no conversion digest.");
            RequireLowerSha256(digest);
        }
        else if (digest is not null)
            throw new InvalidDataException("Native publication cannot carry a legacy conversion digest.");
    }

    private static void RequireLowerSha256(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Publication key or conversion digest is not lowercase SHA-256.");
    }

    private static void EnsureSpace(MemoryStream bytes, int nextBytes, int maxBytes)
    {
        if (bytes.Length > maxBytes - nextBytes)
            throw new InvalidDataException("Complete conversion membership exceeds its canonical-byte cap.");
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
