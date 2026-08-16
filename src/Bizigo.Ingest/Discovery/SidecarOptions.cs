namespace Bizigo.Ingest.Discovery;

/// <summary>
/// Python sidecar istemcisinin ayarları (F1 §9 tablosu).
///
/// <para>
/// Varsayılanların hepsi "sidecar yokmuş gibi davran" tarafına eğimli.
/// <see cref="Enabled"/> kapatıldığında ingest'te tek bir kod yolu bile
/// değişmiyor — bu, K14'ün "sert bağımlılık kurulmayacak" maddesinin
/// çalıştırılabilir hâli.
/// </para>
/// </summary>
public sealed class SidecarOptions
{
    public const string SectionName = "Sidecar";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "http://localhost:8099";

    /// <summary>Beklenen sözleşme sürümü. Uyuşmazsa devre kesici açılır.</summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>Beklenen maskeleme sözlüğü sürümü; 0 ise kontrol edilmez.</summary>
    public int MasksVersion { get; set; }

    /// <summary>F1 §9: 2 sn; aşan istek iptal edilir.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Sınırlı kuyruk. Dolunca <b>düşürülür</b> — ingest asla beklemez.
    /// </summary>
    public int QueueCapacity { get; set; } = 2048;

    public int BatchSize { get; set; } = 200;

    /// <summary>Ardışık hata sayısı; aşılınca devre kesici açılır.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>F1 §9: 5 dk kapalı.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Başarıyla ayrıştırılmış olaylardan örnekleme oranı (0–1). Amaç
    /// <c>template_id</c> üretmek değil, miner'ın küme modelini canlı tutmak:
    /// yalnızca <c>failed</c> beslersek model yalnızca bozuk satırları öğrenir
    /// ve "bu satır neye benziyor" sorusunun karşılaştıracak bir tabanı olmaz.
    /// </summary>
    public double SampleRate { get; set; } = 0.01;

    /// <summary>İmza → <c>template_id</c> önbelleğinin üst sınırı.</summary>
    public int TemplateCacheCapacity { get; set; } = 50_000;

    /// <summary>Maskeleme sözlüğü — sidecar'ın okuduğu <b>aynı</b> dosya.</summary>
    public string MaskFile { get; set; } = "catalog/masks/bizigo-masks.yaml";
}
