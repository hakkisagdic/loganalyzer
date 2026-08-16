using System.Security.Claims;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

/// <summary>
/// Ürünün IdP'den beklediği claim adları (F1 §10.1.1).
///
/// <para>
/// Sözleşme <b>bilinçli olarak dar</b>: dört claim. Keycloak → Entra ID geçişi
/// IdP tarafında mapper ayarı ve <c>idp_group_mapping</c> satırları demek —
/// <b>kodda değişiklik yok</b>. Buraya beşinci bir claim eklemek o özelliği
/// kaybetmek olur.
/// </para>
/// </summary>
public static class BizigoClaims
{
    public const string Subject = "sub";
    public const string PreferredUsername = "preferred_username";
    public const string Roles = "roles";
    public const string Groups = "groups";
}

public static class BizigoRoles
{
    public const string Reader = "reader";
    public const string Analyst = "analyst";
    public const string Author = "author";
    public const string Admin = "admin";

    /// <summary>Yalnızca <c>/v1/logs</c>. Okuma yetkisi <b>yok</b>.</summary>
    public const string Ingest = "ingest";
}

/// <summary>
/// Kimlikten kapsama: IdP grupları → <c>owner_group</c> kümesi (K17, F1 §10.1.1).
///
/// <para>
/// <b>Claim doğrudan <c>owner_group</c> sayılmıyor.</b> Sayılsaydı bir ekibin
/// veri kapsamını değiştirmek için IdP'ye dokunmak gerekirdi; eşleme tablosu bu
/// yüzden Postgres'te.
/// </para>
///
/// <para>
/// <b>Keycloak tuzağı:</b> Group Membership mapper tam yolu <b>başında eğik
/// çizgiyle</b> basıyor (<c>/network/core</c>). Tam yol açık bırakıldı — kapatmak
/// iç içe gruplarda ad çakışması üretirdi — ve giriş burada normalize ediliyor.
/// </para>
/// </summary>
public sealed class AccessScopeResolver(IDbContextFactory<ControlPlaneDbContext> factory)
{
    private GroupMapping _mapping = GroupMapping.Empty;

    /// <summary>Yalnızca teşhis: yüklü eşleme satırı sayısı.</summary>
    public int MappingCount => _mapping.Count;

    /// <summary>
    /// Eşleme tablosunu belleğe alır. Sorgu başına veritabanına gitmemek için:
    /// tablo onlarca satır, sorgu trafiği çok daha yoğun.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.IdpGroupMappings
            .AsNoTracking()
            .Select(m => new { m.IdpGroup, m.OwnerGroup })
            .ToListAsync(cancellationToken);

        Interlocked.Exchange(
            ref _mapping,
            GroupMapping.From(rows.Select(r => (r.IdpGroup, r.OwnerGroup))));
    }

    public AccessScope Resolve(ClaimsPrincipal? principal) =>
        Volatile.Read(ref _mapping).Resolve(principal);
}

/// <summary>
/// Claim → kapsam çevriminin <b>saf</b> hâli.
///
/// <para>
/// Veritabanından ayrı durması bilinçli: çevrim mantığı bu üründeki en pahalı
/// hata sınıfının (K17) tam ortasında ve bir EF sağlayıcısı kurmadan
/// sınanabilmesi gerekiyor. <see cref="AccessScopeResolver"/> yalnızca bunu
/// tazeliyor.
/// </para>
/// </summary>
public sealed class GroupMapping
{
    private readonly Dictionary<string, string> _map;

    private GroupMapping(Dictionary<string, string> map) => _map = map;

    public static GroupMapping Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public int Count => _map.Count;

    public static GroupMapping From(IEnumerable<(string IdpGroup, string OwnerGroup)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // IdP grup adları elle giriliyor; büyük/küçük harf farkı eşlemeyi
        // bozmamalı. Karşılığı olan `owner_group` ise ordinal kalıyor.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (idpGroup, ownerGroup) in rows)
        {
            map[Normalize(idpGroup)] = ownerGroup;
        }

        return new GroupMapping(map);
    }

    public AccessScope Resolve(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return AccessScope.Denied;
        }

        var subject = principal.FindFirst(BizigoClaims.Subject)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "unknown";

        // `admin` kapsam filtresinden muaf — ama bu BİLİNÇLİ ve tek yerde.
        if (principal.IsInRole(BizigoRoles.Admin))
        {
            return AccessScope.System(subject);
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in principal.FindAll(BizigoClaims.Groups))
        {
            if (_map.TryGetValue(Normalize(claim.Value), out var ownerGroup))
            {
                groups.Add(ownerGroup);
            }
        }

        // Eşleşmeyen grup sessizce atlanıyor ve kapsam BOŞ kalabiliyor. Boş kapsam
        // "her şey" değil "hiçbir şey" demek (AccessScope.IsEmpty) — eşleme
        // eksikse kullanıcı veri göremez, yanlış veri görmez.
        return AccessScope.ForGroups(subject, groups);
    }

    /// <summary>Keycloak'ın baştaki eğik çizgisi eşlemeyi bozmasın.</summary>
    private static string Normalize(string group) => group.Trim().TrimStart('/');
}
