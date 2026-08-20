using System.Globalization;

namespace Bizigo.IntegrationTests;

/// <param name="Label">Eğriyi üreten fixture'ın adı — rapordaki sütun başlığı.</param>
/// <param name="ZipfExponent">Tohumlamada kullanılan sıklık yasası üssü.</param>
/// <param name="WindowSignatures">Olay penceresindeki ayrı imza — oranın paydası.</param>
/// <param name="Rows">Taban süpürmesinin satırları.</param>
public sealed record BaselineCurve(
    string Label,
    double ZipfExponent,
    int WindowSignatures,
    IReadOnlyList<BaselineSweepRow> Rows);

/// <param name="Baseline">
/// Okunabilir <b>tek</b> taban uzunluğu — <c>null</c> ise böyle bir sayı YOK.
///
/// <para>
/// Alanın <c>null</c> olabilmesi bir kolaylık değil, kapının kendisi: eğriler
/// ayrışıyorsa ortada seçilecek bir sayı yoktur ve raporun onu <i>basamaması</i>
/// gerekir. Bir uyarı satırı olsaydı, tabloya bakan kişi yine bir sayı bulur ve
/// onu alırdı.
/// </para>
/// </param>
/// <param name="Elbows">Fixture adı → o eğrinin dirseği (ölçülemediyse <c>null</c>).</param>
/// <param name="Reading">Sonucun tek cümlelik okunuşu; rapora aynen giriyor.</param>
public sealed record BaselineFixtureComparison(
    TimeSpan? Baseline,
    IReadOnlyList<(string Label, TimeSpan? Elbow)> Elbows,
    string Reading);

/// <summary>
/// <b>İki eğriyi karşılaştırıp "bu sayı bağlayıcı mı" sorusunu cevaplayan saf
/// fonksiyon</b> (T39 / D6).
///
/// <para>
/// <b>Neden tek eğri yetmiyor.</b> Süpürmenin baktığı veri sentetik ve yayılımı
/// bir <i>düğmeyle</i> kuruluyor: <c>bizigo seed golden --zipf</c>. Dirsek,
/// modelden çıkarılabildiği kadarıyla, fixture'ın <b>en nadir imzasının</b>
/// tekrar aralığı civarında oluşuyor — yani düğmenin doğrudan sonucu. Tek bir
/// eğri bunu asla gösteremez: tablo düzgün bir dirsek gösterir, sayı okunur ve
/// aslında ölçülen şey verinin karakteri değil tohumlama parametresidir.
/// </para>
///
/// <para>
/// <b>Neden imza iki eğri istiyor.</b> <see cref="Compare"/>'in iki zorunlu
/// konumsal parametresi var. Tavsiye olarak yazılsaydı — "iki kez koşturun" —
/// bir kez koşturulur ve unutulurdu; bu depoda tavsiye edilen kapıların
/// tutmadığı iki kez ölçüldü (Sigma ön kontrolü, <c>Produces</c> kapısı).
/// Derleyici hatırlatması hatırlatma değil, zorunluluktur.
/// </para>
///
/// <para>
/// <b>Ne reddediyor, ne reddetmiyor.</b> Bu tip eğrilerin <i>okunabilirliğine</i>
/// karışmıyor — o <see cref="BaselineSweepVerdict"/>'in işi ve her eğri ayrı ayrı
/// oradan geçiyor. Buradaki tek soru, iki okunabilir eğrinin <b>aynı</b> dirseği
/// gösterip göstermediği.
/// </para>
///
/// <para>
/// <b>Ayrışma bir arıza değil bir sonuç.</b> Dirsekler ayrışıyorsa ölçüm
/// başarısız olmadı — tam tersine, cevabı buldu: bu veri kümesi üretim için
/// bağlayıcı bir taban uzunluğu veremez. Bu yüzden ayrışma testi kırmızı
/// yakmıyor, yalnızca <see cref="BaselineFixtureComparison.Baseline"/>'ı
/// doğurmuyor. Kırmızı yakan bir kural, hiçbir fixture ile yeşile dönmeyeceği
/// için bekçi değil kalıcı bir engel olurdu.
/// </para>
/// </summary>
public static class BaselineFixtureVerdict
{
    /// <summary>
    /// Eğrinin <b>dirseği</b>: kendisinden sonra anlamlı bir düşüş kalmayan ilk
    /// ölçülebilen taban uzunluğu.
    ///
    /// <para>
    /// "Anlamlı"nın tanımı <see cref="BaselineSweepVerdict.MeaningfulDrop"/> —
    /// ikinci bir eşik yazmak, bir gün yalnızca birinin değişmesi ve iki
    /// fonksiyonun aynı tabloya farklı cevap vermesi demekti.
    /// </para>
    ///
    /// <para>
    /// <c>null</c> dönmesinin iki hâli var ve ikisi de <see cref="BaselineSweepVerdict.Reject"/>
    /// tarafından zaten yakalanıyor: ikiden az ölçülebilen nokta, ve eğrinin son
    /// noktada hâlâ düşüyor olması. Burada tekrar reddedilmiyorlar, yalnızca
    /// "dirsek yok" deniyor.
    /// </para>
    /// </summary>
    public static TimeSpan? Elbow(IReadOnlyList<BaselineSweepRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var measured = rows.Where(row => row.Status == BaselineLengthStatus.Measured).ToList();

        if (measured.Count < 2)
        {
            return null;
        }

        for (var start = 0; start < measured.Count - 1; start++)
        {
            if (!FallsAfter(measured, start))
            {
                return measured[start].Length;
            }
        }

        // Son adıma kadar düşüyor: düzleşmenin nerede başladığı bilinemez,
        // çünkü arşivin bittiği yerde başlamış olabilir.
        return null;
    }

