using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using ZstdSharp;

namespace Bizigo.Storage.Raw;

/// <param name="EventId">Kaydın kimliği.</param>
/// <param name="Offset">Sıkıştırılmamış NDJSON içindeki bayt konumu.</param>
/// <param name="Length">Satırın uzunluğu (satır sonu hariç).</param>
public sealed record RawRefEntry(Guid EventId, long Offset, int Length)
{
    /// <summary>
    /// <c>ClickHouse.raw_ref</c> biçimi: <c>&lt;object_key&gt;#&lt;offset&gt;:&lt;length&gt;</c>
    /// (F1 §7.1).
    /// </summary>
    public string ToRawRef(string objectKey) => string.Create(
        CultureInfo.InvariantCulture,
        $"{objectKey}#{Offset}:{Length}");
}

public sealed record BuiltRawObject(
    byte[] Compressed,
    string Sha256,
    int EventCount,
    DateTimeOffset TsFrom,
    DateTimeOffset TsTo,
    IReadOnlyList<RawRefEntry> Refs)
{
    public long ByteSize => Compressed.LongLength;
}

/// <summary>
/// Tek bir arşiv nesnesini kurar: NDJSON satırlarını biriktirir, her satırın
/// <b>sıkıştırılmamış</b> konumunu tutar, sonunda ZSTD'ler ve sha256'sını alır.
///
/// <para>
/// Konumlar sıkıştırma öncesine göre: zstd akışı aranabilir (seekable) değil,
/// yani okuma yolu nesneyi açıp o konuma gidiyor. Bunun bedeli tek kaydı okumak
/// için nesnenin tamamını açmak; kazancı, sıkıştırma parametreleri değişse bile
/// eski <c>raw_ref</c>'lerin geçerli kalması.
/// </para>
///
/// <para>
/// sha256 <b>sıkıştırılmış</b> baytların üzerinden alınıyor — depodan geri
/// okunan şey o. İçerik üzerinden alınsaydı, sıkıştırıcı sürümü değiştiğinde
/// doğrulama sahte uyuşmazlık üretirdi.
/// </para>
/// </summary>
public sealed class RawObjectBuilder
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024 * 1024);
    private readonly List<RawRefEntry> _refs = [];
    private DateTimeOffset _tsFrom = DateTimeOffset.MaxValue;
    private DateTimeOffset _tsTo = DateTimeOffset.MinValue;

    public int EventCount => _refs.Count;

    public long UncompressedBytes => _buffer.WrittenCount;

    public bool IsEmpty => _refs.Count == 0;

    public void Add(Guid eventId, DateTimeOffset timestamp, ReadOnlySpan<byte> ndjsonLine)
    {
        _refs.Add(new RawRefEntry(eventId, _buffer.WrittenCount, ndjsonLine.Length));

        _buffer.Write(ndjsonLine);
        _buffer.Write("\n"u8);

        if (timestamp < _tsFrom)
        {
            _tsFrom = timestamp;
        }

        if (timestamp > _tsTo)
        {
            _tsTo = timestamp;
        }
    }

    public BuiltRawObject Build(int compressionLevel)
    {
        if (IsEmpty)
        {
            throw new InvalidOperationException("Boş nesne yazılmaz.");
        }

        using var compressor = new Compressor(compressionLevel);
        var compressed = compressor.Wrap(_buffer.WrittenSpan).ToArray();

        return new BuiltRawObject(
            compressed,
            Convert.ToHexString(SHA256.HashData(compressed)).ToLowerInvariant(),
            _refs.Count,
            _tsFrom,
            _tsTo,
            _refs);
    }

    /// <summary>Nesneyi açıp istenen konumdaki satırı verir.</summary>
    public static ReadOnlyMemory<byte> ExtractLine(ReadOnlySpan<byte> compressed, long offset, int length)
    {
        using var decompressor = new Decompressor();
        var plain = decompressor.Unwrap(compressed).ToArray();

        if (offset < 0 || length < 0 || offset + length > plain.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "raw_ref nesnenin dışını gösteriyor — manifest ile nesne ayrışmış olabilir.");
        }

        return plain.AsMemory((int)offset, length);
    }

    public static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }
}
