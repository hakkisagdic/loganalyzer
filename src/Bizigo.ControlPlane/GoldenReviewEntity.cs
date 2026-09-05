using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// İnsanın kanıt paketi hakkındaki kararı (T38).
///
/// <para>
/// <b>Bilmiyorum neden bir seçenek:</b> inceleme zorunlu — alarm kapatan
/// kullanıcı atlayamıyor. Zorunlu bir soruda kaçış yoksa insanlar rastgele
/// seçip geçer ve altın küme sessizce gürültüyle dolar. Bu, ölçülemez olmaktan
/// <b>kötü</b>: ölçülüyormuş gibi görünür.
/// </para>
///
/// <para>
/// Karşılığı aritmetikte: <see cref="Unknown"/> doğruluk oranının
/// <b>paydasına girmiyor</b>, ve kendi oranı üçüncü bir gösterge olarak
/// duruyor — yüksekse ya kanıt paketi yetersiz ya soru yanlış soruluyor.
/// </para>
/// </summary>
public enum ReviewVerdict
{
    /// <summary>Rapor doğruydu.</summary>
    Correct = 0,

    /// <summary>Rapor yanlıştı.</summary>
    Wrong = 1,

    /// <summary>Rapor yanlış değildi ama eksikti.</summary>
    Incomplete = 2,

    /// <summary>Karar verilemedi. Orana <b>girmiyor</b>, ayrı sayılıyor.</summary>
    Unknown = 3,
}

/// <summary>
/// Çelişen kanıt bölümü hakkında ayrı karar (RCA riski #5, "çelişen kanıt
/// tiyatrosu").
///
/// <para>
/// Ayrı bir alan olmasının sebebi: model, çelişen kanıt alanını doldurmak için
/// önemsiz bir şey uydurabilir ve rapor <b>bütün olarak</b> hâlâ doğru
/// görünebilir. Tek bir "doğru muydu?" sorusu bu boyutu ölçemez.
/// </para>
///
/// <para>
/// Alan <b>bugün</b> açılıyor, kullanımı F4'te. Sonradan eklenseydi geçmiş
/// kayıtlar onu taşımaz ve altın kümenin en eski yarısı bu boyutta kör kalırdı.
/// </para>
/// </summary>
public enum ContradictingEvidenceVerdict
{
    /// <summary>
    /// <b>Kimse söylemedi</b> — alan doldurulmadı.
    ///
    /// <para>
    /// Sıfır olması bilinçli ve tek amacı bu: <c>default</c> bir değerin
    /// <b>anlam taşımaması</b>. Önceki hâlde varsayılan <see cref="NotPresent"/>
    /// idi, yani alanı doldurmayan bir çağıran sessizce <i>"bölüm yoktu"</i>
    /// diyordu — ve o cümle tiyatro oranının paydasını etkiliyor.
    /// </para>
    ///
    /// <para>
    /// Bu deponun en pahalı <b>önlenmiş</b> hatası aynı sınıftandı: EF'in
    /// ürettiği <c>enabled → status</c> göçü her pasif kuralı sessizce
    /// açıyordu, çünkü varsayılan bir değer bir anlam taşıyordu.
    /// </para>
    ///
    /// <para>
    /// Paydaya <b>girmiyor</b> ve <see cref="NotPresent"/>'tan ayrı sayılıyor:
    /// <i>"bölüm yoktu"</i> ile <i>"kimse söylemedi"</i> farklı şeyler.
    /// </para>
    /// </summary>
    Unspecified = 0,

    /// <summary>Bu pakette çelişen kanıt bölümü yoktu — <b>açık</b> bir karar.</summary>
    NotPresent = 1,

    /// <summary>Vardı ve yerindeydi.</summary>
    Sound = 2,

    /// <summary>Vardı ama önemsizdi — alanı doldurmak için üretilmiş.</summary>
    Trivial = 3,

    /// <summary>Değerlendirilemedi.</summary>
    Unknown = 4,
}

