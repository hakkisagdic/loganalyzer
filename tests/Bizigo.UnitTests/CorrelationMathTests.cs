using Bizigo.Contracts;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

/// <summary>
/// Korelasyonların istatistiği (T35).
///
/// <para>
/// Bu dosyanın varlık sebebi: <b>yanlış hesaplanmış bir z-score hiçbir yerde
/// hata vermez.</b> Sorgu koşar, rapor üretilir, hipotezler sıralanır ve
/// sıralama sessizce yanlış olur. Matematik SQL'den ayrı durduğu için elle
/// hesaplanmış değerlere karşı sabitlenebiliyor.
/// </para>
/// </summary>
public sealed class CorrelationMathTests
{
    /// <summary>
    /// Pencere uzunluğu düzeltmesi. Bu olmadan 7 günlük tabandaki 700 olay ile
    /// 45 dakikalık penceredeki 20 olay karşılaştırılamaz.
    /// </summary>
    [Fact]
    public void Taban_pencere_uzunluguna_olcekleniyor()
    {
        // 7 gün = 10.080 dk; 45 dk pencere → 700 × (45/10080) = 3,125
        var expected = CorrelationMath.ExpectedInWindow(
            700, TimeSpan.FromDays(7), TimeSpan.FromMinutes(45));

        Assert.Equal(3.125, expected, 3);
    }

    [Fact]
    public void Sifir_uzunlukta_pencere_sifir_bekleniyor()
    {
        Assert.Equal(0, CorrelationMath.ExpectedInWindow(700, TimeSpan.Zero, TimeSpan.FromMinutes(45)));
        Assert.Equal(0, CorrelationMath.ExpectedInWindow(700, TimeSpan.FromDays(7), TimeSpan.Zero));
    }

    /// <summary>
    /// Poisson: σ = √λ. Beklenen 4, gözlenen 12 → (12−4)/2 = 4.
    /// </summary>
    [Fact]
    public void Poisson_z_score_elde_hesaplananla_ayni()
    {
        Assert.Equal(4.0, CorrelationMath.PoissonZScore(12, 4), 6);
        Assert.Equal(0.0, CorrelationMath.PoissonZScore(4, 4), 6);

        // Düşüş de sapmadır; işaret korunuyor.
        Assert.Equal(-1.0, CorrelationMath.PoissonZScore(2, 4), 6);
    }

    /// <summary>
    /// <b>λ = 0 tuzağı.</b> Naif bir uygulama <c>√0 = 0</c> ile bölüp
    /// <c>Infinity</c> ya da <c>NaN</c> üretir; ikisi de sıralamada sessizce en
    /// tepeye veya en dibe düşer ve kimse sebebini görmez.
    ///
    /// <para>
    /// "Tabanda hiç yoktu" zaten <b>ilk-görülen imza</b> sinyalinin konusu ve
    /// orada çok daha iyi anlatılıyor. Hacim sapması burada susuyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Tabanda_hic_gorulmemis_imza_z_score_uretmiyor()
    {
        var z = CorrelationMath.PoissonZScore(50, 0);

        Assert.Equal(0, z);
        Assert.False(double.IsNaN(z));
        Assert.False(double.IsInfinity(z));
    }

    /// <summary>
    /// Lift oranların oranı: pencerede %50, tabanda %10 → 5×.
    /// </summary>
    [Fact]
    public void Lift_oranlarin_orani()
    {
        Assert.Equal(5.0, CorrelationMath.Lift(50, 100, 100, 1000), 6);
    }

    /// <summary>
    /// <b>Ham sayı karşılaştırması neden yetmez.</b> Pencerede her şey iki
    /// katına çıktıysa hiçbir değer "yoğunlaşmış" değildir; lift 1,0 kalmalı.
    /// Ham sayıya bakan bir uygulama burada "her değer öne çıktı" derdi.
    /// </summary>
    [Fact]
    public void Toplam_hacim_artisi_lift_uretmiyor()
    {
        Assert.Equal(1.0, CorrelationMath.Lift(200, 1000, 100, 500), 6);
    }

    /// <summary>
    /// Tabanda hiç görülmemiş değer <c>0</c> lift alıyor — "sonsuz lift" diye
    /// raporlamak tek bir olayı en güçlü kanıt yapardı.
    /// </summary>
    [Fact]
    public void Tabanda_olmayan_deger_sonsuz_lift_uretmiyor()
    {
        var lift = CorrelationMath.Lift(50, 100, 0, 1000);

        Assert.Equal(0, lift);
        Assert.False(double.IsInfinity(lift));
    }

    /// <summary>
    /// Düzenlilik eşiği sessizliğin yanlış alarm üretmemesinin tamamı: tabanda
    /// ayda bir satır yollayan bir kaynağın 45 dakika susması bulgu değil.
    /// </summary>
    [Fact]
    public void Seyrek_kaynak_duzenli_sayilmiyor()
    {
        var baseline = TimeSpan.FromDays(7);
        var window = TimeSpan.FromMinutes(45);

        // 7 günde 10 olay → pencere başına ~0,045: düzenli değil.
        Assert.False(CorrelationMath.WasRegular(10, baseline, window, minPerWindow: 5));

        // 7 günde 100.000 olay → pencere başına ~446: düzenli.
        Assert.True(CorrelationMath.WasRegular(100_000, baseline, window, minPerWindow: 5));
    }

    /// <summary>
    /// Yayılma gecikmeleri <b>ilk bozulana</b> göre; sıra zamana göre.
    /// </summary>
    [Fact]
    public void Yayilma_gecikmesi_ilk_bozulana_gore()
    {
        var t0 = new DateTimeOffset(2026, 8, 20, 14, 2, 0, TimeSpan.Zero);

        var ordered = CorrelationMath.WithLag(
        [
            new SourceOnset("g", "rtr-b", t0.AddSeconds(45), 3, 0),
            new SourceOnset("g", "rtr-a", t0, 5, 0),
            new SourceOnset("g", "rtr-c", t0.AddMinutes(2), 1, 0),
        ]);

        Assert.Equal(["rtr-a", "rtr-b", "rtr-c"], ordered.Select(o => o.Onset.SourceId));
        Assert.Equal(TimeSpan.Zero, ordered[0].Lag);
        Assert.Equal(TimeSpan.FromSeconds(45), ordered[1].Lag);
        Assert.Equal(TimeSpan.FromMinutes(2), ordered[2].Lag);
    }

    [Fact]
    public void Bos_yayilma_bos_donuyor() => Assert.Empty(CorrelationMath.WithLag([]));
}
