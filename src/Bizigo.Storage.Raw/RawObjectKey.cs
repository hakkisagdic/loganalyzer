using System.Globalization;

namespace Bizigo.Storage.Raw;

/// <summary>
/// Ham arşiv nesne anahtarı (F1 §7.1):
/// <c>raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/{source_class}/{id}.ndjson.zst</c>
///
/// <c>owner_group</c> yolun <b>içinde</b>, çünkü ham okuma da kapsam filtresinden
/// geçmek zorunda (K17). Anahtardan grubu okuyabilmek, indirmeden önce yetki
/// kontrolü yapmayı mümkün kılıyor.
/// </summary>
public sealed record RawObjectKey(
    string OwnerGroup,
    DateTimeOffset Hour,
    string SourceClass,
    string Id)
{
    public const string Prefix = "raw";
    public const string Extension = ".ndjson.zst";

    public string Value => string.Create(
        CultureInfo.InvariantCulture,
        $"{Prefix}/{OwnerGroup}/{Hour.UtcDateTime:yyyy}/{Hour.UtcDateTime:MM}/{Hour.UtcDateTime:dd}/{Hour.UtcDateTime:HH}/{SourceClass}/{Id}{Extension}");

    public override string ToString() => Value;

    /// <summary>
    /// Anahtardan <c>owner_group</c>'u çıkarır. Biçim beklenenden farklıysa
    /// <c>null</c> döner — kapsam kontrolü "bilinmiyor"u reddeder, tahmin etmez.
    /// </summary>
    public static string? ReadOwnerGroup(string objectKey)
    {
        ArgumentNullException.ThrowIfNull(objectKey);

        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            ? parts[1]
            : null;
    }
}
