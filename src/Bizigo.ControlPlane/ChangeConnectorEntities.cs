using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Değişiklik kaynağının tipi (T25, K34'ün üç kaynağı).
/// </summary>
public enum ChangeConnectorType : byte
{
    /// <summary>Dışarıdan itilen imzalı webhook (T24). Zamanlanmıyor — push.</summary>
    Webhook = 1,

    /// <summary>
    /// Cihazdan periyodik config çekip fark alan toplayıcı. Toplayıcının kendisi
    /// <b>T26</b>; burada yalnızca yapılandırması ve zamanlaması duruyor.
    /// </summary>
    DeviceConfig = 2,

    /// <summary>
    /// Yalnızca elle giriş. Kimlik bilgisi yok, zamanlaması yok — ekrandaki
    /// formun hangi grup adına yazdığını adlandırmak için var.
    /// </summary>
    Manual = 3,
}

public enum ConnectorRunState : byte
{
    Succeeded = 1,
    Failed = 2,

    /// <summary>Vadesi geldi ama koşacak bir şey yoktu (ör. pasif edilmiş).</summary>
    Skipped = 3,
}

/// <summary>
/// Ekrandan tanımlanan değişiklik kaynağı (T25, K34: "ekrandan
/// yapılandırılabilmeli").
///
/// <para>
/// <b>Kimlik bilgisi burada ŞİFRELİ duruyor</b> (<see cref="CredentialCipher"/>)
/// ve ürünün başka hiçbir yerinde düz metin olarak bulunmuyor: ne API yanıtında,
/// ne logda, ne hata mesajında. Şifreleme
/// <c>Bizigo.Contracts.Security.SecretProtector</c> ile — T22'nin kanal gizli
/// bilgileri için kurduğu şemanın aynısı, çünkü ikinci bir şema ikinci bir
/// anahtar rotasyonu demekti.
/// </para>
///
/// <para>
/// <b>T26 buraya oturuyor.</b> Cihaz config toplayıcısının ihtiyaç duyduğu her
/// şey — hedef, zamanlama, kimlik bilgisi, etkin/pasif, sahip grubu — bu
/// tabloda. O ticket yalnızca bir <c>IChangeConnectorRunner</c> yazacak.
/// </para>
/// </summary>
[Table("change_connectors")]
public sealed class ChangeConnectorEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>
    /// URL'de görünen kısa kimlik: webhook connector'ı için
    /// <c>POST /v1/changes/webhooks/{slug}</c>. Grup içinde tekil.
    ///
    /// <para>
    /// Ayrı bir alan çünkü <see cref="Id"/> bir GUID ve onu bir CI
    /// yapılandırmasına yapıştırmak kimsenin işine yaramıyor; ad ise
    /// değiştirilebilir olmalı ve değişince webhook'un adresi kırılmamalı.
    /// </para>
    /// </summary>
    [MaxLength(64)]
    public required string Slug { get; set; }

    [MaxLength(200)]
    public required string Name { get; set; }

    public ChangeConnectorType ConnectorType { get; set; }

    /// <summary>
    /// Bu connector'ın yazabileceği <b>tek</b> grup. Kapsam kapısı (K17) burada
    /// da gerçek bir kapı: connector kendi grubunun dışına olay düşüremiyor.
    /// </summary>
    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    /// <summary>
    /// Gizli <b>olmayan</b> yapılandırma, JSON. API yanıtında olduğu gibi
    /// dönüyor — bu yüzden buraya gizli bir şey yazılmamalı; gizli olan
    /// <see cref="CredentialCipher"/>'a gider.
    /// </summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>
    /// Şifrelenmiş kimlik bilgisi (AES-256-GCM, base64). Webhook için paylaşılan
    /// imza anahtarı, cihaz için parola/jeton. Yanıtta yalnızca "var mı yok mu"
    /// bilgisi dönüyor.
    /// </summary>
    public string CredentialCipher { get; set; } = string.Empty;

    /// <summary>
    /// Zamanlama aralığı. <see langword="null"/> ise connector
    /// <b>zamanlanmıyor</b> — webhook (push) ve elle giriş için doğru olan bu.
    /// </summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>Zamanlayıcının tek sorgusu bunun üzerinden: "vadesi gelmiş etkin connector'lar".</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public ConnectorRunState? LastRunState { get; set; }

    /// <summary>
    /// Son hata — <b>redaksiyondan geçmiş</b>. Ham istisna mesajı buraya
    /// yazılmıyor: bağlantı hatası, kimlik bilgisini mesajın içinde taşıyan en
    /// sık yol.
    /// </summary>
    [MaxLength(1024)]
    public string LastError { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Connector başına çalışma geçmişi (T25 kapsamı: "çalışma geçmişi ve son hata").
///
/// <para>
/// Ayrı tablo çünkü <see cref="ChangeConnectorEntity.LastError"/> yalnızca son
/// durumu söylüyor ve "bu connector dün gece saat üçten beri başarısız" sorusu
/// ancak geçmişle cevaplanıyor — arada bir başarılı koşu varsa sorun aralıklı,
/// yoksa kalıcı, ve iki durumun müdahalesi farklı.
/// </para>
/// </summary>
[Table("change_connector_runs")]
public sealed class ChangeConnectorRunEntity
{
    [Key]
    public long Id { get; set; }

    public Guid ConnectorId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public ConnectorRunState State { get; set; }

    /// <summary>Bu koşuda kaç değişiklik olayı yazıldı.</summary>
    public int ChangesWritten { get; set; }

    /// <summary>Redaksiyondan geçmiş hata metni.</summary>
    [MaxLength(1024)]
    public string Error { get; set; } = string.Empty;
}
