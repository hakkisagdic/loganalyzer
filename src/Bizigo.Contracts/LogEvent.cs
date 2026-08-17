using System.Net;

namespace Bizigo.Contracts;

public enum ParseStatus : byte
{
    Ok = 1,
    Partial = 2,
    Failed = 3,
}

public static class OwnerGroups
{
    /// <summary>
    /// Envanterde eşleşmeyen kaynaklar buraya düşer. Olay <b>reddedilmez</b> —
    /// veri kaybı, eksik envanterden kötüdür. Yalnızca yöneticiye görünür ve
    /// bir sağlık uyarısı üretir (F1 §8).
    /// </summary>
    public const string Unassigned = "_unassigned";
}

/// <summary>
/// <see cref="LogEvent.TimeSource"/> değerleri — olayın zamanının nereden
/// geldiği.
///
/// <para>
/// Sıra aynı zamanda <b>güven sırası</b>: <see cref="Parsed"/> cihazın kendi
/// yazdığı zaman, <see cref="Observed"/> collector'ın satırı gördüğü an,
/// <see cref="Received"/> ise bizim aldığımız an. Aradaki fark ağ gecikmesi ve
/// tampon süresi kadar — yani dakikalara çıkabilir.
/// </para>
/// </summary>
public static class TimeSources
{
    /// <summary>Parser satırdan çözdü. Tek gerçekten güvenilir kaynak.</summary>
    public const string Parsed = "parsed";

    /// <summary>Collector'ın gözlem zamanı; satırda tarih yoktu.</summary>
    public const string Observed = "observed";

    /// <summary>Son çare: bizim aldığımız an.</summary>
    public const string Received = "received";

    /// <summary>
    /// Kolon eklenmeden önce yazılmış satırlar. Geçmişi 'parsed' saymak onu
    /// olduğundan güvenilir, 'received' saymak olmadığı kadar şüpheli
    /// gösterirdi.
    /// </summary>
    public const string Unknown = "";
}

/// <summary>
/// Normalize olay. Kolon karşılıkları <c>db/clickhouse/0001_events.sql</c>.
/// OCSF ve OTel alanları burada <b>saklanmaz</b>, <c>core</c> + <c>Attrs</c>
/// üzerinden türetilir (K8, F1 §5).
/// </summary>
public sealed record LogEvent
{
    public required Guid EventId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// <see cref="Timestamp"/> hangi kaynaktan geldi — <see cref="TimeSources"/>.
    ///
    /// <para>
    /// Bunu bilmeden "olay saat 14:03'te oldu" cümlesi kurulamıyor: değer
    /// cihazın yazdığı zaman da olabilir, bizim aldığımız an da. RCA'nın
    /// korelasyon penceresi buna bağlı.
    /// </para>
    /// </summary>
    public string TimeSource { get; init; } = TimeSources.Unknown;

    public DateTimeOffset IngestedAt { get; init; } = DateTimeOffset.UtcNow;

    public required string OwnerGroup { get; init; }
    public required string SourceId { get; init; }
    public string Host { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public string Product { get; init; } = string.Empty;

    public string ParserId { get; init; } = string.Empty;
    public string ParserVersion { get; init; } = string.Empty;
    public ParseStatus ParseStatus { get; init; } = ParseStatus.Failed;
    public uint ParseGeneration { get; init; } = 1;
    public string EncodingDetected { get; init; } = string.Empty;
    public string TemplateId { get; init; } = string.Empty;

    public byte SeverityNum { get; init; }
    public uint OcsfClassUid { get; init; }
    public ushort OcsfActivityId { get; init; }

    // core — sıcak kolonlar
    public IPAddress SrcIp { get; init; } = IPAddress.IPv6Any;
    public IPAddress DstIp { get; init; } = IPAddress.IPv6Any;
    public ushort SrcPort { get; init; }
    public ushort DstPort { get; init; }
    public string Proto { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Attrs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>UTF-8 NFC normalize edilmiş mesaj gövdesi.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Ham arşive geri bağ: <c>&lt;object_key&gt;#&lt;offset&gt;:&lt;length&gt;</c>.</summary>
    public string RawRef { get; init; } = string.Empty;
}

public enum ChangeTargetKind : byte
{
    Device = 1,
    Service = 2,
    Config = 3,
    Inventory = 4,
    Maintenance = 5,
}

/// <summary>
/// "Ne değişti" olayı. RCA'nın en güçlü sinyali (K21). Tablo F1'de açılıyor ki
/// F3'te geçmiş hazır olsun.
/// </summary>
public sealed record ChangeEvent
{
    public required Guid ChangeId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string OwnerGroup { get; init; }

    public required ChangeTargetKind TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required string ChangeKind { get; init; }

    public string Actor { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string Source { get; init; } = "api";
    public string ExternalRef { get; init; } = string.Empty;
}
