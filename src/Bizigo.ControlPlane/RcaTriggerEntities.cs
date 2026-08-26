using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

using Bizigo.Contracts;

namespace Bizigo.ControlPlane;

/// <summary>
/// Bir koşumun neden kabul edilmediği — <b>kapalı küme</b> (RCA §5).
///
/// <para>
/// <b>Sebepler ayrı tutuluyor ve bu tartışmaya açık değil.</b> Sınır, döngü ve
/// kota farklı şeyler söylüyor: biri "zincir yeterince derine indi", diğeri
/// "bu zaten koştu", üçüncüsü "bütçe bitti". Tek bir <c>Rejected</c> değerine
/// indirmek, sonraki kişinin <i>hangi kapının kapattığını</i> bilememesi demek —
/// ve bu depoda "veri var, yüzey yok" ile "veri kayda hiç girmiyor" ayrımı iki
/// kez ödendi.
/// </para>
/// </summary>
public enum RcaRejectionReason
{
    /// <summary>Reddedilmedi.</summary>
    None = 0,

    /// <summary>
    /// Aynı tetikleyici anahtarı aynı pencerede zaten koştu. Alarm fırtınasının
    /// karşılığı: 500 alarm → 1 RCA.
    /// </summary>
    Debounced = 1,

    /// <summary>
    /// <c>depth ≥ 2</c>. Zincir sınırı — <b>döngü değil</b>. Bir zincir hiç
    /// döngü içermeden de bu sınıra çarpabilir.
    /// </summary>
    DepthExceeded = 2,

    /// <summary>
    /// Tetikleyici anahtarı <b>kendi soyağacında</b> zaten var: A → B → A.
    /// <see cref="DepthExceeded"/>'dan ayrı olması şart — <c>depth ≤ 2</c> bu
    /// döngüyü <b>kısaltır ama engellemez</b>, A ikinci kez koşar, aynı rapor
    /// iki kez üretilir ve kota iki kez ödenir. İkisi tek sebeple kaydedilseydi
    /// testte de "derinlik yetiyor" yanılsaması kalırdı.
    /// </summary>
    AncestorRepeat = 3,

    /// <summary>
    /// Kota. <b>T46'nın işi</b> — burada yalnızca kümede yeri var ki T46 kapalı
    /// kümeyi yeniden şekillendirmek zorunda kalmasın.
    /// </summary>
    QuotaExceeded = 4,
}

/// <summary>
/// Bir koşumun <b>başına gelen her şey</b> — kapalı küme (T46, F4 kota kararı §9.3).
///
/// <para>
/// <b>Emsal ve gerekçe <c>AlertRunState</c>.</b> Alarm motoru zaman aşımını
/// <c>Quiet</c> yapmıyor, <c>TimedOut</c> yapıyor; çünkü "alarm yok" cevabı asla
/// bir zaman aşımından türememeli. Buradaki karşılığı üç ayrı değer:
/// <see cref="Empty"/> (bakıldı, bulunamadı) ≠ <see cref="Rejected"/> +
/// <c>QuotaExceeded</c> (<b>hiç bakılmadı</b>) ≠ <see cref="Cancelled"/>
/// (başladı, yarıda kesildi). Sonuncusu özellikle önemli: yarıda kesilen koşum
/// kanıt toplamış olabilir ve o kanıt "bulunamadı" diye sunulursa <b>yanlış bir
/// olumsuzluk</b> üretir.
/// </para>
///
/// <para>
/// <b>Durum yalnızca burada.</b> <c>rca_report</c> bir statü taşıyıcısı değil;
/// var olduğunda taşıyacağı şey üretilen belgenin kendisi olacak. İkiye
/// bölünürlerse "kota mı, boş mu" sorusu bir <c>join</c>'e döner ve join'in iki
/// sessiz hâli var: satır ikisinde birden ya da hiçbirinde. İkisi de belirti
/// üretmez. <c>RcaReportStatusGuardTests</c> bu sınırı bekliyor.
/// </para>
/// </summary>
public enum RcaRunState
{
    /// <summary>
    /// Kuyruğa <b>hiç girmedi</b>. Sebebi <see cref="RcaRunEntity.Rejection"/>'da.
    /// Kotadan <b>düşülmez</b> — iş hiç başlamadı, maliyet ödenmedi.
    /// </summary>
    Rejected = 0,

    /// <summary>
    /// Kabul edildi, slot bekliyor.
    ///
    /// <para>
    /// <b>Ret değil bekletme</b> (§9 bulgu 2). "Sıranı bekliyorsun" ile "kotan
    /// doldu" kullanıcı için tamamen farklı; tek bir "şu an çalıştırılamıyor"
    /// mesajı ikisini birleştirir ve kullanıcıyı bekleyeceği yerde kotasını
    /// sorgulamaya gönderir.
    /// </para>
    /// </summary>
    Queued = 1,

    /// <summary>Slot alındı, koşuyor.</summary>
    Running = 2,

    /// <summary>Bitti ve rapor üretti.</summary>
    Complete = 3,

