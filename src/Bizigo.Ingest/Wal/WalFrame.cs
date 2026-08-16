using System.Buffers.Binary;
using System.IO.Hashing;

namespace Bizigo.Ingest.Wal;

/// <summary>
/// Segment içindeki tek çerçeve.
///
/// <code>
/// [magic u32 = 0x425A4731 "BZG1"][payload_len u32][crc32 u32][payload]
/// </code>
///
/// <para>
/// <b>Magic neden var:</b> <c>kill -9</c> yarım yazma bırakabilir. CRC tek başına
/// bozuk gövdeyi yakalar ama uzunluk alanının kendisi yırtılmışsa çılgın bir
/// uzunluk okunur ve kurtarma çöker. Magic, çerçeve sınırının gerçekten orada
/// başladığını doğruluyor: uymayan bayt görüldüğü anda kurtarma o noktada durur.
/// </para>
/// </summary>
internal static class WalFrame
{
    public const uint Magic = 0x42_5A_47_31;
    public const int HeaderBytes = 12;

    /// <summary>Tek bir çerçevenin diske yazılacak tam hali.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8, 4), Crc32.HashToUInt32(payload));
        payload.CopyTo(buffer.AsSpan(HeaderBytes));
        return buffer;
    }

    /// <summary>
    /// Baştan bir çerçeve okumayı dener.
    /// </summary>
    /// <returns>
    /// Okunan toplam bayt; 0 ise burada geçerli çerçeve yok — dosyanın <b>geri
    /// kalanı budanır</b>. Yarım yazma normaldir, hata değildir: ack verilmemiş
    /// bir yazmanın yarısını görmek beklenen durumdur.
    /// </returns>
    public static int TryDecode(ReadOnlySpan<byte> input, out ReadOnlySpan<byte> payload)
    {
        payload = default;

        if (input.Length < HeaderBytes)
        {
            return 0;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(input[..4]) != Magic)
        {
            return 0;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4, 4));
        if (length > int.MaxValue - HeaderBytes)
        {
            return 0;
        }

        var total = HeaderBytes + (int)length;
        if (input.Length < total)
        {
            return 0;
        }

        var body = input.Slice(HeaderBytes, (int)length);
        if (Crc32.HashToUInt32(body) != BinaryPrimitives.ReadUInt32BigEndian(input.Slice(8, 4)))
        {
            return 0;
        }

        payload = body;
        return total;
    }
}
