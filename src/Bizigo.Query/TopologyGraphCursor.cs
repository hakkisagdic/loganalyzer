using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Query;

internal readonly record struct TopologyGraphCursor(long PublishedSequence, byte[] QueryFingerprint, string LastKey);

internal static class TopologyGraphCursorCodec
{
    private const uint Magic = 0x42_5A_47_51; // BZGQ
    private const byte Version = 1;
    private const int FingerprintBytes = 16;
    private const int ChecksumBytes = 16;
    private const int FixedBytes = 4 + 1 + 8 + FingerprintBytes + 2 + ChecksumBytes;
    private const int MaxKeyBytes = 1024;

    public static string Encode(long publishedSequence, ReadOnlySpan<byte> queryFingerprint, string lastKey)
    {
        if (publishedSequence < 0) throw new ArgumentOutOfRangeException(nameof(publishedSequence));
        if (queryFingerprint.Length != FingerprintBytes) throw new ArgumentException("Invalid query fingerprint.", nameof(queryFingerprint));
        ArgumentException.ThrowIfNullOrEmpty(lastKey);

        var key = Encoding.UTF8.GetBytes(lastKey);
        if (key.Length > MaxKeyBytes) throw new ArgumentOutOfRangeException(nameof(lastKey));
        var bytes = new byte[FixedBytes + key.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), Magic);
        bytes[4] = Version;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(5, 8), publishedSequence);
        queryFingerprint.CopyTo(bytes.AsSpan(13, FingerprintBytes));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(29, 2), (ushort)key.Length);
        key.CopyTo(bytes.AsSpan(31, key.Length));
        var checksum = SHA256.HashData(bytes.AsSpan(0, 31 + key.Length));
        checksum.AsSpan(0, ChecksumBytes).CopyTo(bytes.AsSpan(31 + key.Length));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static TopologyGraphCursor Decode(string encoded, ReadOnlySpan<byte> expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        byte[] bytes;
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new TopologyCursorException("Topology cursor is not valid base64url.", ex);
        }

        if (bytes.Length < FixedBytes
            || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) != Magic
            || bytes[4] != Version)
        {
            throw new TopologyCursorException("Topology cursor is truncated or has an unknown version.");
        }

        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(29, 2));
        if (keyLength == 0 || keyLength > MaxKeyBytes || bytes.Length != FixedBytes + keyLength)
        {
            throw new TopologyCursorException("Topology cursor length is invalid.");
        }

        var contentLength = 31 + keyLength;
        var expectedChecksum = SHA256.HashData(bytes.AsSpan(0, contentLength));
        if (!CryptographicOperations.FixedTimeEquals(
            bytes.AsSpan(contentLength, ChecksumBytes),
            expectedChecksum.AsSpan(0, ChecksumBytes)))
        {
            throw new TopologyCursorException("Topology cursor checksum is invalid.");
        }

        var fingerprint = bytes.AsSpan(13, FingerprintBytes);
        if (expectedFingerprint.Length != FingerprintBytes
            || !CryptographicOperations.FixedTimeEquals(fingerprint, expectedFingerprint))
        {
            throw new TopologyCursorException("Topology cursor belongs to another query or access scope.");
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(5, 8));
        if (sequence < 0) throw new TopologyCursorException("Topology cursor sequence is invalid.");
        var key = new UTF8Encoding(false, true).GetString(bytes, 31, keyLength);
        return new(sequence, fingerprint.ToArray(), key);
    }
}
