using Bizigo.Contracts;

namespace Bizigo.Evidence;

/// <summary>
/// Korelasyonların istatistiği — <b>saf fonksiyonlar</b>, veritabanı yok.
///
/// <para>
/// SQL'den ayrı durmasının tek sebebi test edilebilirlik. Yanlış hesaplanmış bir
/// z-score hiçbir yerde hata vermez: sorgu koşar, rapor üretilir, hipotezler
/// sıralanır ve sıralama sessizce yanlış olur. Burada durduğu için her biri
/// elle hesaplanmış değerlere karşı sabitlenebiliyor.
/// </para>
/// </summary>
public static class CorrelationMath
{
    /// <summary>
    /// Taban sayımını pencere uzunluğuna göre <b>ölçekler</b>.
    ///
    /// <para>
    /// Bu düzeltme olmadan sinyal anlamsız: 7 günlük tabandaki 700 olay ile 45
    /// dakikalık penceredeki 20 olay doğrudan karşılaştırılamaz. Bölmeyi SQL'e
    /// gömmemenin sebebi de bu — gömülü olsaydı yapılıp yapılmadığı görünmezdi.
    /// </para>
    /// </summary>
    public static double ExpectedInWindow(long baselineCount, TimeSpan baselineLength, TimeSpan windowLength)
    {
        if (baselineLength <= TimeSpan.Zero || windowLength <= TimeSpan.Zero)
        {
            return 0;
        }

        return baselineCount * (windowLength / baselineLength);
    }

    /// <summary>
    /// Poisson z-score: gözlenen sayının beklenenden kaç standart sapma uzakta
    /// olduğu.
    ///
    /// <para>
    /// Poisson dağılımında varyans ortalamaya eşit, yani standart sapma
    /// <c>√λ</c>. Log hacmi için makul bir yaklaşım: olaylar bağımsız gelir ve
    /// oran görece sabittir.
    /// </para>
    ///
    /// <para>
    /// <b>λ = 0 durumu ayrı ele alınıyor</b> ve bu bir tuzak. Tabanda hiç
    /// görülmemiş bir imza için <c>√0 = 0</c> ve bölme sonsuza gider; naif bir
    /// uygulama <c>NaN</c> ya da <c>Infinity</c> üretir, o da sıralamada sessizce
    /// en tepeye ya da en dibe düşer. Ama "tabanda hiç yoktu" zaten
    /// <b>ilk-görülen imza</b> sinyalinin konusu ve orada çok daha iyi
    /// anlatılıyor. Burada <c>0</c> dönüyor: hacim sapması o imza hakkında
    /// söyleyecek bir şeyi olmadığını söylüyor, uydurmuyor.
    /// </para>
    /// </summary>
    public static double PoissonZScore(long observed, double expected)
    {
        if (expected <= 0)
        {
            return 0;
        }

        return (observed - expected) / Math.Sqrt(expected);
    }

    /// <summary>
    /// Lift: bir alan değerinin pencerede, tabandakine göre kaç kat yoğunlaştığı.
    ///
    /// <para>
    /// <c>(pencerede bu değer / pencere toplamı) ÷ (tabanda bu değer / taban
    /// toplamı)</c>. 1.0 "hiç değişmemiş", 8.4 "sekiz kattan fazla yoğunlaşmış"
    /// demek — RCA raporundaki <c>upstream=core-sw-02 (lift 8.4×)</c> satırı bu.
    /// </para>
    ///
    /// <para>
    /// Oranların oranı olması önemli: ham sayı karşılaştırması, pencerede toplam
    /// hacim arttığında <b>her</b> değeri yükseltirdi ve ortak öznitelik diye
    /// gösterdiği şey aslında "her şey arttı" olurdu.
    /// </para>
    ///
    /// <para>
    /// Tabanda hiç görülmemiş bir değer için <c>0</c> dönüyor — bölme sonsuza
    /// gitmesin diye. Bu, sinyalin o değer hakkında konuşmadığı anlamına geliyor;
    /// "sonsuz lift" diye raporlamak, tek bir olayı en güçlü kanıt yapardı.
    /// </para>
    /// </summary>
    public static double Lift(long windowCount, long windowTotal, long baselineCount, long baselineTotal)
    {
        if (windowTotal <= 0 || baselineTotal <= 0 || baselineCount <= 0)
        {
            return 0;
        }

        var windowShare = (double)windowCount / windowTotal;
        var baselineShare = (double)baselineCount / baselineTotal;

        return baselineShare <= 0 ? 0 : windowShare / baselineShare;
    }

    /// <summary>
    /// Bir kaynağın tabanda <b>düzenli</b> gönderip göndermediği.
    ///
    /// <para>
    /// Sessizlik sinyalinin yanlış alarm üretmemesi buna bağlı: tabanda ayda bir
    /// satır yollayan bir kaynağın pencerede susması bir bulgu değil, normal.
    /// Ölçüt basit ve açık: taban boyunca beklenen pencere başına olay sayısı
    /// eşiği geçmeli.
    /// </para>
    /// </summary>
    public static bool WasRegular(long baselineCount, TimeSpan baselineLength, TimeSpan windowLength, double minPerWindow) =>
        ExpectedInWindow(baselineCount, baselineLength, windowLength) >= minPerWindow;

    /// <summary>
    /// Yayılma sırasında bir kaynağın ilk bozulmasının, <b>ilk bozulan</b>
    /// kaynağa göre gecikmesi. Sıralamanın okunabilir hâli.
    /// </summary>
    public static IReadOnlyList<(SourceOnset Onset, TimeSpan Lag)> WithLag(IReadOnlyList<SourceOnset> onsets)
    {
        ArgumentNullException.ThrowIfNull(onsets);

        if (onsets.Count == 0)
        {
            return [];
        }

        var first = onsets.Min(o => o.FirstDegradedAt);

        return [.. onsets
            .OrderBy(o => o.FirstDegradedAt)
            .ThenBy(o => o.SourceId, StringComparer.Ordinal)
            .Select(o => (o, o.FirstDegradedAt - first))];
    }
}
