namespace Bizigo.Contracts;

/// <summary>
/// Bir isteğin görebileceği veri kapsamı (K17).
///
/// Kapsam <b>kaynaktan</b> gelir, olaydan değil: kullanıcının IdP grupları
/// <c>idp_group_mapping</c> üzerinden <c>owner_group</c> kümesine çevrilir.
///
/// <b>Kapalı başlar:</b> hiçbir grup yoksa ve sınırsız değilse, sorgu hiçbir şey
/// döndürmez. Boş kapsamın "her şey" anlamına gelmesi, bu üründe yapılabilecek en
/// pahalı hata olurdu.
/// </summary>
public sealed record AccessScope
{
    /// <summary>Kimlik doğrulanmamış / yetkisiz istek. Hiçbir satır görmez.</summary>
    public static AccessScope Denied { get; } = new()
    {
        Subject = "anonymous",
        OwnerGroups = new HashSet<string>(StringComparer.Ordinal),
        IsUnrestricted = false,
    };

    /// <summary>OIDC <c>sub</c> claim'i. Audit için.</summary>
    public required string Subject { get; init; }

    /// <summary>Görülebilir <c>owner_group</c> kümesi.</summary>
    public required IReadOnlySet<string> OwnerGroups { get; init; }

    /// <summary>
    /// Kapsam filtresi uygulanmaz. Yalnızca <c>admin</c> rolü için ve
    /// <b>bilinçli</b> olarak verilir — varsayılan asla bu değildir.
    /// </summary>
    public bool IsUnrestricted { get; init; }

    /// <summary>Sorgunun hiç satır döndüremeyeceği durum.</summary>
    public bool IsEmpty => !IsUnrestricted && OwnerGroups.Count == 0;

    /// <summary>Sistem içi işler (replay yazımı, scrub) için tam kapsam.</summary>
    public static AccessScope System(string subject) => new()
    {
        Subject = subject,
        OwnerGroups = new HashSet<string>(StringComparer.Ordinal),
        IsUnrestricted = true,
    };

    public static AccessScope ForGroups(string subject, IEnumerable<string> groups) => new()
    {
        Subject = subject,
        OwnerGroups = new HashSet<string>(groups, StringComparer.Ordinal),
        IsUnrestricted = false,
    };
}
