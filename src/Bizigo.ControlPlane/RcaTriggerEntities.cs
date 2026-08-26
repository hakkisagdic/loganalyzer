using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Bir RCA koşumunu doğuran kaynak (RCA §5, K20).
///
/// <para>
/// <b>Dört değer, ve dördüncü satır burada YOK.</b> §5 tablosu beş satır
/// sayıyor ama beşincisi — anomali zinciri — bir kaynak değil bir <b>devam</b>
/// kuralı: girdisi bir sinyal değil, daha önce koşmuş bir RCA. Zincirin ilk
/// halkası her zaman bu dört değerden biri.
/// </para>
///
/// <para>
/// Bunu enum'a beşinci değer olarak eklemek, tam da ölçümle çürütülmüş okumayı
/// koda yazmak olurdu: tabloda diğerleriyle aynı görünen bir satır, arkasında
/// eşiği ve sıklığı kararlaştırılmamış bir dedektör saklıyordu. Devam kuralının
/// izi <see cref="RcaRunEntity.Depth"/>'te — sıfırdan büyük her koşum bir
/// devamdır ve kaynağını <b>kökünden</b> miras alır.
/// </para>
///
/// <para>
/// <b>Küme kapalı.</b> Açık olsaydı §5'in tek-kuyruk garantisi yeni değerler
/// için tanımsız kalırdı: yeni bir tetikleyici eklemek bir çekirdek kararı,
/// plugin kararı değil.
/// </para>
/// </summary>
public enum RcaTriggerSource
{
    /// <summary>
    /// Alarm ya da Sigma — ikisi arasında çalışma zamanında <b>yol farkı yok</b>.
    /// Giriş noktası <c>alert_triggers</c> tablosuna düşen satır; <c>AlertRaised</c>
    /// diye bir olay depoda hiç yok ve tasarım onu varsayıyordu.
    /// </summary>
    Alert = 0,

    /// <summary>Kullanıcı, arayüzden.</summary>
    User = 1,

    /// <summary>Dış API — <c>POST /v1/rca</c> + <c>Idempotency-Key</c>.</summary>
    Api = 2,

    /// <summary>
    /// Takvim / cron. Kotanın <b>en öngörülebilir</b> tüketicisi: diğer üçü
    /// olaya bağlı, bu takvime.
    /// </summary>
    Schedule = 3,
}

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
    /// Dış API'nin idempotency anahtarı; yalnızca <see cref="RcaTriggerSource.Api"/>
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
}
