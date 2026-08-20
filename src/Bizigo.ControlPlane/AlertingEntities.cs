using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>Üç kural tipi, tek değerlendirici (K32, T21).</summary>
public enum AlertRuleType
{
    /// <summary>Sayı bir sınırı aştı mı. Verinin <b>varlığı</b> üzerinde.</summary>
    Threshold = 0,

    /// <summary>Değişim hızlandı mı — pencere önceki pencereye göre. Yine varlık üzerinde.</summary>
    Ratio = 1,

    /// <summary>
    /// Beklenen veri <b>gelmedi mi</b>. Verinin <b>yokluğu</b> üzerinde çalışan tek tip
    /// ve bu yüzden en zoru: olay tablosu var olmayan bir şeyi listeleyemez, dolayısıyla
    /// cevap envanterle olay etkinliğinin farkından çıkıyor.
    /// </summary>
    Silence = 2,
}

/// <summary>Eşik karşılaştırması. Serbest ifade yok — kapalı küme.</summary>
public enum AlertComparison
{
    GreaterThan = 0,
    GreaterThanOrEqual = 1,
    LessThan = 2,
    LessThanOrEqual = 3,
}

/// <summary>
/// Alarm kuralı (T21, K32).
///
/// <para>
/// <b>Kapsam kuralın üstünde saklanıyor, çalışma anında çözülmüyor.</b> Kural
/// zamanlayıcıdan tetikleniyor, yani değerlendirme anında ortada bir HTTP isteği
/// ve dolayısıyla bir kimlik yok. Kapsamı o anda "sahibinin bugünkü grupları"
/// diye çözmek, kural sahibinin ekibi değiştiğinde kuralın sessizce başka veriyi
/// saymaya başlaması demek olurdu. Bu yüzden kural yazıldığı andaki kapsamı
/// <b>taşıyor</b>; sahibinin kapsamı daraldığında kural da daraltılmalı — bunu
/// yapan yer kural kaydetme ucu.
/// </para>
///
/// <para>
/// <b>Sınırsız kapsamlı kural yok.</b> <c>admin</c> rolü sorguda kapsamsız
/// gezebiliyor ama kural yazarken grupları açıkça saymak zorunda. Gerekçe:
/// sınırsız bir kural, "bir ekibin kuralı başka ekibin olaylarını saymıyor"
/// değişmezini tek satırda delerdi ve bunu yapan kişi çoğu zaman fark etmezdi.
/// </para>
/// </summary>
[Table("alert_rules")]
public sealed class AlertRuleEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public required string Name { get; set; }

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public AlertRuleType RuleType { get; set; } = AlertRuleType.Threshold;

    /// <summary>Kuralı yazan kimlik (OIDC <c>sub</c>). Denetim ve "kim yazdı" için.</summary>
    [MaxLength(256)]
    public required string OwnerSubject { get; set; }

    /// <summary>
    /// Kuralın koşacağı <c>owner_group</c> kümesi, virgülle ayrılmış.
    /// Değerlendirici bundan <c>AccessScope.ForGroups</c> üretiyor; başka yol yok.
    /// </summary>
    [MaxLength(1024)]
    public required string OwnerGroups { get; set; }

    /// <summary>
    /// Kaydedilmiş arama, JSON. Eşik ve oran tiplerinde olay filtresi; sessizlikte
    /// yalnızca <c>source_ids</c> anlamlı.
    /// </summary>
    public string SearchJson { get; set; } = "{}";

    /// <summary>Değerlendirme penceresi (saniye). Eşikte "son N saniye".</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>Zamanlayıcının kuralı ne sıklıkla koşturacağı (saniye).</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Eşik tipinde sınır; oran tipinde çarpan (ör. 3 = 3×).</summary>
    public double Threshold { get; set; }

    public AlertComparison Comparison { get; set; } = AlertComparison.GreaterThan;

    /// <summary>
    /// Sessizlik tipinde: bir kaynağın kaç saniye susarsa alarm sayılacağı.
    /// Diğer tiplerde kullanılmıyor.
    /// </summary>
    public int SilenceSeconds { get; set; } = 900;

    /// <summary>
    /// Aynı kural için iki tetiklenme arasındaki asgari süre. Gürültü kontrolünün
    /// ilk kademesi — kanal tarafındaki gruplamadan önce gelir (T22).
    /// </summary>
    public int RepeatIntervalSeconds { get; set; } = 3600;

    public bool Enabled { get; set; } = true;

    /// <summary>Zamanlayıcının bir sonraki tur hesabı. <c>null</c> ise ilk turda koşar.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>
    /// Son turun sonucu. <b>Zaman aşımı burada ayrı bir durum</b> — "tetiklenmedi"
    /// ile aynı kefeye konsaydı yavaş bir sorgu sessizce "her şey yolunda"ya
    /// dönüşürdü (F1'in en pahalı ders sınıfı).
    /// </summary>
    public AlertRunState LastRunState { get; set; } = AlertRunState.NeverRun;

    [MaxLength(1024)]
    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AlertRunState
{
    NeverRun = 0,

    /// <summary>Koştu, eşik aşılmadı.</summary>
    Quiet = 1,

    /// <summary>Koştu ve tetiklendi.</summary>
    Fired = 2,

    /// <summary>Bakım penceresi ya da tekrar aralığı yüzünden bastırıldı.</summary>
    Suppressed = 3,

    /// <summary>Sorgu zaman aşımına uğradı — sonuç <b>bilinmiyor</b>, "sessiz" değil.</summary>
    TimedOut = 4,

    /// <summary>Hata. Sonuç yine bilinmiyor.</summary>
    Failed = 5,
}