    /// <summary>
    /// Koştu, baktı, <b>bulamadı</b>. Kotadan düşülüyor: bakma maliyeti ödendi.
    /// </summary>
    Empty = 4,

    /// <summary>
    /// Kanıt toplandı, akıl yürütme kesildi (§4.3). Aynı paketle <b>yeniden
    /// koşturulabilir</b> — paket saklı. Kesilmiş bir raporu atmak, elde duran
    /// kanıtı da atmak olurdu.
    /// </summary>
    Truncated = 5,

    /// <summary>
    /// Sistem <b>bildiği bir sınırda kasten durdu</b> — operatör yapılandırmaya
    /// bakarak öngörebilirdi.
    ///
    /// <para>
    /// <see cref="Failed"/>'dan ayıran ölçüt tek soru: <b>öngörülebilir miydi?</b>
    /// Süre tavanı, token bütçesi, iptal isteği → <c>Cancelled</c>. Sağlayıcı
    /// hatası, tekrardan sonra hâlâ bozuk çıktı → <c>Failed</c>.
    /// </para>
    ///
    /// <para>
    /// ⚠️ Kota ekseni bundan <b>bağımsız</b>: token bütçesi dolan koşum
    /// <c>Cancelled</c> <b>ve</b> kotadan düşülüyor. İki eksen olduğu
    /// yazılmazsa "iptal edildi, o hâlde bedava" çıkarımı doğar.
    /// </para>
    /// </summary>
    Cancelled = 6,

    /// <summary>
    /// Öngörülemeyen arıza. Kotadan <b>düşülüyor</b> — kanıt topladı, belki
    /// modeli çağırdı, maliyeti gerçekten ödendi (§9 bulgu 1).
    /// </summary>
    Failed = 7,
}

