namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Reddetme protokolünün bekçileri</b> (T35).
///
/// <para>
/// Bu sınıf <c>DevStackCollection</c>'a <b>ait değil</b> ve bilerek: ölçtüğü
/// şey saf karar mantığı, dolayısıyla Docker'sız koşuyor. Karar mantığı
/// ölçümün gövdesinde kalsaydı yalnızca Testcontainers'lı bir koşumda
/// sınanabilirdi — yani pratikte hiç sınanmazdı, çünkü o koşum ayda birkaç kez
/// ve elle yapılıyor.
/// </para>
///
/// <para>
/// Ölçülen şeyin özü: <b>ölçemediğini fark edebilmek</b>. Aşağıdaki dört
/// durumun dördü de tabloda "düz bir eğri" olarak görünür ve düz eğri tam da
/// aranan cevabın biçimidir. Ayırt edilmezlerse ölçüm, arşivin sınırını
/// verinin karakteri diye raporlar.
/// </para>
/// </summary>
public sealed class BaselineSweepVerdictTests
{
    private static BaselineSweepRow Measured(double hours, double ratio) =>
        new(TimeSpan.FromHours(hours), BaselineLengthStatus.Measured, (int)(ratio * 100), ratio);

    private static BaselineSweepRow TooShort(double hours) =>
        new(TimeSpan.FromHours(hours), BaselineLengthStatus.ArchiveTooShort, 0, 0);

    [Fact]
    public void Bos_pencere_reddediliyor()
    {
        // Payda sıfırken her oran sıfır çıkar ve tablo KUSURSUZ düz görünür —
        // yani aranan dirsekle aynı biçimde. Ölçümün ilk hâli burada uyarı
        // basıp yeşil bitiyordu.
        var rejection = BaselineSweepVerdict.Reject(0, [Measured(1, 0), Measured(24, 0)]);

        Assert.NotNull(rejection);
        Assert.Contains("PAYDASI", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Arsiv_en_kisa_tabana_bile_ulasmiyorsa_reddediliyor()
    {
        var rejection = BaselineSweepVerdict.Reject(50, [TooShort(1), TooShort(24), TooShort(720)]);

        Assert.NotNull(rejection);
        Assert.Contains("EN KISA", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Tek_olculebilen_uzunluk_egri_sayilmiyor()
    {
        // Bir nokta bir eğri değil. Dirsek iki noktadan türetilemez ve tek
        // satırlık bir tablo "düşüş yok" diye de okunabilir.
        var rejection = BaselineSweepVerdict.Reject(50, [Measured(1, 0.40), TooShort(24), TooShort(720)]);

        Assert.NotNull(rejection);
        Assert.Contains("Tek nokta", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Oran_hic_dusmuyorsa_reddediliyor()
    {
        // Model "taban uzadıkça oran düşer" diyor. Düşmüyorsa ya taban aralığı
        // boş ya imzalar zamanla kararsız — ikisinde de okunacak dirsek yok.
        var rejection = BaselineSweepVerdict.Reject(
            50, [Measured(1, 0.40), Measured(24, 0.41), Measured(168, 0.42)]);

        Assert.NotNull(rejection);
        Assert.Contains("düşmüyor", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Egri_son_olculebilen_noktada_hala_dusuyorsa_reddediliyor()
    {
        // **En sinsi durum.** Ölçüm başarıyla koştu, tablo doldu, eğri düşüyor
        // — ama nerede düzleştiği görünmüyor. Düzleşme arşivin bittiği yerde
        // başlamış olabilir ve ikisi tabloda ayırt edilemez. Buradan taban
        // seçmek, arşivin sınırını verinin karakteri sanmak olur.
        var rejection = BaselineSweepVerdict.Reject(
            100, [Measured(1, 0.90), Measured(24, 0.60), Measured(168, 0.30), TooShort(720)]);

        Assert.NotNull(rejection);
        Assert.Contains("hâlâ düşüyor", rejection, StringComparison.Ordinal);
    }

    [Fact]
    public void Duzlesmis_egri_KABUL_ediliyor()
    {
        // Bekçinin ölçüsü: fazla hevesli olsaydı her koşumu reddederdi ve
        // ölçüm hiç yapılamazdı. Dirsek görünüyorsa geçiyor.
        var rejection = BaselineSweepVerdict.Reject(
            100,
            [
                Measured(1, 0.90),
                Measured(24, 0.50),
                Measured(168, 0.31),
                Measured(336, 0.305),
                Measured(720, 0.303),
            ]);

        Assert.Null(rejection);
    }

    [Fact]
    public void Dusus_esigi_MUTLAK_degil_goreli()
    {
        // %40→%38 ile %4→%2 aynı mutlak farkı taşır ama ikincisi oranın
        // yarıya inmesidir. Mutlak bir eşik, düşük hacimli veri kümelerinde
        // eğriyi olduğundan düz gösterir ve dirseği erken ilan ederdi.
        var lowVolume = BaselineSweepVerdict.Reject(
            100, [Measured(1, 0.20), Measured(24, 0.04), Measured(168, 0.02)]);

        Assert.NotNull(lowVolume);
        Assert.Contains("hâlâ düşüyor", lowVolume, StringComparison.Ordinal);

        var highVolume = BaselineSweepVerdict.Reject(
            100, [Measured(1, 0.60), Measured(24, 0.40), Measured(168, 0.39)]);

        Assert.Null(highVolume);
    }

    [Fact]
    public void Olculemeyen_uzunluklar_sayiliyor()
    {
        // Eksik satırı söylemeyen bir rapor, tam bir süpürme yapılmış
        // izlenimi bırakır.
        var rows = new List<BaselineSweepRow>
        {
            Measured(1, 0.5), Measured(24, 0.3), TooShort(336), TooShort(720),
        };

        Assert.Equal(2, BaselineSweepVerdict.UnmeasurableCount(rows));
    }
}