/// <summary>
/// Tetiklenme geçmişi (T21 kapsam: "tetiklenme geçmişi").
///
/// <para>
/// Sessizlik tipinde <b>kaynak başına</b> bir kayıt üretiliyor: on cihazın
/// dokuzu susmuşsa mesele "bir kural tetiklendi" değil, hangi dokuz cihaz
/// olduğu.
/// </para>
/// </summary>
[Table("alert_triggers")]
public sealed class AlertTriggerEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RuleId { get; set; }

    public DateTimeOffset FiredAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Değerlendirilen pencerenin başı — bildirimdeki bağlantı bunu taşıyor.</summary>
    public DateTimeOffset WindowFrom { get; set; }

    public DateTimeOffset WindowTo { get; set; }

    /// <summary>Tetikleyen değer: eşikte sayı, oranda katsayı, sessizlikte susma saniyesi.</summary>
    public double Value { get; set; }

    /// <summary>Karşılaştırıldığı sınır — mesajda "100 &gt; 80" yazabilmek için.</summary>
    public double Threshold { get; set; }

    /// <summary>Etkilenen kaynak; sessizlik tipinde dolu, diğerlerinde boş olabilir.</summary>
    [MaxLength(128)]
    public string SourceId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string OwnerGroup { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string Summary { get; set; } = string.Empty;

    // --- Asgari yaşam döngüsü (T38) ----------------------------------------
    //
    // T38 "alarm kapatan kullanıcı inceleme adımını atlayamıyor" diyordu ve
    // depoda kapatma diye bir şey YOKTU: bu varlık ateşle-unut bir geçmiş
    // kaydıydı. Kapatma olmadan atlayamama da olmuyor.
    //
    // BİLEREK DAR: durum, kapatan, kapatma zamanı, inceleme bağı. Atama,
    // eskalasyon, susturma yok — susturma zaten ayrı bir kavram ve ayrı bir
    // tablosu var (MaintenanceWindowEntity).

    public AlertTriggerState State { get; set; } = AlertTriggerState.Open;

    /// <summary>Kapatan kullanıcının OIDC <c>sub</c>'ı. Açıkken boş.</summary>
    [MaxLength(256)]
    public string ClosedBySubject { get; set; } = string.Empty;

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Kapatırken verilen inceleme. <b>Kapalı bir tetiklenmede dolu olmak
    /// zorunda</b> — zorunluluğu taşıyan bağ bu, ve boş bırakılabilseydi
    /// "atlayamıyor" iddiası yalnızca ekranda kalırdı.
    /// </summary>
    public Guid? ReviewId { get; set; }
}