/// <summary>
/// Bir RCA koşum talebi — <b>kabul edilmiş ya da reddedilmiş</b> (T45, RCA §5).
///
/// <para>
/// <b>Reddedilenler de bu tabloda.</b> Sessizce düşürmek, "neden RCA
/// üretilmedi" sorusunu cevapsız bırakır; ayrı bir tabloya koymak ise aynı
/// soruyu iki yerden sordurur.
/// </para>
///
/// <para>
/// <b>Koşum durumu (kuyruk, çalışıyor, bitti) burada YOK</b> — o T46'nın kapalı
/// kümesi. T45'in cevapladığı tek soru "bu talep kuyruğa girer mi": kabul
/// kararı, soyağacı ve ret sebebi. Durum makinesini şimdi yazmak, T46'nın onu
/// yeniden şekillendirmesi demek olurdu.
/// </para>
/// </summary>
[Table("rca_runs")]
public sealed class RcaRunEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Zinciri başlatan koşum. Kök koşumda <b>kendisi</b>.
    ///
    /// <para>
    /// Nullable değil: kök koşum için <c>null</c> bırakmak, "zincirin kökü kim"
    /// sorusunu her okumada bir <c>?? Id</c> ile cevaplatırdı ve o ifade bir yerde
    /// unutulurdu.
    /// </para>
    /// </summary>
    public Guid RootRunId { get; set; }

    /// <summary>Doğrudan ata; kök koşumda <see langword="null"/>.</summary>
    public Guid? ParentRunId { get; set; }

    /// <summary>
    /// Kökten uzaklık. Kök <c>0</c>.
    ///
    /// <para>
    /// <b>Sıfırdan büyük her koşum bir devamdır</b> — devam kuralının tek izi bu.
    /// <see cref="RcaTriggerSource"/>'ta beşinci bir değer olmamasının sebebi de
    /// bu: kaynak kökten miras alınıyor, "kim nihayetinde sebep oldu" sorusu
    /// derinlikten bağımsız cevaplanabiliyor.
    /// </para>
    /// </summary>
    public int Depth { get; set; }

    public RcaTriggerSource Source { get; set; }

    /// <summary>
    /// Kaynağa özgü kimlik: alarmda <c>rule_id</c>, takvimde zamanlama kimliği,
    /// kullanıcıda ve API'de talebi açan özne.
    /// </summary>
    [MaxLength(200)]
    public string TriggerIdentity { get; set; } = string.Empty;

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    /// <summary>İncelenecek zaman aralığı — koşumun girdisi.</summary>
    public DateTimeOffset WindowFrom { get; set; }

    public DateTimeOffset WindowTo { get; set; }

    /// <summary>
    /// Debounce anahtarı — <b>pencere kovası dahil</b>.
    ///
    /// <para>
    /// Sorduğu soru: "bu tetikleyici son on dakikada zaten koştu mu". Pencere
    /// kovası anahtarın parçası, çünkü debounce zaten <b>zaman</b> hakkında.
    /// </para>
    /// </summary>
    [MaxLength(512)]
    public string DebounceKey { get; set; } = string.Empty;

    /// <summary>
    /// Soyağacı anahtarı — <b>pencere kovası HARİÇ</b>.
    ///
    /// <para>
    /// Sorduğu soru başka: "bu tetikleyici kendi atalarımın arasında var mı".
    /// Pencere dahil edilseydi A → B → A zinciri kova sınırını aştığı anda
    /// kontrolden kaçardı — ve zincirler tam da zaman aldıkları için kova
    /// sınırını aşarlar. İki anahtarın ayrı olmasının sebebi bu; aynı üçlüye
    /// iki farklı soru sormak, bu depoda defalarca bedeli ödenmiş bir hata sınıfı.
    /// </para>
    /// </summary>
    [MaxLength(512)]
    public string LineageKey { get; set; } = string.Empty;

    /// <summary>
    /// Dış API'nin idempotency anahtarı; yalnızca <see cref="RcaTriggerSource.External"/>
    /// için dolu. Aynı anahtar → aynı koşum, yeni koşu değil.
    /// </summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>Kabul edildi mi. <c>false</c> ise <see cref="Rejection"/> dolu.</summary>
    public bool Accepted { get; set; }

    public RcaRejectionReason Rejection { get; set; } = RcaRejectionReason.None;

    /// <summary>
    /// Reddin insan okuyacak hâli. Sebep kodunun yanında ayrıca duruyor: kod
    /// <i>hangi kapı</i> sorusunu, metin <i>hangi değerle</i> sorusunu
    /// cevaplıyor ("depth 2 ≥ 2" ile "anahtar A soyağacında" farklı satırlar).
    /// </summary>
    [MaxLength(512)]
    public string RejectionDetail { get; set; } = string.Empty;

    /// <summary>Talebin geldiği an — kabul edilse de edilmese de.</summary>
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Talebi açan özne (OIDC <c>sub</c>) — denetim için.</summary>
    [MaxLength(256)]
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>
    /// Koşumun ürettiği kanıt paketi; henüz üretilmediyse <see langword="null"/>.
    ///
    /// <para>
    /// <b>Idempotency bu alana dayanıyor:</b> aynı anahtar ikinci kez geldiğinde
    /// "aynı raporu döndür" ancak koşumun hangi paketi ürettiği biliniyorsa
    /// mümkün. Yabancı anahtar kısıtı YOK — paket saklama politikası gereği
    /// silinebiliyor (T36) ve silinmiş bir paket koşum kaydını da silmemeli:
    /// "bu koşum oldu ama paketi artık yok" cevaplanabilir kalmalı.
    /// </para>
    /// </summary>
    public Guid? EvidenceBundleId { get; set; }

    /// <summary>
    /// Koşumun <b>başına gelen her şey</b> — kapalı küme (T46).
    ///
    /// <para>
    /// <see cref="Accepted"/> ile çelişmiyor, onu <i>tamamlıyor</i>: kabul
    /// kararı kapının cevabı, bu ise koşumun hikâyesi. Reddedilen bir talep
    /// <see cref="RcaRunState.Rejected"/> durumunda kalıyor ve <b>neden</b>
    /// reddedildiği <see cref="Rejection"/>'da.
    /// </para>
    /// </summary>
    public RcaRunState State { get; set; } = RcaRunState.Rejected;

    /// <summary>
    /// Bu koşum grubun günlük kotasından <b>düşülüyor mu</b>.
    ///
    /// <para>
    /// <b>§9'un birinci bulgusu:</b> "reddedilen koşum düşülmez" yalnızca
    /// <i>girişte</i> reddedilen için geçerli. Süre ya da token tavanına takılan
    /// koşum reddedilmedi, <b>başarısız oldu</b> — kanıt topladı, belki modeli
    /// çağırdı, maliyeti gerçekten ödendi. İkisini aynı kefeye koymak kotayı
    /// gerçek harcamadan koparır.
    /// </para>
    ///
    /// <para>
    /// Kabul anında <c>true</c> yazılıyor ve <b>geri alınmıyor</b>. İade
    /// edilebilir olsaydı "iptal et, yeniden dene" bir kota atlatma yolu olurdu.
    /// </para>
    /// </summary>
    public bool CountsAgainstQuota { get; set; }

    // Kotanın hangi PENCEREYE yazıldığı bilerek saklanmıyor: pencere
    // `RequestedAt` ile yapılandırılmış politikanın fonksiyonu. Satıra
    // yazsaydık politika değiştiğinde eski satırlar eski pencereyi taşımaya
    // devam eder ve sayaç iki farklı tanımı aynı anda kullanırdı — bu depoda
    // ikinci gerçek kaynağın bedeli birkaç kez ödendi.

    /// <summary>Slot alınıp koşumun başladığı an; kuyrukta bekleyen için boş.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Koşumun bittiği an — hangi durumla bittiğinden bağımsız.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Durumu açıklayan tek satır: hangi sınır kesti, hangi sağlayıcı düştü.
    ///
    /// <para>
    /// <see cref="RejectionDetail"/>'dan ayrı, çünkü ikisi farklı anlara ait:
    /// biri kapının, diğeri yürütmenin. Tek alana koymak, "hiç başlamadı" ile
    /// "yarıda kesildi"nin gerekçesini aynı yere yazmak olurdu.
    /// </para>
    /// </summary>
    [MaxLength(512)]
    public string StateDetail { get; set; } = string.Empty;
}
