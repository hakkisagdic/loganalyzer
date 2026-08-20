namespace Bizigo.IntegrationTests;

/// <summary>Bir taban uzunluğunun ölçülüp ölçülemediği.</summary>
public enum BaselineLengthStatus
{
    /// <summary>Arşiv bu uzunluğun sonuna kadar uzanıyor; sayı gerçek.</summary>
    Measured,

    /// <summary>
    /// Arşiv buraya ulaşmıyor. Bu satır için <b>sayı basılmamalı</b>.
    ///
    /// <para>
    /// Sebep, bu ölçümün en sinsi tuzağı: arşivin bittiği yerden sonra
    /// "ilk-görülen" oranı düşmeyi bırakır — çünkü taban uzamıyor, boş
    /// uzuyor. Eğri düzleşir ve o düzleşme <b>aranan dirsekle birebir aynı
    /// görünür</b>. Sayıyı basmak, arşivin sınırını verinin karakteri diye
    /// okutmak olurdu.
    /// </para>
    /// </summary>
    ArchiveTooShort,
}

/// <summary>Süpürmenin tek bir satırı.</summary>
/// <param name="Length">Denenen taban uzunluğu.</param>
/// <param name="Status">Arşiv bu uzunluğu karşılıyor mu.</param>
/// <param name="FirstSeen">Penceredeki "ilk-görülen" imza sayısı.</param>
/// <param name="NewRatio">Yeni oranı — 0..1.</param>
public sealed record BaselineSweepRow(
    TimeSpan Length,
    BaselineLengthStatus Status,
    int FirstSeen,
    double NewRatio);

/// <summary>
/// <b>Süpürmenin okunabilir olup olmadığına karar veren saf fonksiyon</b> (T35).
///
/// <para>
/// Neden ayrı bir tip ve neden saf: karar mantığı ölçümün içinde kalsaydı
/// yalnızca Testcontainers'lı bir koşumda sınanabilirdi, yani pratikte hiç
/// sınanmazdı. Burada Docker'sız sınanabiliyor — deponun
/// <c>DiscoveryWorker.NextBackoff</c>'ta izlediği yolun aynısı.
/// </para>
///
/// <para>
/// <b>Neden bir "uyarı" değil bir ret.</b> Ölçümün ilk hâli, ölçemediğinde
/// bir uyarı basıp <i>yeşil bitiyordu</i>. Yeşil bir koşum "ölçüm yapıldı"
/// diye okunur ve tabloya bakan kişi orada bir sayı arar. Bu, Sigma ön
/// kontrolünde bir turdur kapatılan hatanın aynısı: söz veren ama sözünü
/// tutmayan bir kapı, kapı değildir.
/// </para>
///
/// <para>
/// Reddetme <b>sonucu</b> değil <b>okunabilirliği</b> değerlendiriyor. Burada
/// hiçbir eşik iddia edilmiyor; hangi tabanın seçileceği hâlâ sayıya bakan
/// insanın kararı. Reddedilen tek şey, <i>okunacak bir sayı olmadığı hâlde
/// sayı varmış gibi görünmesi</i>.
/// </para>
/// </summary>
public static class BaselineSweepVerdict
{
    /// <summary>
    /// İki uzunluk arasındaki oran düşüşünün "anlamlı" sayılması için gereken
    /// göreli fark.
    ///
    /// <para>
    /// Göreli, mutlak değil: %40'tan %38'e düşüş ile %4'ten %2'ye düşüş aynı
    /// mutlak farkı taşır ama ikincisi oranın yarıya inmesidir. Mutlak bir
    /// eşik, düşük hacimli veri kümelerinde eğriyi olduğundan düz gösterirdi.
    /// </para>
    ///
    /// <para>
    /// <c>internal</c>, <c>private</c> değil: <see cref="BaselineFixtureVerdict"/>
    /// dirseği ararken <b>aynı</b> eşiği kullanmak zorunda. İkinci bir sabit
    /// yazmak, bir gün yalnızca birinin değişmesi ve iki fonksiyonun aynı
    /// tabloya farklı cevap vermesi demekti.
    /// </para>
    /// </summary>
    internal const double MeaningfulDrop = 0.05;

