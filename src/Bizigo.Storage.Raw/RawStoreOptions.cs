namespace Bizigo.Storage.Raw;

public sealed class RawStoreOptions
{
    public const string SectionName = "RawStore";

    /// <summary>S3 uç noktası. RustFS, SeaweedFS ya da gerçek S3 — fark etmez.</summary>
    public string ServiceUrl { get; set; } = "http://localhost:9000";

    public string Bucket { get; set; } = "bizigo-raw";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>AWS dışı uygulamalar sanal barındırma stilini genelde desteklemez.</summary>
    public bool ForcePathStyle { get; set; } = true;

    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Hedef nesne boyutu (sıkıştırma öncesi). Aşıldığında grup için yeni nesne
    /// açılır. ~64 MB, F1 §7.1.
    /// </summary>
    public long TargetObjectBytes { get; set; } = 64L * 1024 * 1024;

    public int CompressionLevel { get; set; } = 3;

    /// <summary>Yükleyicinin bekleyen segmentlere bakma sıklığı.</summary>
    public TimeSpan UploadInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Segment, yüklendiği <b>doğrulandıktan sonra</b> bu süre daha tutulur
    /// (F1 §7.0 koruma #3). RustFS bu pencerede veri kaybederse yerelden yeniden
    /// yüklenebilir. Sıfırlanırsa koruma kalkar.
    /// </summary>
    public TimeSpan SegmentRetention { get; set; } = TimeSpan.FromHours(48);

    public TimeSpan ScrubInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Her scrub turunda kaç nesne örnekleneceği.</summary>
    public int ScrubSampleSize { get; set; } = 20;

    /// <summary>
    /// Bir nesne kaç kez kurtarılmaya çalışılır (T40).
    ///
    /// <para>
    /// Sınır olmasaydı bozuk bir S3 yapılandırması sonsuz yeniden yazma
    /// döngüsü üretirdi. Sınıra ulaşan nesne <c>Unrecoverable</c> oluyor —
    /// yani "hâlâ deniyor" ile "artık denemiyor" ayırt edilebiliyor.
    /// </para>
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 3;
}
