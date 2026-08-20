using Bizigo.Contracts;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// Saklanan paketin <b>okuma</b> kapsamı (K17, T37).
///
/// <para>
/// <b>Neden ayrı bir kapı gerekiyor:</b> kanıt toplanırken kapsam filtresi
/// sorgu yolunda uygulanıyor. Ama paket <b>saklanıyor</b> ve sonra <b>kimlikle</b>
/// okunuyor — okuma bir sorgu değil, bir belge getirme, ve orada filtrelenecek
/// bir <c>WHERE</c> yok. A grubunun kapsamıyla toplanmış bir paketi B grubundan
/// biri <c>GET /v1/rca/{id}</c> ile isteyebilseydi, içindeki her kanıt satırını
/// görürdü: özetler ham log gövdesi taşıyor.
/// </para>
///
/// <para>
/// Bu, bu depodaki en pahalı hata sınıfının kanıt katmanındaki hâli olurdu —
/// hata yok, sayaç yok, belirti yok; yalnızca başka grubun verisi.
/// </para>
/// </summary>
public sealed class EvidenceBundleScopeTests
{
    private static BundleScope Groups(params string[] groups) => new(groups, IsSystem: false);

    private static AccessScope Reader(params string[] groups) =>
        AccessScope.ForGroups("analyst", groups);

    [Fact]
    public void Ayni_kapsam_okuyabiliyor()
    {
        Assert.True(Groups("network/core").IsReadableBy(Reader("network/core")));
    }

    /// <summary>
    /// Okuyanın kapsamı <b>geniş</b> olabilir — paketin kapsamı onun alt kümesi.
    /// </summary>
    [Fact]
    public void Genis_kapsamli_okuyucu_dar_paketi_okuyabiliyor()
    {
        Assert.True(Groups("network/core").IsReadableBy(Reader("network/core", "network/edge")));
    }

    /// <summary>
    /// <b>Asıl korunan durum.</b> Başka grubun paketine erişilemiyor.
    /// </summary>
    [Fact]
    public void Baska_grubun_paketi_okunamiyor()
    {
        Assert.False(Groups("network/core").IsReadableBy(Reader("network/edge")));
    }

    /// <summary>
    /// <b>Kısmi kesişim yetmiyor.</b> İki gruplu bir paketin bir grubunu
    /// görebilen kişi paketi <b>okuyamıyor</b>: paket bölünebilir değil, içindeki
    /// kanıt iki grubun verisinden karışık ve satır bazında ayıklanamaz.
    /// </summary>
    [Fact]
    public void Kismi_kesisim_yetmiyor()
    {
        Assert.False(Groups("network/core", "network/edge").IsReadableBy(Reader("network/core")));
    }

    /// <summary>
    /// Sistem kapsamıyla toplanmış paket yalnızca <b>sınırsız</b> okuyucuya
    /// açık: içinde her grubun verisi olabilir ve sınırlı bir okuyucu onu
    /// daraltamaz.
    /// </summary>
    [Fact]
    public void Sistem_kapsamli_paketi_yalnizca_sinirsiz_okuyucu_okuyabiliyor()
    {
        var system = new BundleScope([], IsSystem: true);

        Assert.True(system.IsReadableBy(AccessScope.System("admin")));
        Assert.False(system.IsReadableBy(Reader("network/core")));
    }

    /// <summary>
    /// Sınırsız okuyucu her paketi okuyabiliyor — yönetici zaten her grubu
    /// görüyor.
    /// </summary>
    [Fact]
    public void Sinirsiz_okuyucu_her_paketi_okuyabiliyor()
    {
        Assert.True(Groups("network/core").IsReadableBy(AccessScope.System("admin")));
    }

    /// <summary>
    /// <b>Kapsamsız paket kimseye açılmıyor.</b> Boş grup listesi + sistem
    /// değil, tutarsız bir kayıt; "kısıt yok" diye okunması, kapsam kapısının
    /// tam tersine çalışması olurdu.
    /// </summary>
    [Fact]
    public void Bos_kapsamli_paket_sinirli_okuyucuya_acilmiyor()
    {
        Assert.False(new BundleScope([], IsSystem: false).IsReadableBy(Reader("network/core")));
    }

    /// <summary>
    /// Kapsamı boş olan okuyucu (yetkisi hiç çözülmemiş) hiçbir şey okuyamıyor —
    /// <c>AccessScope.Denied</c>'ın kanıt katmanındaki karşılığı.
    /// </summary>
    [Fact]
    public void Kapsamsiz_okuyucu_hicbir_seyi_okuyamiyor()
    {
        Assert.False(Groups("network/core").IsReadableBy(AccessScope.Denied));
    }
}