/// <summary>
/// Altın küme kaydı (T38).
///
/// <para>
/// <b>Neden ilişkisel, T36'nın paketi gibi belge değil:</b> paket <i>bütün
/// olarak</i> okunuyor, inceleme ise <b>alan üzerinde toplanıyor</b>. Kalite
/// göstergesi doğruluk oranı hesaplıyor; belge yolunda her gösterge sorgusu
/// JSON açardı. T36'nın "ilişkisel alt tablo her göçte geçmiş satırlara NULL
/// kolon ekler" gerekçesi orada doğru, burada geçerli değil.
/// </para>
///
/// <para>
/// <b>Neden kendi <see cref="OwnerGroup"/> kolonu var:</b>
/// <c>EvidenceBundleEntity</c>'de kapsam yalnızca JSON gövdesindeki
/// <c>BundleScope</c>'ta duruyor — yani sorgulanamaz bir yerde. Kapsam
/// filtresini pakete <c>JOIN</c> atıp JSON açmaya bağlamak, K17'nin kapısını
/// sorgu planının insafına bırakmak olurdu.
/// </para>
/// </summary>
[Table("golden_reviews")]
public sealed class GoldenReviewEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// İncelemenin dayandığı kanıt paketi. <b>Zorunlu.</b>
    ///
    /// <para>
    /// F4'ün karşılaştırması pakete bakacak; paketsiz bir inceleme F4'te
    /// <b>ölçülemez</b>. Kapatma anında paket yoksa kapatma paket üretimini
    /// tetikliyor — inceleme kaydı paketsiz yazılamıyor.
    /// </para>
    /// </summary>
    public Guid BundleId { get; set; }

    /// <summary>
    /// İncelemeyi doğuran alarm tetiklenmesi. <b>İsteğe bağlı:</b> kullanıcı
    /// tetikli RCA'da alarm yok.
    /// </summary>
    public Guid? TriggerId { get; set; }

    /// <summary>
    /// Kapsam grubu — kendi kolonu (yukarıdaki gerekçe). Kaydı yazan kapsamdan
    /// geliyor, paketten değil.
    /// </summary>
    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    public ReviewVerdict Verdict { get; set; }

    public ContradictingEvidenceVerdict ContradictingEvidence { get; set; }

    /// <summary>İnceleyenin serbest notu. F4 bunu prompt değerlendirmesinde okuyacak.</summary>
    [MaxLength(4096)]
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// İnceleyenin bildiği gerçek kök neden — rapor yanlışsa <b>doğrusu</b>.
    ///
    /// <para>
    /// <c>Note</c>'tan ayrı bir alan, çünkü ayrı bir soruyu cevaplıyor: not
    /// *"neden böyle düşünüyorum"*, bu alan *"cevap neydi"*. F4'ün
    /// model-insan karşılaştırması ikincisini okuyacak ve serbest notun
    /// içinden ayıklamak zorunda kalırsa karşılaştırma bir ayrıştırma
    /// işine dönüşür.
    /// </para>
    ///
    /// <para>
    /// Boş olabilir ve boşluğu bilgi: <c>Verdict</c> <c>Correct</c> ise
    /// zaten doldurulacak bir şey yok; <c>Wrong</c> olup burası boşsa
    /// inceleyen yanlışı görmüş ama doğrusunu bilmiyor demektir — F4 için
    /// ayrı bir sinyal.
    /// </para>
    /// </summary>
    [MaxLength(4096)]
    public string ActualRootCause { get; set; } = string.Empty;

    /// <summary>OIDC <c>sub</c>. Audit ve "kim ne kadar inceledi" için.</summary>
    [MaxLength(256)]
    public required string ReviewerSubject { get; set; }

    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Kayıt biçiminin sürümü.
    ///
    /// <para>
    /// İlişkisel olmasına rağmen taşınıyor: F4 alanları (model çıktısı,
    /// model-insan karşılaştırması) göç ile eklenecek ve o gün eski satırların
    /// <b>hangi şemayla yazıldığı</b> bilinmek zorunda. Kolonun varlığı ile
    /// doldurulmuş olması ayrı şeyler; sürüm olmadan ikisi ayırt edilemez.
    /// </para>
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Bugün yazılan inceleme sürümü.
    ///
    /// <para>
    /// <b>2 (T47):</b> <see cref="CorrectFindingRank"/> eklendi ve
    /// <see cref="ContradictingEvidenceVerdict.Unspecified"/> açıldı. Sürüm
    /// artışı süs değil <b>paydanın ayırıcısı</b>: <c>CorrectFindingRank</c>
    /// <see langword="null"/> iken *"hiçbir bulgu doğru değildi"* demek, ama
    /// sürüm 1 satırlarında aynı <see langword="null"/> *"soru hiç
    /// sorulmadı"* demek. Kolonun varlığı ile doldurulmuş olması ayrı şeyler
    /// ve bu alan tam olarak bu gün için taşınıyordu.
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Rank sorusunun sorulmaya <b>başladığı</b> sürüm. <c>accuracy@k</c>'nın
    /// paydası bu eşiği kullanıyor; sabitin adı olması, eşiğin ölçüm kodunda
    /// çıplak bir <c>2</c> olarak durmasını engelliyor.
    /// </summary>
    public const int RankSchemaVersion = 2;

    /// <summary>
    /// İnceleyene göre <b>kaçıncı</b> bulgu doğruydu — 1 tabanlı, sıralı
    /// <c>findings[]</c> listesindeki konum.
    ///
    /// <para>
    /// <c>accuracy@1</c> ve <c>accuracy@3</c> <b>bu tek alandan</b> çıkıyor
    /// (<c>== 1</c> ve <c>&lt;= 3</c>); ikinci bir eksen açılmıyor.
    /// </para>
    ///
    /// <para>
    /// <see langword="null"/> = <b>hiçbir bulgu doğru değildi</b> — bu bir
    /// ölçüm, eksik veri değil. *"Soru sorulmadı"* hâli buradan değil
    /// <see cref="SchemaVersion"/>'dan okunuyor; ikisini tek
    /// <see langword="null"/>'a indirmek, bu ölçümün engellemek için var
    /// olduğu hatanın kendisi olurdu.
    /// </para>
    ///
    /// <para>
    /// Metin eşleştirmesi yerine <b>sıra</b> seçildi: bu depoda *"dizge
    /// yanlış"* sınıfının dört örneği var ve hepsi kolonu ve sorgusu doğru
    /// olan yerlerde çıktı. İnsan zaten listeye bakıyor; ondan bir sayı
    /// istemek, iki metni karşılaştırmaktan hem ucuz hem kesin.
    /// </para>
    /// </summary>
    public int? CorrectFindingRank { get; set; }

    /// <summary>
    /// Sıra sorusu bu incelemede <b>gerçekten soruldu mu</b> —
    /// <c>accuracy@k</c>'nın paydası bu alandan geliyor.
    ///
    /// <para>
    /// <b>Şema sürümü bu ayrımı taşımaya yetmiyor ve sebebi ölçüldü:</b> iki
    /// yakalama yolu var. Rapor ekranı bulguları <i>gösteriyor</i>, dolayısıyla
    /// soruyu sorabiliyor; alarm kapatma ekranı bulguları göstermiyor,
    /// dolayısıyla <b>soramıyor</b>. İkisi de aynı sürümle yazıyor. Payda
    /// sürüme bağlansaydı, kapatma yoluyla yazılan her inceleme sorulmamış bir
    /// soruyla paydaya girer ve <c>accuracy@1</c>'i sessizce aşağı çekerdi.
    /// </para>
    ///
    /// <para>
    /// T36'nın <c>Measured=false</c> ↔ <c>Unreliable=0</c> ayrımının aynısı:
    /// ölçümün <b>yapıldığı</b> ayrı bir alan, ölçümün <b>sonucu</b> ayrı.
    /// </para>
    /// </summary>
    public bool CorrectFindingRankAsked { get; set; }
}
