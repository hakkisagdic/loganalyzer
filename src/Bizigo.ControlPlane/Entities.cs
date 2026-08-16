using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// Kaynak/cihaz envanteri. <c>owner_group</c> buradan geliyor — olaydan değil
/// (K17, F1 §8). Envanter eksikse olay reddedilmez, <c>_unassigned</c>'a düşer.
/// </summary>
[Table("sources")]
public sealed class SourceEntity
{
    [Key]
    [MaxLength(128)]
    public required string SourceId { get; set; }

    /// <summary>Syslog peer IP / hostname / cihaz etiketi. Dispatcher bununla eşliyor.</summary>
    [MaxLength(256)]
    public string? PeerAddress { get; set; }

    [MaxLength(256)]
    public string? Hostname { get; set; }

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    [MaxLength(64)]
    public string Vendor { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Product { get; set; } = string.Empty;

    /// <summary>
    /// Dispatcher kademe 1: doğrudan bağ. Üretim trafiğinin &gt;%95'i buradan
    /// geçmeli (T06 <c>bound_ratio</c> metriği).
    /// </summary>
    [MaxLength(128)]
    public string? ParserId { get; set; }

    /// <summary>
    /// Kodlama ipucu: <c>auto</c>, <c>utf-8</c>, <c>windows-1254</c>, <c>iso-8859-9</c>…
    /// Ağ cihazları UTF-8 garanti etmiyor (K4).
    /// </summary>
    [MaxLength(32)]
    public string Encoding { get; set; } = "auto";

    [MaxLength(64)]
    public string SourceClass { get; set; } = "default";

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// IdP grubu → <c>owner_group</c> eşlemesi (K26, F1 §10.1.1).
///
/// Claim'i doğrudan <c>owner_group</c> saymıyoruz: bir ekibin kapsamını
/// değiştirmek için IdP'ye dokunmak gerekirdi. Keycloak Group Membership mapper
/// tam yolu <b>başında eğik çizgiyle</b> basıyor (<c>/network/core</c>) —
/// burada tam yol saklanır, giriş normalize edilir.
/// </summary>
[Table("idp_group_mapping")]
public sealed class IdpGroupMappingEntity
{
    [Key]
    [MaxLength(256)]
    public required string IdpGroup { get; set; }

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    [MaxLength(256)]
    public string Note { get; set; } = string.Empty;
}

/// <summary>Parser kataloğu. YAML gövdesi burada versiyonlanıyor.</summary>
[Table("parsers")]
public sealed class ParserEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(128)]
    public required string ParserId { get; set; }

    [MaxLength(32)]
    public required string Version { get; set; }

    [MaxLength(64)]
    public string Vendor { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Product { get; set; } = string.Empty;

    public required string Yaml { get; set; }

    /// <summary>Testsiz parser yayınlanamaz (T05). Yayın öncesi son geçen test sayısı.</summary>
    public int PassingTests { get; set; }

    public ParserState State { get; set; } = ParserState.Draft;

    [MaxLength(128)]
    public string Owner { get; set; } = string.Empty;

    /// <summary>Sürekli zaman aşımı veren parser karantinaya alınır (T05 ReDoS savunması).</summary>
    public bool Quarantined { get; set; }

    [MaxLength(512)]
    public string QuarantineReason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? PublishedAt { get; set; }
}

public enum ParserState
{
    Draft = 0,
    InReview = 1,
    Published = 2,
    Retired = 3,
}

/// <summary>
/// Ham arşiv manifesti (K25, F1 §7.0 koruma #4).
///
/// <b>Bu tablonun varlık sebebi:</b> manifest olmadan "replay 7 gün yerine 5 gün
/// döndü" durumu fark edilmez. Manifest'le bu bir hata mesajı olur.
/// </summary>
[Table("raw_manifest")]
public sealed class RawManifestEntity
{
    [Key]
    [MaxLength(512)]
    public required string ObjectKey { get; set; }

    [MaxLength(64)]
    public required string OwnerGroup { get; set; }

    [MaxLength(64)]
    public required string Sha256 { get; set; }

    public long ByteSize { get; set; }

    public int EventCount { get; set; }

    public DateTimeOffset TsFrom { get; set; }

    public DateTimeOffset TsTo { get; set; }

    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Geri okunup sha256'sı doğrulandığı an. Null ise WAL segmenti silinemez.</summary>
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>Periyodik scrub'ın son kontrolü.</summary>
    public DateTimeOffset? LastScrubbedAt { get; set; }

    public RawObjectState State { get; set; } = RawObjectState.Uploaded;
}

public enum RawObjectState
{
    Uploaded = 0,
    Verified = 1,
    ChecksumMismatch = 2,
    Missing = 3,
}

/// <summary>
/// Her sorgu buraya düşer: kim, hangi kapsam, hangi filtre, kaç satır (F1 §10.2).
/// </summary>
[Table("audit_log")]
public sealed class AuditLogEntity
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(256)]
    public required string Subject { get; set; }

    [MaxLength(64)]
    public required string Action { get; set; }

    [MaxLength(64)]
    public string Resource { get; set; } = string.Empty;

    /// <summary>Uygulanan kapsam — sonradan "bu kişi neyi görebiliyordu" sorusuna cevap.</summary>
    [MaxLength(1024)]
    public string Scope { get; set; } = string.Empty;

    [MaxLength(4096)]
    public string Details { get; set; } = string.Empty;

    public long RowCount { get; set; }

    public int DurationMs { get; set; }

    public bool Succeeded { get; set; } = true;
}
