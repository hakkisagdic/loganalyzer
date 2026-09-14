namespace Bizigo.Contracts.Security;

/// <summary>
/// Gölge sayacın <b>ne için durduğu</b> — T60'ın kararı.
///
/// <para>
/// <b>Neden bir enum.</b> T41 gölge katmanı *"sayı kabul edilebilir çıkarsa
/// ikinci turda maskelemeye terfi eder"* diye bıraktı, yani sayaç bir
/// <b>bekleyen kalem</b>di. T60 o turu koştu ve terfi etmedi. Buradan sonra
/// sayacın durması iki bambaşka anlam taşıyabiliyor:
/// </para>
///
/// <list type="bullet">
/// <item><b>Bir gün terfi için bekliyor</b> — o hâlde kapanmamış bir iş var,
/// ve *"gölge katman ne oldu"* sorusu her faz sonunda yeniden sorulmalı.</item>
/// <item><b>Kalıcı bir ölçüm</b> — o hâlde kapanacak bir iş yok ve soru bir
/// daha sorulmamalı; sayaç kendi başına bir ürün özelliği.</item>
/// </list>
///
/// <para>
/// İkisi tek gösterimde durursa *"bekleyen kalem listesi boşaldı mı"* sorusunun
/// cevabı asla evet olamaz — <c>CLAUDE.md</c> §8'in kalıbı, ve bu depoda aynı
/// ayrım üç kez daha kuruldu: <c>ProducesContractTests.Pending</c> ≠
/// <c>Exempt</c>, <c>EvidenceStatus.NotRegistered</c> ≠ <c>OutOfScope</c>,
/// <c>EpicStatusTests.KnownDivergence</c> ≠ <c>StructurallyUnaligned</c>.
/// </para>
///
/// <para>
/// <b>Bir yorum yetmezdi</b>: yorum okunmadan da doğru kalır. Bu değer kanıt
/// paketine <c>redaction_shadow_purpose</c> olarak yazılıyor, yani sayıyı altı
/// ay sonra okuyan taraf onun hangi soruyu cevapladığını <b>sayının yanında</b>
/// görüyor. Alan olmasaydı okuyan varsayılanı seçerdi ve varsayılan
/// <see cref="PromotionCandidate"/> olurdu — T41'in bıraktığı hâl.
/// </para>
/// </summary>
public enum ShadowLayerPurpose
{
    /// <summary>
    /// <b>Bugün kullanılmıyor.</b> Sayaç bir terfi kararını besliyor: eşik
    /// ayarlanır, sayı düşer, katman maskelemeye geçer.
    ///
    /// <para>
    /// Değerin burada durmasının sebebi ölçümün <b>geri alınabilir</b> olması:
    /// T60'ın kararı bir korpusa ve bir kanıt kümesine dayanıyor
    /// (<c>ShadowPromotionMeasurementTests</c>). Gerçek müşteri verisinde
    /// katman A'nın C+B'nin kaçırdığı bir sırrı yakaladığı <b>ölçülürse</b>
    /// karar yeniden açılır ve bu değere dönülür. O gün bu enum'ın var olması,
    /// dönüşün bir kod değişikliği olarak görünmesini sağlıyor.
    /// </para>
    /// </summary>
    PromotionCandidate,

    /// <summary>
    /// <b>T60'ın kararı.</b> Sayaç kalıcı bir ölçüm: terfi beklemiyor, kendi
    /// işini yapıyor — prompt'a giren metnin ne kadarının <b>opak</b> yüksek
    /// entropili malzeme olduğunu söylüyor.
    ///
    /// <para>
    /// Gerekçe ölçüldü (T60): entropi ekseninde ayıran bir eşik <b>yok</b> —
    /// sahte sır fixture'larındaki değerlerin hepsini aday yapan her eşik,
    /// altın korpusun adreslerini ve imza adlarını da aday yapıyor. Yani
    /// katman A'nın gölgede durma sebebi *"eşik henüz ölçülmedi"* değildi;
    /// <b>eksen yanlış</b>. Bir belirtecin hassas olup olmadığı kodlamasında
    /// yazmıyor, bağlamında yazıyor — ve bağlam katman C'nin ekseni.
    /// </para>
    /// </summary>
    PermanentInstrument,
}
