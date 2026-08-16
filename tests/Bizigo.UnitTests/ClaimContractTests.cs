using System.Security.Claims;
using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Claim sözleşmesinin testi (T09 kabul kriteri, F1 §10.1.1).
///
/// <para>
/// Sözleşme dört claim'den ibaret ve <b>dar tutulması bilinçli</b>: Keycloak →
/// Entra ID geçişi IdP tarafında mapper ayarı demek, kodda değişiklik değil.
/// Bu testler o sözleşmenin bekçisi — beşinci bir claim'e bağımlılık eklenirse
/// burada görünür.
/// </para>
/// </summary>
public sealed class ClaimContractTests
{
    private static GroupMapping Mapping(params (string Idp, string Owner)[] rows) =>
        GroupMapping.From(rows);

    private static ClaimsPrincipal User(
        string subject = "user-1",
        string[]? roles = null,
        string[]? groups = null)
    {
        var claims = new List<Claim> { new(BizigoClaims.Subject, subject) };
        claims.AddRange((roles ?? []).Select(r => new Claim(BizigoClaims.Roles, r)));
        claims.AddRange((groups ?? []).Select(g => new Claim(BizigoClaims.Groups, g)));

        return new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            authenticationType: "test",
            nameType: BizigoClaims.PreferredUsername,
            roleType: BizigoClaims.Roles));
    }

    [Fact]
    public void Kimliksiz_istek_hicbir_sey_goremiyor()
    {
        var resolver = Mapping();

        var scope = resolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()));

        // Boş kapsam "her şey" değil "hiçbir şey".
        Assert.True(scope.IsEmpty);
        Assert.False(scope.IsUnrestricted);
    }

    [Fact]
    public void Null_principal_reddediliyor()
    {
        var resolver = Mapping();

        Assert.True(resolver.Resolve(null).IsEmpty);
    }

    [Fact]
    public void Keycloakin_bastaki_egik_cizgisi_esleme_bozmuyor()
    {
        // Group Membership mapper `/network/core` basıyor, `network/core` değil.
        // Bu testin varlık sebebi tam olarak o eğik çizgi.
        var resolver = Mapping(("network/core", "net-core"));

        var scope = resolver.Resolve(User(groups: ["/network/core"]));

        Assert.Equal(["net-core"], scope.OwnerGroups);
    }

    [Fact]
    public void Eslesme_tablosu_tam_yolu_egik_cizgiyle_saklasa_da_calisiyor()
    {
        // Tablodaki değerin baştaki eğik çizgiyle yazılmış olması da olağan.
        var resolver = Mapping(("/network/core", "net-core"));

        Assert.Equal(["net-core"], resolver.Resolve(User(groups: ["/network/core"])).OwnerGroups);
    }

    [Fact]
    public void Ic_ice_gruplar_ayirt_ediliyor()
    {
        // `full.path` açık olmasaydı `network/core` ile `platform/core` aynı
        // görünürdü; bu testin koruduğu şey o karar.
        var resolver = Mapping(
            ("network/core", "net-core"),
            ("platform/core", "platform-core"));

        Assert.Equal(["net-core"], resolver.Resolve(User(groups: ["/network/core"])).OwnerGroups);
        Assert.Equal(["platform-core"], resolver.Resolve(User(groups: ["/platform/core"])).OwnerGroups);
    }

    [Fact]
    public void Birden_cok_grup_birlesiyor()
    {
        var resolver = Mapping(
            ("network/core", "net-core"),
            ("network/edge", "net-edge"));

        var scope = resolver.Resolve(User(groups: ["/network/core", "/network/edge"]));

        Assert.Equal(["net-core", "net-edge"], scope.OwnerGroups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Eslesmeyen_grup_kapsam_uretmiyor()
    {
        var resolver = Mapping(("network/core", "net-core"));

        var scope = resolver.Resolve(User(groups: ["/bilinmeyen/grup"]));

        // Eşleme eksikse kullanıcı veri GÖREMEZ — yanlış veri görmez.
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public void Admin_kapsam_filtresinden_muaf()
    {
        var resolver = Mapping(("network/core", "net-core"));

        var scope = resolver.Resolve(User(roles: [BizigoRoles.Admin]));

        // Muafiyet bilinçli ve TEK yerde tanımlı.
        Assert.True(scope.IsUnrestricted);
    }

    [Fact]
    public void Analyst_muaf_degil()
    {
        var resolver = Mapping(("network/core", "net-core"));

        var scope = resolver.Resolve(User(roles: [BizigoRoles.Analyst], groups: ["/network/core"]));

        Assert.False(scope.IsUnrestricted);
        Assert.Equal(["net-core"], scope.OwnerGroups);
    }

    [Fact]
    public void Ingest_rolu_hicbir_veri_goremiyor()
    {
        // Collector kimliği sızarsa veri YAZILABİLİR, OKUNAMAZ. Rol ayrımının
        // tek sebebi bu; testi de burada.
        var resolver = Mapping(("network/core", "net-core"));

        var scope = resolver.Resolve(User(subject: "service-account-bizigo-collector",
            roles: [BizigoRoles.Ingest]));

        Assert.True(scope.IsEmpty);
        Assert.False(scope.IsUnrestricted);
    }

    [Fact]
    public void Subject_audit_icin_tasiniyor()
    {
        var resolver = Mapping();

        Assert.Equal("user-42", resolver.Resolve(User(subject: "user-42")).Subject);
    }

    [Fact]
    public void Grup_eslemesi_ordinal_degil_kultur_duyarsiz_karsilastiriliyor()
    {
        // IdP grup adlarının yazımı elle giriliyor; büyük/küçük harf farkı
        // eşlemeyi bozmamalı. Ama karşılığı olan `owner_group` ordinal kalıyor.
        var resolver = Mapping(("Network/Core", "net-core"));

        Assert.Equal(["net-core"], resolver.Resolve(User(groups: ["/network/core"])).OwnerGroups);
    }
}
