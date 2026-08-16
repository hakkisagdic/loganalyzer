namespace Bizigo.Ingest.Pipeline;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// Bellek içi kanalın kapasitesi (batch adedi). Sınırlı olması şart: sınırsız
    /// kanal, backpressure'ı bellek tükenmesine erteler.
    /// </summary>
    public int ChannelCapacity { get; set; } = 1024;

    /// <summary>0 ise <see cref="Environment.ProcessorCount"/>.</summary>
    public int WorkerCount { get; set; }

    /// <summary>Tek istekte kabul edilen en büyük gövde.</summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Kaynak envanterde bulunamazsa kullanılacak yedek kod sayfası. Kaynak bazlı
    /// değer envanterden gelir (T06); bu, hiçbir bilgi yokken kullanılan taban.
    /// </summary>
    public string DefaultFallbackEncoding { get; set; } = "windows-1254";

    public int EffectiveWorkerCount => WorkerCount > 0 ? WorkerCount : Environment.ProcessorCount;
}
