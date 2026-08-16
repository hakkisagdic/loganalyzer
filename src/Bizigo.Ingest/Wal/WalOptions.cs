namespace Bizigo.Ingest.Wal;

public sealed class WalOptions
{
    public const string SectionName = "Ingest:Wal";

    /// <summary>Segment dosyalarının bulunduğu dizin. Kalıcı bir birim olmalı.</summary>
    public string Directory { get; set; } = "/var/lib/bizigo/wal";

    /// <summary>Bu boyutu aşan segment kapatılır, yenisi açılır.</summary>
    public long MaxSegmentBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// Diskte tutulan toplam WAL üst sınırı. Aşılırsa yazma reddedilir ve uç
    /// <c>503 + Retry-After</c> döner — backpressure zincirinin başladığı yer
    /// (F1 §2.3). Kendi kuyruğumuzu yazmıyoruz; collector yeniden dener.
    /// </summary>
    public long MaxTotalBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    /// <summary>503 ile birlikte dönen <c>Retry-After</c> saniyesi.</summary>
    public int RetryAfterSeconds { get; set; } = 5;

    /// <summary>
    /// <see langword="false"/> yapılırsa ack artık dayanıklı değildir. Yalnızca
    /// kıyaslama içindir; üretimde <b>asla</b> kapatılmaz.
    /// </summary>
    public bool FlushToDisk { get; set; } = true;
}
