using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Üretilen RCA belgesi (T51, RCA §4.2).
///
/// <para>
/// <b>Sınır ve neden pazarlığa açık değil.</b> <c>rca_runs</c> koşumun başına
/// gelen her şeyin sahibi (kabul, ret, yürütme, sonuç); bu tablo
/// <b>üretilen belgenin</b> sahibi (metin, cümle atfı, atılan cümle sayısı).
/// İkisi ayrı, çünkü <i>"kota mı, boş mu"</i> sorusu bölünmüş hâlde bir
/// <c>join</c>'e dönerdi ve join'in iki sessiz hâli var: satır ikisinde birden
/// ya da hiçbirinde. İkisi de belirti üretmez — ekran bir şey gösterir, yanlış
/// olduğunu kimse görmez.
/// </para>
///
/// <para>
/// <b>Bu yüzden burada statü benzeri bir kolon YOK</b> ve olmayacağını
/// <c>RcaReportStatusGuardTests</c> tutuyor. Bekçi T46'da yazıldı; T44'e kadar
/// denetleyecek tek bir tip bulamadığı için <b>boş küme üzerinde yeşil
/// yanıyordu</b>.
/// </para>
///
/// <para>
/// <b>Neden belge + üst veri, ikisi birden</b> — <c>evidence_bundles</c>'ın
/// kalıbı ve aynı gerekçe. Rapor bütün olarak okunuyor, kimse "raporlar
/// arasında güveni yüksek bulgular" diye sormuyor; ilişkisel bir alt tablo her
/// şema göçünde geçmiş satırlara <c>NULL</c> kolonlar ekleyip eski raporları
/// sessizce farklı bir şekle sokardı.
/// </para>
///
/// <para>
/// Üst veri kolonları o kuralın <b>ölçülmüş istisnası</b>: T47 altın küme
/// üzerinde atılan cümle oranını hesaplayacak ve bunu her satır için JSON
/// açmadan yapabilmeli. Kopya oldukları bilinçli, tek yazan taraf var (depo),
/// ve bir test ikisinin ayrışmadığını tutuyor.
/// </para>
/// </summary>
[Table("rca_reports")]
public sealed class RcaReportEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Hangi kanıt paketinden üretildi.
    ///
    /// <para>
    /// <b>Tekil DEĞİL</b>, ve bu RCA §3'ün kendi gerekçesi: aynı paket üzerinde
    /// farklı model ya da farklı prompt koşturmak <i>beklenen</i> bir iş —
    /// model değiştiğinde regresyon testi buradan çıkıyor. Tekil yapmak, o
    /// karşılaştırmayı tanım gereği imkânsız kılardı.
    /// </para>
    /// </summary>
    public Guid BundleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Belge biçiminin sürümü — kolon olarak, <b>belgeyi açmadan</b>
    /// okunabilsin diye. <c>evidence_bundles</c>'daki gerekçenin aynısı:
    /// okunamayan bir kaydı bulmanın tek yolu onu okumaya çalışmak olmamalı.
    /// </summary>
    public int SchemaVersion { get; set; }

    [MaxLength(200)]
    public string ScenarioId { get; set; } = string.Empty;

    [MaxLength(40)]
    public string ScenarioVersion { get; set; } = string.Empty;

    // --- Karar 1'in sayıları -----------------------------------------------
    // "Model 12 cümle üretti, 3'ü kanıta bağlanamadı ve çıkarıldı." Üçü de
    // kolon, çünkü F4'ün kalite göstergesi bunlar ve T47 onları toplu
    // sorgulayacak.

    /// <summary>
    /// Karar 1'in <b>paydası</b>. Kolon olması şart: pay tek başına bir oran
    /// değil, ve paydasız iki raporun kalitesi karşılaştırılamaz.
    /// </summary>
    public int ProducedSentenceCount { get; set; }

    /// <summary>Karar 1'in <b>payı</b> — rapora girmeyen cümle sayısı.</summary>
    public int DroppedSentenceCount { get; set; }

    /// <summary>
    /// Atıf <b>uydurmuş</b> cümleler; <see cref="DroppedSentenceCount"/>'un alt
    /// kümesi. Ayrı tutuluyor çünkü <i>"hiç atıf yapmadı"</i> ile <i>"atıf
    /// uydurdu"</i> iki farklı kalite sorunu: biri prompt'un, diğeri modelin.
    /// İkisi tek sayıya inseydi T47 hangisine karar vereceğini bilemezdi.
    /// </summary>
    public int FabricatedCitationSentenceCount { get; set; }

    /// <summary>Belgenin tamamı — <c>ReasoningSerializer</c> biçiminde.</summary>
    [Column(TypeName = "jsonb")]
    public string Payload { get; set; } = string.Empty;
}
