using System.Globalization;
using Bizigo.Contracts;

namespace Bizigo.Storage.Raw;

public sealed class RawAccessDeniedException(string objectKey)
    : UnauthorizedAccessException($"Ham nesne kapsam dışında: {objectKey}");

/// <summary>
/// Ham okuma yolu: <c>raw_ref → nesne + konum</c> (F1 §7.1, T10 bunu kullanır).
///
/// <para>
/// <b>Kapsam kontrolü indirmeden önce yapılıyor.</b> Grup, nesne anahtarının
/// içinde durduğu için ağ trafiği oluşmadan karar verilebiliyor — kontrolü
/// indirmeden sonraya bırakmak, yetkisiz veriyi belleğe almak demekti.
/// </para>
/// </summary>
public sealed class RawReader(IRawObjectStore store)
{
    /// <summary>
    /// <c>&lt;object_key&gt;#&lt;offset&gt;:&lt;length&gt;</c> biçimini ayrıştırır.
    /// Biçim bozuksa <see langword="false"/> — tahmin edilmez.
    /// </summary>
    public static bool TryParseRawRef(
        string rawRef,
        out string objectKey,
        out long offset,
        out int length)
    {
        objectKey = string.Empty;
        offset = 0;
        length = 0;

        if (string.IsNullOrWhiteSpace(rawRef))
        {
            return false;
        }

        var hash = rawRef.LastIndexOf('#');
        if (hash <= 0 || hash == rawRef.Length - 1)
        {
            return false;
        }

        var colon = rawRef.IndexOf(':', hash);
        if (colon < 0)
        {
            return false;
        }

        if (!long.TryParse(
                rawRef.AsSpan(hash + 1, colon - hash - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out offset)
            || !int.TryParse(
                rawRef.AsSpan(colon + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out length))
        {
            return false;
        }

        objectKey = rawRef[..hash];
        return true;
    }

    /// <summary>Tek kaydın ham NDJSON satırı.</summary>
    public async Task<RawRecord?> ReadAsync(
        string rawRef,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!TryParseRawRef(rawRef, out var objectKey, out var offset, out var length))
        {
            return null;
        }

        EnsureInScope(objectKey, scope);

        var compressed = await store.GetAsync(objectKey, cancellationToken);
        if (compressed is null)
        {
            return null;
        }

        var line = RawObjectBuilder.ExtractLine(compressed, offset, length);
        return RawRecordCodec.Read(line.Span);
    }

    /// <summary>Nesnenin tamamı — replay (T11) bunu kullanır.</summary>
    public async Task<IReadOnlyList<RawRecord>> ReadObjectAsync(
        string objectKey,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureInScope(objectKey, scope);

        var compressed = await store.GetAsync(objectKey, cancellationToken);
        if (compressed is null)
        {
            return [];
        }

        var plain = RawObjectBuilder.Decompress(compressed);
        var records = new List<RawRecord>();
        var start = 0;

        for (var i = 0; i < plain.Length; i++)
        {
            if (plain[i] != (byte)'\n')
            {
                continue;
            }

            if (i > start)
            {
                records.Add(RawRecordCodec.Read(plain.AsSpan(start, i - start)));
            }

            start = i + 1;
        }

        return records;
    }

    private static void EnsureInScope(string objectKey, AccessScope scope)
    {
        var ownerGroup = RawObjectKey.ReadOwnerGroup(objectKey);

        // Anahtardan grup okunamıyorsa reddediyoruz. "Bilinmiyor"u geçirmek,
        // biçimi bozuk tek bir anahtarın kapsam kontrolünü atlamasına yeterdi.
        if (ownerGroup is null || !scope.Allows(ownerGroup))
        {
            throw new RawAccessDeniedException(objectKey);
        }
    }
}
