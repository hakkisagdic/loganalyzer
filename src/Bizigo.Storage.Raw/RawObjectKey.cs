using System.Globalization;

namespace Bizigo.Storage.Raw;

/// <summary>
/// Ham arşiv nesne anahtarı (F1 §7.1):
/// <c>raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/{source_class}/{id}.ndjson.zst</c>
///
/// <c>owner_group</c> yolun <b>içinde</b>, çünkü ham okuma da kapsam filtresinden
/// geçmek zorunda (K17). Anahtardan grubu okuyabilmek, indirmeden önce yetki
/// kontrolü yapmayı mümkün kılıyor.
///
/// <para>
/// <b>Grup adı eğik çizgi içerebilir.</b> Keycloak grup adlarını tam yol olarak
/// basıyor (<c>/network/core</c>), yani <c>owner_group</c> tek bir yol segmenti
/// değil. Bu yüzden anahtar baştan değil <b>sondan</b> ayrıştırılıyor: sabit olan
/// son altı segment (yyyy/MM/dd/hh/sınıf/kimlik), geriye kalan orta kısım gruptur.
/// Baştan ayrıştırmak <c>network/core</c>'u <c>network</c> olarak okur ve kapsam
/// kontrolü sessizce yanlış grupla çalışırdı.
/// </para>
/// </summary>
public sealed record RawObjectKey(
    string OwnerGroup,
    DateTimeOffset Hour,
    string SourceClass,
    string Id)
{
    public const string Prefix = "raw";
    public const string Extension = ".ndjson.zst";

    /// <summary>Grup adından sonra gelen sabit segment sayısı: yyyy/MM/dd/hh/sınıf/kimlik.</summary>
    private const int TrailingSegments = 6;

    public string Value => string.Create(
        CultureInfo.InvariantCulture,
        $"{Prefix}/{Normalize(OwnerGroup)}/{Hour.UtcDateTime:yyyy}/{Hour.UtcDateTime:MM}/{Hour.UtcDateTime:dd}/{Hour.UtcDateTime:HH}/{SourceClass}/{Id}{Extension}");

    public override string ToString() => Value;

    /// <summary>
    /// Anahtardan <c>owner_group</c>'u çıkarır. Biçim beklenenden farklıysa
    /// <c>null</c> döner — kapsam kontrolü "bilinmiyor"u reddeder, tahmin etmez.
    /// </summary>
    public static string? ReadOwnerGroup(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // En az: raw + tek segmentlik grup + altı sabit segment.
        if (parts.Length < TrailingSegments + 2
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Tarih segmentleri doğrulanıyor: doğrulanmazsa beklenmedik biçimdeki bir
        // anahtardan uydurma bir grup adı okunur ve kontrol yanlış çalışır.
        for (var i = parts.Length - TrailingSegments; i < parts.Length - 2; i++)
        {
            if (!IsDigits(parts[i]))
            {
                return null;
            }
        }

        return string.Join('/', parts[1..^TrailingSegments]);
    }

    /// <summary>Keycloak'ın baştaki eğik çizgisi (<c>/network/core</c>) anahtarda boş segment üretmesin.</summary>
    private static string Normalize(string ownerGroup) => ownerGroup.Trim('/');

    private static bool IsDigits(string value)
    {
        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
