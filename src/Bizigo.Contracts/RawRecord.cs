namespace Bizigo.Contracts;

/// <summary>
/// Ham, <b>henüz çözülmemiş</b> tek kayıt. Ingest'in içeri aldığı ve WAL'a
/// yazdığı birim (F1 §2.3, §7.1).
///
/// <para>
/// <b>Neden string değil bayt:</b> ağ cihazlarının çoğu UTF-8 garanti etmez.
/// Kodlama tespiti yanlış çıkarsa tek düzeltme yolu orijinal baytları geri
/// okumaktır — çözülmüş string saklanırsa hata kalıcı olur (K4, F1 §2.4).
/// Bu yüzden <see cref="Body"/> bayt taşır ve arşive base64 olarak gider.
/// </para>
/// </summary>
public sealed record RawRecord
{
    public required Guid EventId { get; init; }

    /// <summary>Ingest'in kaydı aldığı an. Cihazın kendi damgası değil.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>OTLP <c>time_unix_nano</c> — cihazın/collector'ın damgası, varsa.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>
    /// Kaynağı envanterde aramak için kullanılan anahtar: peer IP, hostname ya da
    /// cihaz etiketi. Eşleşmezse <see cref="OwnerGroups.Unassigned"/> (F1 §8).
    /// </summary>
    public required string SourceKey { get; init; }

    /// <summary>
    /// Envanterden çözülen kaynak kimliği. <b>WAL'a yazılırken boştur</b> — ack
    /// çözümlemeyi beklemez (F1 §2.3). Dispatcher (T06) doldurur, arşiv nesnesi
    /// (T04) bu değerle anahtarlanır.
    /// </summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>
    /// Kapsam grubu. <see cref="SourceId"/> ile aynı anda çözülür; ham okuma da
    /// kapsam filtresinden geçtiği için nesne anahtarının parçası (K17, F1 §7.1).
    /// </summary>
    public string OwnerGroup { get; init; } = string.Empty;

    /// <summary>Taşıma: <c>syslog-tcp</c>, <c>syslog-udp</c>, <c>otlp-http</c>.</summary>
    public string TransportProto { get; init; } = string.Empty;

    /// <summary>Karşı uç adresi (<c>10.1.2.3:41022</c>) — teşhis ve kaynak eşlemesi için.</summary>
    public string TransportPeer { get; init; } = string.Empty;

    /// <summary>
    /// Gönderenin <i>iddia ettiği</i> kodlama. Tespit sırasının ilk adayı, kesin
    /// bilgi değil.
    /// </summary>
    public string EncodingDeclared { get; init; } = string.Empty;

    /// <summary>OTLP severity_number, varsa.</summary>
    public byte SeverityNumber { get; init; }

    /// <summary>ORİJİNAL BAYTLAR. Hiçbir aşamada yeniden kodlanmaz.</summary>
    public required ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>OTLP kaynak + kayıt öznitelikleri, düzleştirilmiş.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
