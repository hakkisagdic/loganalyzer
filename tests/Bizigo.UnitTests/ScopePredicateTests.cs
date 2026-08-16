using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;

namespace Bizigo.UnitTests;

/// <summary>
/// Kapsam ayrımının (K17) çekirdek mantığı. Container gerektirmiyor.
///
/// Buradaki bir hata "bir ekip başka ekibin ağ loglarını görüyor" demek — bu
/// üründeki en pahalı hata sınıfı. Testler bilinçli olarak paranoyak.
/// </summary>
public sealed class ScopePredicateTests
{
    [Fact]
    public void Bos_kapsam_hicbir_sey_dondurmez()
    {
        var predicate = ScopePredicate.From(AccessScope.Denied);

        Assert.True(predicate.DeniesEverything);
        Assert.False(predicate.IsUnrestricted);
        // "1" (filtre yok) DEĞİL, "0" (hiçbir şey) olmalı.
        Assert.Equal("0", predicate.ToSqlFragment());
    }

    [Fact]
    public void Grupsuz_kullanici_kapali_baslar()
    {
        var scope = AccessScope.ForGroups("ali", []);

        Assert.True(scope.IsEmpty);
        Assert.True(ScopePredicate.From(scope).DeniesEverything);
    }

    [Fact]
    public void Sinirsiz_kapsam_filtre_uygulamaz()
    {
        var predicate = ScopePredicate.From(AccessScope.System("replay"));

        Assert.True(predicate.IsUnrestricted);
        Assert.Equal("1", predicate.ToSqlFragment());
        Assert.False(predicate.HasParameter);
    }

    [Fact]
    public void Gruplu_kapsam_IN_kosulu_uretir()
    {
        var predicate = ScopePredicate.From(AccessScope.ForGroups("ali", ["network-core", "network-edge"]));

        Assert.False(predicate.DeniesEverything);
        Assert.False(predicate.IsUnrestricted);
        Assert.True(predicate.HasParameter);
        Assert.Equal("owner_group IN ({scope_groups:Array(String)})", predicate.ToSqlFragment());
        Assert.Equal(2, predicate.ParameterValue.Length);
    }

    [Fact]
    public void Daraltma_kapsami_genisletemez()
    {
        var scope = AccessScope.ForGroups("ali", ["network-core"]);

        // Kullanıcı sorgusunda sahip olmadığı bir grubu istiyor.
        var predicate = ScopePredicate.From(scope, ["network-core", "finans"]);

        Assert.Equal(["network-core"], predicate.Groups);
        Assert.DoesNotContain("finans", predicate.Groups);
    }

    [Fact]
    public void Tamamen_kapsam_disi_daraltma_hicbir_sey_dondurmez()
    {
        var scope = AccessScope.ForGroups("ali", ["network-core"]);

        var predicate = ScopePredicate.From(scope, ["finans"]);

        // Kesişim boş → sessizce tüm kapsama düşmüyor, hiçbir şey döndürmüyor.
        Assert.True(predicate.DeniesEverything);
        Assert.Equal("0", predicate.ToSqlFragment());
    }

    [Fact]
    public void Sinirsiz_kapsamda_daraltma_yine_de_uygulanir()
    {
        var predicate = ScopePredicate.From(AccessScope.System("admin"), ["network-core"]);

        Assert.False(predicate.IsUnrestricted);
        Assert.Equal(["network-core"], predicate.Groups);
    }

    [Fact]
    public void Grup_adi_buyuk_kucuk_harf_duyarli_eslesir()
    {
        var scope = AccessScope.ForGroups("ali", ["network-core"]);

        // "Network-Core" farklı bir gruptur. Kültür duyarlı karşılaştırma burada
        // sessizce yanlış eşleşme üretirdi (bkz. Türkçe I/ı tuzağı).
        var predicate = ScopePredicate.From(scope, ["Network-Core"]);

        Assert.True(predicate.DeniesEverything);
    }
}

public sealed class RawObjectKeyTests
{
    [Fact]
    public void Anahtar_beklenen_bicimde_uretilir()
    {
        var key = new RawObjectKey(
            "network-core",
            new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.Zero),
            "fortigate",
            "01J8XK");

        Assert.Equal("raw/network-core/2026/08/14/09/fortigate/01J8XK.ndjson.zst", key.Value);
    }

    [Fact]
    public void Anahtardan_grup_okunabiliyor()
    {
        Assert.Equal(
            "network-core",
            RawObjectKey.ReadOwnerGroup("raw/network-core/2026/08/14/09/fortigate/01J8XK.ndjson.zst"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("raw")]
    [InlineData("baska/network-core/2026/08/14/09/x/y.ndjson.zst")]
    [InlineData("/network-core/2026")]
    public void Beklenmeyen_bicimde_grup_tahmin_edilmez(string key)
    {
        // "Bilinmiyor" reddedilir. Tahmin, kapsam ayrımını delerdi.
        Assert.Null(RawObjectKey.ReadOwnerGroup(key));
    }
}
