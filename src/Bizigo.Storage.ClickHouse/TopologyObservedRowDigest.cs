using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Derived 0014 integrity metadata for one observed physical row. This is
/// intentionally separate from the frozen v2/v4 projection-manifest rowset
/// hash: adding a column to that hash would invalidate durable pending batches.
/// </summary>
public static class TopologyObservedRowDigest
{
    public const int FrozenColumnCount = 40;
    private static readonly byte[] Prefix = "bizigo.topology.observed-row/v1\0"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The exact pre-0014 projector column order, excluding derived digest.</summary>
    public static IReadOnlyList<string> FrozenColumns { get; } = Array.AsReadOnly(new[]
    {
        "owner_group", "child_owner_group", "parent_owner_group", "from_node_id", "to_node_id",
        "relation", "directed", "provenance", "confidence", "edge_id", "trace_logical_id",
        "parent_span_logical_id", "span_logical_id", "parent_semantic_anchor", "child_semantic_anchor",
        "parent_fingerprint", "child_fingerprint", "parent_source_id", "child_source_id",
        "parent_node_binding_revision", "child_node_binding_revision", "parent_owner_history_revision",
        "child_owner_history_revision", "parent_node_history_revision", "child_node_history_revision",
        "parent_event_time_nano", "child_event_time_nano", "first_seen", "last_seen",
        "parent_occurrence_ids", "child_occurrence_ids", "evidence_occurrence_ids",
        "parent_trace_expiry", "child_trace_expiry", "observed_expiry", "expires_nano",
        "fingerprint", "publication_seq", "ttl_at", "ttl_supported",
    });

    /// <summary>
    /// Computes lowercase SHA-256 of a typed, length-framed v1 byte stream.
    /// The projector passes its original EdgeRow values. The reader passes the
    /// same 40 columns after exact CH hydration: arrays as string[] and ttl_at
    /// as signed Unix seconds. Neither path includes physical_row_sha256.
    /// </summary>
    public static string Compute(IReadOnlyList<object?> frozenPhysicalValues)
    {
        ArgumentNullException.ThrowIfNull(frozenPhysicalValues);
        if (frozenPhysicalValues.Count != FrozenColumnCount)
            throw new InvalidDataException("Observed row digest requires the frozen 40-column layout.");

        using var stream = new MemoryStream();
        stream.Write(Prefix);
        Span<byte> count = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(count, FrozenColumnCount);
        stream.Write(count);

        for (var ordinal = 0; ordinal < FrozenColumnCount; ordinal++)
        {
            var value = frozenPhysicalValues[ordinal];
            if (value is null or DBNull)
                throw new InvalidDataException($"Observed row digest column {ordinal} is null.");
            if (ordinal is 6 or 39)
                WriteFrame(stream, 'b', [ReadBooleanByte(value, ordinal)]);
            else if (ordinal == 8)
                WriteInt32(stream, 'f', BitConverter.SingleToInt32Bits(ReadFiniteSingle(value)));
            else if (ordinal is >= 19 and <= 24)
                WriteInt64(stream, 'i', ReadInt64(value, ordinal));
            else if (ordinal is >= 25 and <= 28 or 37)
                WriteUInt64(stream, 'u', ReadUInt64(value, ordinal));
            else if (ordinal is >= 29 and <= 31)
                WriteArray(stream, value, ordinal);
            else if (ordinal is >= 32 and <= 35)
                WriteDecimal(stream, value, ordinal);
            else if (ordinal == 38)
                WriteInt64(stream, 't', ReadUtcUnixSeconds(value));
            else
                WriteString(stream, value, ordinal);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static byte ReadBooleanByte(object value, int ordinal)
    {
        if (value is not byte result || result > 1)
            throw new InvalidDataException($"Observed row digest column {ordinal} is not a UInt8 boolean.");
        return result;
    }

    private static float ReadFiniteSingle(object value)
    {
        if (value is not float result || !float.IsFinite(result))
            throw new InvalidDataException("Observed row digest confidence is not finite Float32.");
        return result;
    }

    private static long ReadInt64(object value, int ordinal) => value is long result
        ? result : throw new InvalidDataException($"Observed row digest column {ordinal} is not Int64.");

    private static ulong ReadUInt64(object value, int ordinal) => value is ulong result
        ? result : throw new InvalidDataException($"Observed row digest column {ordinal} is not UInt64.");

    private static long ReadUtcUnixSeconds(object value)
    {
        if (value is long seconds) return seconds;
        if (value is not DateTime date || date.Kind != DateTimeKind.Utc
            || date.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new InvalidDataException("Observed row digest ttl_at is not an exact UTC second.");
        return new DateTimeOffset(date).ToUnixTimeSeconds();
    }

    private static void WriteString(Stream stream, object value, int ordinal)
    {
        if (value is not string text)
            throw new InvalidDataException($"Observed row digest column {ordinal} is not String.");
        if ((ordinal is 9 or 13 or 14 or 15 or 16 or 36)
            && (text.Length != 64 || text.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new InvalidDataException($"Observed row digest FixedString column {ordinal} is not lowercase hex64.");
        WriteFrame(stream, 's', Utf8.GetBytes(text));
    }

    private static void WriteArray(Stream stream, object value, int ordinal)
    {
        if (value is not IReadOnlyList<string> items)
            throw new InvalidDataException($"Observed row digest column {ordinal} is not Array(String).");
        using var payload = new MemoryStream();
        WriteUInt32Raw(payload, checked((uint)items.Count));
        foreach (var item in items)
        {
            if (item is null)
                throw new InvalidDataException($"Observed row digest array {ordinal} contains null.");
            var bytes = Utf8.GetBytes(item);
            WriteUInt32Raw(payload, checked((uint)bytes.Length));
            payload.Write(bytes);
        }
        WriteFrame(stream, 'a', payload.ToArray());
    }

    private static void WriteDecimal(Stream stream, object value, int ordinal)
    {
        if (value is not decimal number || number < 0 || number != decimal.Truncate(number))
            throw new InvalidDataException($"Observed row digest column {ordinal} is not integral Decimal(21,0).");
        WriteFrame(stream, 'd', Encoding.ASCII.GetBytes(number.ToString("0", CultureInfo.InvariantCulture)));
    }

    private static void WriteInt32(Stream stream, char tag, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        WriteFrame(stream, tag, bytes);
    }

    private static void WriteInt64(Stream stream, char tag, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        WriteFrame(stream, tag, bytes);
    }

    private static void WriteUInt64(Stream stream, char tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteFrame(stream, tag, bytes);
    }

    private static void WriteFrame(Stream stream, char tag, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(checked((byte)tag));
        WriteUInt32Raw(stream, checked((uint)payload.Length));
        stream.Write(payload);
    }

    private static void WriteUInt32Raw(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
