using Bizigo.Contracts;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Kapsam filtresinin SQL karşılığı (K17).
///
/// <b>Tasarımın taşıyıcı noktası:</b> bu tip yalnızca bir <see cref="AccessScope"/>
/// üzerinden üretilebiliyor ve bu derlemedeki her okuma metodu onu <b>parametre
/// olarak istiyor</b>. Yani "kapsamsız sorgu" yazmak için ya bu tipi elde etmek
/// ya da imzayı değiştirmek gerekir; ikisi de kazara olmaz.
///
/// Mimari test (T02) ayrıca <c>ClickHouse.Driver</c>'a bu derlemenin dışından
/// referans verilmesini yasaklıyor — yani kimse bu kapıyı atlayıp doğrudan
/// bağlantı açamıyor.
/// </summary>
public readonly struct ScopePredicate
{
    private readonly string[] _groups;

    private ScopePredicate(bool unrestricted, bool denyAll, string[] groups)
    {
        IsUnrestricted = unrestricted;
        DeniesEverything = denyAll;
        _groups = groups;
    }

    public bool IsUnrestricted { get; }

    /// <summary>Kapsam boş — sorgu hiçbir satır döndüremez.</summary>
    public bool DeniesEverything { get; }

    public IReadOnlyList<string> Groups => _groups;

    /// <summary>
    /// İstenen daraltmayı kapsamla kesiştirir. Daraltma kapsamı <b>genişletemez</b>:
    /// kullanıcı sorgusunda olmayan bir grubu istese bile kesişim boş kalır.
    /// </summary>
    public static ScopePredicate From(AccessScope scope, IReadOnlyList<string>? narrowTo = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsUnrestricted)
        {
            return narrowTo is { Count: > 0 }
                ? new ScopePredicate(false, false, [.. narrowTo])
                : new ScopePredicate(true, false, []);
        }

        if (scope.OwnerGroups.Count == 0)
        {
            return new ScopePredicate(false, true, []);
        }

        var effective = narrowTo is { Count: > 0 }
            ? narrowTo.Where(scope.OwnerGroups.Contains).ToArray()
            : [.. scope.OwnerGroups];

        return effective.Length == 0
            ? new ScopePredicate(false, true, [])
            : new ScopePredicate(false, false, effective);
    }

    /// <summary>
    /// WHERE parçası. Sınırsız kapsamda <c>1</c>, boş kapsamda <c>0</c> —
    /// yani boş kapsam sessizce "filtre yok"a dönüşmüyor, açıkça hiçbir şey
    /// döndürmüyor.
    /// </summary>
    public string ToSqlFragment(string parameterName = "scope_groups")
    {
        if (DeniesEverything)
        {
            return "0";
        }

        return IsUnrestricted ? "1" : $"owner_group IN ({{{parameterName}:Array(String)}})";
    }

    public bool HasParameter => !DeniesEverything && !IsUnrestricted;

    public string[] ParameterValue => _groups;
}