    /// <summary>
    /// <b>İmza iki eğri istiyor</b> — bkz. sınıf notu. Üçüncü ve sonrası
    /// isteğe bağlı; daha çok düğme konumu daha güçlü kanıt.
    /// </summary>
    public static BaselineFixtureComparison Compare(
        BaselineCurve first,
        BaselineCurve second,
        params BaselineCurve[] rest)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(rest);

        var curves = new List<BaselineCurve> { first, second };
        curves.AddRange(rest);

        var elbows = curves.Select(curve => (curve.Label, Elbow: Elbow(curve.Rows))).ToList();

        if (elbows.Any(entry => entry.Elbow is null))
        {
            var missing = string.Join(
                ", ",
                elbows.Where(entry => entry.Elbow is null).Select(entry => entry.Label));

            return new BaselineFixtureComparison(
                Baseline: null,
                elbows,
                $"Şu eğrilerde dirsek yok: {missing}. Bir eğride dirsek bulunamadıysa " +
                "kıyaslanacak bir şey de yoktur; taban seçilemez.");
        }

        var distinct = elbows.Select(entry => entry.Elbow!.Value).Distinct().ToList();

        if (distinct.Count > 1)
        {
            var detail = string.Join(
                " · ",
                curves.Zip(elbows).Select(pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"zipf={pair.First.ZipfExponent:0.##}→{BaselineSweepVerdict.Describe(pair.Second.Elbow!.Value)}")));

            return new BaselineFixtureComparison(
                Baseline: null,
                elbows,
                $"Dirsek fixture'ın sıklık düğmesiyle KAYIYOR ({detail}). Yani bu " +
                "tabloda okunan şey verinin karakteri değil tohumlama parametresi. " +
                "Buradan üretim için bağlayıcı bir taban uzunluğu ÇIKMAZ — ölçümün " +
                "kanıtladığı şey mekanizmanın çalıştığı, sayının kendisi değil. " +
                "Bağlayıcı sayı gerçek müşteri verisiyle tekrarlanmalı.");
        }

        var elbow = distinct[0];

        return new BaselineFixtureComparison(
            elbow,
            elbows,
            $"Dirsek {BaselineSweepVerdict.Describe(elbow)}, denenen bütün sıklık " +
            "düğmelerinde AYNI — yani çevirebildiğimiz düğmeye karşı dayanıklı. " +
            "Bu sayıyı üretim için bağlayıcı yapmaz (örneklem hâlâ kataloğun altın " +
            "örnekleri), ama fixture'a bağlı olmadığı ölçüldü ve F3 bu tabanla " +
            "başlayabilir.");
    }

    /// <summary>
    /// <paramref name="start"/>'tan sonra anlamlı bir düşüş kalmış mı.
    ///
    /// <para>
    /// <b>Sonrasının tamamına</b> bakılıyor, yalnızca bir sonraki adıma değil:
    /// gürültülü bir eğride tek bir düz adım "düzleşti" gibi görünür ve
    /// ondan sonra düşüş devam eder. Dirseğin tanımı "buradan sonrası düz",
    /// "burada bir adım düz" değil.
    /// </para>
    /// </summary>
    private static bool FallsAfter(IReadOnlyList<BaselineSweepRow> measured, int start)
    {
        for (var i = start; i < measured.Count - 1; i++)
        {
            var previous = measured[i].NewRatio;
            var next = measured[i + 1].NewRatio;

            if (previous > 0 && (previous - next) / previous > BaselineSweepVerdict.MeaningfulDrop)
            {
                return true;
            }
        }

        return false;
    }
}
