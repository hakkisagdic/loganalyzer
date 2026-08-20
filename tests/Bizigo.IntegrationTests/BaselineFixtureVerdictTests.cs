namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>İki eğrinin karşılaştırılmasının bekçileri</b> (T39 / D6).
///
/// <para>
/// <see cref="BaselineSweepVerdictTests"/> ile aynı sebeple
/// <c>DevStackCollection</c>'a <b>ait değil</b>: ölçtüğü şey saf karar mantığı,
/// Docker'sız koşuyor. Ölçümün gövdesinde kalsaydı yalnızca elle,
/// Testcontainers'lı bir koşumda sınanabilirdi — yani pratikte hiç.
/// </para>
///
/// <para>
/// Burada sınanan iddia şu: <b>tek eğriden okunan dirsek bir sayı gibi görünür
/// ama tohumlama düğmesinin sonucu olabilir.</b> Karşılaştırma o ayrımı
/// yapıyor ve ayrım tutmazsa "seçilebilir taban" <b>doğmuyor</b> —
/// <see cref="BaselineFixtureComparison.Baseline"/> <c>null</c> kalıyor. Kapı
/// bir uyarı metni değil, değerin kendisinin yokluğu.
/// </para>
/// </summary>
public sealed class BaselineFixtureVerdictTests
{
    private static BaselineSweepRow Measured(double hours, double ratio) =>
        new(TimeSpan.FromHours(hours), BaselineLengthStatus.Measured, (int)(ratio * 100), ratio);

    private static BaselineSweepRow TooShort(double hours) =>
        new(TimeSpan.FromHours(hours), BaselineLengthStatus.ArchiveTooShort, 0, 0);

    private static BaselineCurve Curve(string label, double zipf, params BaselineSweepRow[] rows) =>
        new(label, zipf, WindowSignatures: 50, rows);

    /// <summary>
    /// Dirsek: kendisinden <b>sonrası</b> düz olan ilk nokta. Eğri 1sa→6sa→24sa
    /// boyunca düşüp orada duruyorsa cevap 24sa'tir.
    /// </summary>
    [Fact]
    public void Dirsek_dususun_bittigi_ilk_nokta()
    {
        var elbow = BaselineFixtureVerdict.Elbow(
        [
            Measured(1, 0.80),
            Measured(6, 0.50),
            Measured(24, 0.30),
            Measured(72, 0.295),
            Measured(168, 0.293),
        ]);

        Assert.Equal(TimeSpan.FromHours(24), elbow);
    }

    /// <summary>
    /// Tek düz adım dirsek değil. Gürültülü bir eğride 6sa→24sa arası yatay
    /// kalıp sonra düşüş devam edebiliyor; "buradan sonrası düz" tanımı
    /// yalnızca bir sonraki adıma bakarak kurulamaz.
    /// </summary>
    [Fact]
    public void Tek_duz_adim_dirsek_sayilmiyor()
    {
        var elbow = BaselineFixtureVerdict.Elbow(
        [
            Measured(1, 0.80),
            Measured(6, 0.79),   // düz adım — buraya takılmamalı
            Measured(24, 0.40),
            Measured(72, 0.39),
        ]);

        Assert.Equal(TimeSpan.FromHours(24), elbow);
    }

    /// <summary>
    /// Eğri son ölçülebilen noktada hâlâ düşüyorsa dirsek YOK: düzleşmenin
    /// nerede başladığı bilinemez, çünkü arşivin bittiği yerde başlamış
    /// olabilir. <c>BaselineSweepVerdict</c> bunu zaten reddediyor; burada
    /// yalnızca dirseğin uydurulmadığı doğrulanıyor.
    /// </summary>
    [Fact]
    public void Sonuna_kadar_dusen_egride_dirsek_yok()
    {
        var elbow = BaselineFixtureVerdict.Elbow(
        [
            Measured(1, 0.80),
            Measured(6, 0.60),
            Measured(24, 0.40),
            TooShort(72),
        ]);

        Assert.Null(elbow);
    }

    [Fact]
    public void Iki_olculebilen_noktadan_azi_dirsek_vermiyor()
    {
        Assert.Null(BaselineFixtureVerdict.Elbow([Measured(1, 0.4), TooShort(24)]));
    }

    /// <summary>
    /// <b>Asıl kapı.</b> Dirsek düğmeyle kayıyorsa seçilebilir taban
    /// <b>doğmuyor</b> — bir uyarı basılmıyor, değer <c>null</c> kalıyor.
    /// Uyarı olsaydı tabloya bakan kişi yine bir sayı bulur ve alırdı.
    /// </summary>
    [Fact]
    public void Dirsek_dugmeyle_kayiyorsa_secilebilir_taban_yok()
    {
        var steep = Curve("dik", 2.0,
            Measured(1, 0.90), Measured(6, 0.70), Measured(24, 0.50),
            Measured(72, 0.30), Measured(168, 0.295));

        var flat = Curve("düz", 1.4,
            Measured(1, 0.60), Measured(6, 0.30), Measured(24, 0.295),
            Measured(72, 0.293), Measured(168, 0.292));

        var comparison = BaselineFixtureVerdict.Compare(steep, flat);

        Assert.Null(comparison.Baseline);
        Assert.Contains("KAYIYOR", comparison.Reading, StringComparison.Ordinal);

        // Sebebin teşhis edilebilmesi için iki dirsek de raporda görünmeli.
        Assert.Equal(TimeSpan.FromHours(72), comparison.Elbows[0].Elbow);
        Assert.Equal(TimeSpan.FromHours(6), comparison.Elbows[1].Elbow);
    }

    /// <summary>
    /// Geri alma: aynı dirsek iki düğme konumunda da çıkıyorsa taban doğuyor.
    /// Kapının kırmızı yanabildiği kadar <b>yeşile de dönebildiği</b>
    /// ölçülmeden, kapının bir şey söylediği bilinemez.
    /// </summary>
    [Fact]
    public void Dirsek_dugmeye_dayanikliysa_taban_doguyor()
    {
        var steep = Curve("dik", 2.0,
            Measured(1, 0.90), Measured(6, 0.70), Measured(24, 0.30),
            Measured(72, 0.295), Measured(168, 0.294));

        var flat = Curve("düz", 1.4,
            Measured(1, 0.60), Measured(6, 0.45), Measured(24, 0.20),
            Measured(72, 0.198), Measured(168, 0.197));

        var comparison = BaselineFixtureVerdict.Compare(steep, flat);

        Assert.Equal(TimeSpan.FromHours(24), comparison.Baseline);
        Assert.Contains("AYNI", comparison.Reading, StringComparison.Ordinal);

        // Yine de üretim için bağlayıcı DEĞİL: örneklem hâlâ altın örnekler.
        Assert.Contains("bağlayıcı yapmaz", comparison.Reading, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bir eğride dirsek yoksa karşılaştırılacak bir şey de yok — "diğerinin
    /// dirseğini al" sessizce yanlış cevap olurdu.
    /// </summary>
    [Fact]
    public void Bir_egride_dirsek_yoksa_taban_yok()
    {
        var good = Curve("dik", 2.0,
            Measured(1, 0.90), Measured(6, 0.30), Measured(24, 0.295));

        var falling = Curve("düz", 1.4,
            Measured(1, 0.90), Measured(6, 0.60), Measured(24, 0.30));

        var comparison = BaselineFixtureVerdict.Compare(good, falling);

        Assert.Null(comparison.Baseline);
        Assert.Contains("düz", comparison.Reading, StringComparison.Ordinal);
    }
}
