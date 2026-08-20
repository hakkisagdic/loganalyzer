using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Bir RCA raporunun <b>insan değerlendirmesi</b> (T37, RCA §7).
///
/// <para>
/// <b>Neden bu tablo T37'de açılıyor:</b> ekrandaki üç düğme
/// (<i>doğru / kısmen / yanlış</i>) bir yere yazmıyorsa <b>yalan söyleyen bir
/// düğmedir</b>. Kullanıcı "doğru" der, hiçbir şey olmaz, ve RCA belgesinin 2.
/// riski — inceleme yorgunluğu — basılmayan düğmelerle değil <b>basılan</b>
/// düğmelerle gerçekleşir. Bu, hiç düğme koymamaktan kötü: kullanıcı katkı
/// verdiğini sanır.
/// </para>
///
/// <para>
/// <b>Neden pakete bağlı, rapora değil:</b> saklanan şey paket
/// (<see cref="EvidenceBundleEntity"/>); rapor ondan deterministik olarak
/// türetiliyor. İnceleme "bu kanıt neyi gösteriyordu" sorusunun cevabı ve
/// F4'te aynı paket üzerinde farklı model koşturulduğunda <b>değişmemesi
/// gereken</b> taraf o. Rapora bağlasaydık, her yeniden üretimde incelemenin
/// hangi rapora ait olduğu belirsizleşirdi.
/// </para>
///
/// <para>
/// <b>Altın küme akışı burada değil — T38.</b> Bu tablo o akışın <b>girdisi</b>:
/// <c>(kanıt paketi, gerçek kök neden)</c> çiftleri buradan doğuyor. T37'nin
/// işi çifti kaydetmek; alarm kapatmayla bağlamak ve kalite ölçümü T38'in.
/// </para>
/// </summary>
[Table("evidence_reviews")]
public sealed class EvidenceReviewEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// İncelenen kanıt paketi.
    ///
    /// <para>
    /// <b>Tekil değil, bilerek.</b> Aynı paket birden çok kez incelenebilir:
    /// ilk bakan "kısmen" der, kök neden sonradan anlaşılınca ikinci bir kayıt
    /// düşer. Son sözü söyleyen <see cref="ReviewedAt"/>'e göre en yeni kayıt.
    /// Üzerine yazmak, incelemenin <b>değiştiğini</b> — ki bu kalite ölçümü için
    /// bir veri — silerdi.
    /// </para>
    /// </summary>
    public Guid BundleId { get; set; }

    public DateTimeOffset ReviewedAt { get; set; }

    /// <summary>
    /// <c>correct</c> / <c>partially</c> / <c>wrong</c> — RCA §4.2'nin
    /// <c>review.state</c>'i. Dizgi olarak duruyor, enum olarak değil:
    /// saklanan bir kayıt ve enum değerlerinin sayısal karşılığı bir gün
    /// kaydırılırsa geçmiş satırlar sessizce başka bir şey ifade ederdi.
    /// </summary>
    [MaxLength(32)]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// İnceleyen — token'daki özne. Kim olduğu bilinmeden inceleme bir ölçüm
    /// değil anonim bir oy olurdu.
    /// </summary>
    [MaxLength(256)]
    public string Reviewer { get; set; } = string.Empty;

    /// <summary>
    /// <b>Gerçek kök neden</b> — altın kümenin asıl değerli yarısı (RCA §7.2).
    ///
    /// <para>
    /// "Yanlış" demek modeli düzeltmiyor; <b>doğrusunun ne olduğu</b> düzeltiyor.
    /// Boş bırakılabilir: zorunlu yapmak, acelesi olan kullanıcının düğmeye hiç
    /// basmamasına yol açar ve o zaman elimizde ne oy ne kök neden kalır.
    /// </para>
    /// </summary>
    [MaxLength(4000)]
    public string ActualRootCause { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Note { get; set; } = string.Empty;
}