    /// <summary>
    /// Süpürme okunabilir mi. Okunabiliyorsa <c>null</c>, değilse <b>sebep</b>.
    ///
    /// <para>
    /// Sebep dönmek, <c>bool</c> dönmekten farklı: reddedilen bir koşumda
    /// operatörün ne yapacağını bilmesi gerekiyor ve "ölçüm anlamsız" tek
    /// başına bunu söylemiyor. Her ret dalı ne yapılacağını yazıyor.
    /// </para>
    /// </summary>
    public static string? Reject(int windowSignatures, IReadOnlyList<BaselineSweepRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (windowSignatures <= 0)
        {
            return "Olay penceresinde hiç imzalı olay yok, yani oranın PAYDASI sıfır. " +
                   "Her taban uzunluğu için 'ilk-görülen' de sıfır çıkar ve tablo " +
                   "kusursuz düz bir eğri gösterir — aranan dirsekle aynı biçimde. " +
                   "Daha yoğun bir olay penceresi seçin ya da önce veri yükleyin.";
        }

        var measured = rows.Where(row => row.Status == BaselineLengthStatus.Measured).ToList();

        if (measured.Count == 0)
        {
            return "Arşiv, denenen EN KISA tabanın sonuna bile ulaşmıyor. Ölçüm, " +
                   "taban uzunluğunu değil arşivin uzunluğunu ölçerdi. En az taban " +
                   "süresi kadar geçmiş yükleyin (`bizigo seed golden --span-days`).";
        }

        if (measured.Count == 1)
        {
            return $"Yalnızca tek bir taban uzunluğu ({Describe(measured[0].Length)}) " +
                   "ölçülebildi. Tek nokta bir eğri değil; dirsek iki noktadan " +
                   "türetilemez. Arşivi uzatın ya da daha kısa tabanlar ekleyin.";
        }

        // Model şunu söylüyor: taban uzadıkça daha çok imza "daha önce
        // görülmüş" olur, yani oran DÜŞER. Hiç düşmüyorsa model tutmuyor
        // demektir ve sebebi burada bilinemez — ama okunacak bir dirsek de yok.
        var first = measured[0].NewRatio;
        var last = measured[^1].NewRatio;

        if (last >= first)
        {
            return $"Oran taban uzadıkça düşmüyor ({first:P1} → {last:P1}). Beklenen " +
                   "davranış bu değil: uzun taban daha çok imzayı 'görülmüş' yapmalı. " +
                   "Ya arşiv taban aralığında boş, ya imzalar zamanla kararsız " +
                   "(ör. maskelenmemiş bir alan her gün yeni `signature_hash` üretiyor). " +
                   "Bu tabloda dirsek aramak, olmayan bir eğriyi okumaktır.";
        }

        // En sinsi durum: eğri SON ölçülebilen noktada hâlâ düşüyor.
        //
        // O zaman düzleşmenin nerede başladığı bilinemez — düzleşme arşivin
        // bittiği yerde başlamış olabilir ve ikisi tabloda birbirinden
        // ayırt edilemez. Bu, "ölçüm yapıldı ama sonucu okunamaz" hâli ve
        // sessizce geçerse yanlış bir dirsek seçilir.
        var previous = measured[^2].NewRatio;
        var stillFalling = previous > 0 && (previous - last) / previous > MeaningfulDrop;

        if (stillFalling)
        {
            return $"Eğri en uzun ÖLÇÜLEBİLEN tabanda ({Describe(measured[^1].Length)}) " +
                   $"hâlâ düşüyor ({previous:P1} → {last:P1}). Dirsek bu aralığın " +
                   "DIŞINDA; buradan bir taban seçmek, arşivin sınırını verinin " +
                   "karakteri sanmak olur. Daha uzun bir arşivle tekrarlayın.";
        }

        return null;
    }

    /// <summary>
    /// Süpürmede kaç uzunluğun arşiv yüzünden ölçülemediği — rapora yazılıyor.
    ///
    /// <para>
    /// Sıfırdan büyük olması ölçümü geçersiz kılmıyor ama <b>görünmesi</b>
    /// gerekiyor: tabloda eksik satır olduğunu söylemeyen bir rapor, tam bir
    /// süpürme yapılmış izlenimi bırakır.
    /// </para>
    /// </summary>
    public static int UnmeasurableCount(IReadOnlyList<BaselineSweepRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows.Count(row => row.Status == BaselineLengthStatus.ArchiveTooShort);
    }

    internal static string Describe(TimeSpan length) =>
        length < TimeSpan.FromDays(1)
            ? FormattableString.Invariant($"{length.TotalHours:0}sa")
            : FormattableString.Invariant($"{length.TotalDays:0}g");
}