/// <summary>
/// Tetiklenmenin yaşam döngüsü (T38).
///
/// <para>
/// İki durum yeterli: bir alarm ya bakılmayı bekliyor ya kapatılmış.
/// Ara durumlar ("inceleniyor", "atandı") ancak sahiplenme varsa anlamlı ve
/// sahiplenme bu ticket'ın kapsamı dışında.
/// </para>
/// </summary>
public enum AlertTriggerState
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// Bakım penceresi — "susturma" (T21 kapsam).
///
/// <para>
/// Kural tipindeki <see cref="AlertRuleType.Silence"/> ile karıştırılmamalı:
/// orası "veri susmuş", burası "alarmı sustur". Tip adı <c>Silence</c>, burası
/// <c>MaintenanceWindow</c> — iki kavram iki isim.
/// </para>
///
/// <para>
/// <c>RuleId</c> boşsa pencere o gruptaki <b>tüm</b> kuralları kapsıyor: bakım
/// çoğunlukla bir cihaza değil bir ekibin altyapısına yapılıyor.
/// </para>
/// </summary>
[Table("alert_maintenance_windows")]
public sealed class MaintenanceWindowEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary><c>null</c> ise gruptaki tüm kurallar.</summary>
    public Guid? RuleId { get; set; }

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    // `From`/`To` değil: Postgres'te `from` ayrılmış sözcük ve elle yazılan her
    // sorguda tırnak gerektirirdi. Kolon adları psql oturumunda okunabilir kalmalı.
    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(256)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum NotificationChannelType
{
    Slack = 0,
    Teams = 1,
    Email = 2,
    Webhook = 3,
}

/// <summary>
/// Bildirim kanalı yapılandırması (T22).
///
/// <para>
/// <b>Gizli bilgi ile geri kalanı ayrı kolonlarda.</b> Webhook URL'i ve SMTP
/// parolası <see cref="SecretCipher"/>'da şifreli duruyor; <see cref="ConfigJson"/>
/// yalnızca gizli olmayanı taşıyor (kime gönderilecek, hangi sunucu, hangi port).
/// Ayrımın sebebi listeleme ucu: kanal listesi <c>ConfigJson</c>'ı gösterebiliyor,
/// çünkü içinde gösterilemeyecek bir şey <b>olamıyor</b>. Tek alanda dursalardı
/// her uçta ayrı ayrı maskeleme gerekirdi ve biri unutulurdu.
/// </para>
/// </summary>
[Table("notification_channels")]
public sealed class NotificationChannelEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public required string Name { get; set; }

    public NotificationChannelType ChannelType { get; set; }

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    /// <summary>Gizli <b>olmayan</b> yapılandırma, JSON. API yanıtında olduğu gibi dönüyor.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// Şifrelenmiş gizli bilgi (AES-GCM, base64). <b>Hiçbir</b> API yanıtına,
    /// loga veya hata mesajına girmiyor; testle sabitlenmiş.
    /// </summary>
    public string SecretCipher { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Hangi kural hangi kanala gidiyor.</summary>
[Table("alert_rule_channels")]
public sealed class AlertRuleChannelEntity
{
    public Guid RuleId { get; set; }

    public Guid ChannelId { get; set; }
}

/// <summary>
/// Bir tetiklenmenin bir kanala teslimi (T22).
///
/// <para>
/// <b>Kuyruk kalıcı, bellekte değil.</b> "Kanal geçici olarak ulaşılamazsa alarm
/// kaybolmuyor" kabul kriteri ancak böyle karşılanıyor: süreç yeniden başlasa da
/// bekleyen teslimler duruyor.
/// </para>
/// </summary>
[Table("notification_deliveries")]
public sealed class NotificationDeliveryEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TriggerId { get; set; }

    public Guid RuleId { get; set; }

    public Guid ChannelId { get; set; }

    public DeliveryState State { get; set; } = DeliveryState.Pending;

    public int Attempts { get; set; }

    /// <summary>Geri adım sonrası bir sonraki deneme anı.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>
    /// Son hata — <b>redaksiyondan geçmiş</b>. Gizli bilgi buraya yazılamaz;
    /// yazılabilseydi "gizli bilgi hiçbir yerde görünmüyor" iddiası veritabanı
    /// tarafından çürütülürdü.
    /// </summary>
    [MaxLength(1024)]
    public string LastError { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum DeliveryState
{
    Pending = 0,
    Delivered = 1,

    /// <summary>Deneme hakkı bitti. Kayıt duruyor — sessizce silinmiyor.</summary>
    Failed = 2,
}
