using System.Globalization;

namespace Bizigo.Replay;

/// <param name="From">Aralığın başı (dahil).</param>
/// <param name="To">Aralığın sonu (hariç).</param>
/// <param name="ParserId">Sabitlenmiş parser kimliği; boşsa dispatcher normal seçim yapar.</param>
/// <param name="ParserVersion">Sabitlenmiş sürüm. Boş bırakmak "en güncel" demek — replay'i tekrarlanamaz kılar.</param>
/// <param name="OwnerGroups">Filtre. Boşsa bölümün tamamı yeniden işlenir.</param>
/// <param name="SourceIds">Filtre. Boşsa bölümün tamamı yeniden işlenir.</param>
public sealed record ReplayPlan
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    public string ParserId { get; init; } = string.Empty;
    public string ParserVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>
    /// Manifest'te eksik nesne bulunsa bile devam edilsin mi.
    ///
    /// <para>
    /// Varsayılan <see langword="false"/>: eksik nesne, replay'in o aralığı
    /// <b>sessizce kısa döndürmesi</b> demek. Manifest'in (K25 koruma #4) var
    /// olma sebebi tam olarak bunu bir hata mesajına çevirmek.
    /// </para>
    /// </summary>
    public bool ContinueOnMissingObjects { get; init; }

    /// <summary>
    /// <b>Hâlâ yazılan</b> bir bölümün de replay'e dâhil edilmesine izin verilsin mi.
    ///
    /// <para>
    /// Varsayılan <see langword="false"/> ve gerekçesi F1'in açık bıraktığı
    /// ölçümün cevabı: <c>REPLACE PARTITION</c> atomik <b>ama bu yetmiyor</b>.
    /// Motor önce mevcut satırları okuyup gölge tabloyu kuruyor, sonra bölümü
    /// değiştiriyor. O iki adım arasında canlı ingest'in aynı bölüme yazdığı her
    /// satır gölgede yok — ve değiştirme onu <b>sessizce siliyor</b>. Atomiklik
    /// yalnızca "yarım bölüm görünmez" diyor; anlık görüntüden sonra geleni
    /// korumuyor.
    /// </para>
    ///
    /// <para>
    /// Bu yüzden açık bölüm (bugünün bölümü) varsayılan olarak reddediliyor.
    /// Geçmiş bölümlerde yeni yazma olmadığı için tehlike yok ve replay'in
    /// olağan kullanımı zaten geçmiş. Bugünü de kapsamak isteyen, ingest'i
    /// durdurduğunu bilerek bu bayrağı açıyor.
    /// </para>
    /// </summary>
    public bool AllowOpenPartition { get; init; }

    public bool HasFilter => OwnerGroups.Count > 0 || SourceIds.Count > 0;

    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{From:yyyy-MM-dd}..{To:yyyy-MM-dd} parser={ParserId}@{ParserVersion} filtre={HasFilter}");
}

/// <param name="Field">Değişen alan adı.</param>
/// <param name="Before">Eski değer.</param>
/// <param name="After">Yeni değer.</param>
public sealed record FieldChange(string Field, string Before, string After);

/// <param name="EventId">Değişen olayın kimliği.</param>
/// <param name="StatusBefore">Eski <c>parse_status</c>.</param>
/// <param name="StatusAfter">Yeni <c>parse_status</c>.</param>
/// <param name="Changes">Değişen alanlar (örneklerde dolu, sayımda boş).</param>
public sealed record EventDiff(
    Guid EventId,
    string StatusBefore,
    string StatusAfter,
    IReadOnlyList<FieldChange> Changes);

/// <summary>
/// <c>--dry-run</c> fark raporu.
///
/// <para>
/// <b>Bu rapor olmadan replay korkutucu bir düğmedir ve kimse basmaz.</b>
/// Özelliğin değeri tam da burada: kullanıcı çalıştırmadan önce ne değişeceğini
/// görüyor.
/// </para>
/// </summary>
public sealed record ReplayReport
{
    public required ReplayPlan Plan { get; init; }
    public required IReadOnlyList<string> Partitions { get; init; }

    /// <summary>Arşivden okunup yeniden ayrıştırılan kayıt sayısı.</summary>
    public int RecordsReplayed { get; init; }

    /// <summary>Mevcut olayla birebir aynı çıkan kayıtlar.</summary>
    public int Unchanged { get; init; }

    /// <summary>En az bir alanı değişen kayıtlar.</summary>
    public int Changed { get; init; }

    /// <summary><c>failed</c> → <c>ok</c> dönenler. Replay'in asıl vaadi bu sayı.</summary>
    public int FailedToOk { get; init; }

    /// <summary><c>ok</c> → <c>failed</c> dönenler. Sıfırdan büyükse parser bir gerileme getirmiş.</summary>
    public int OkToFailed { get; init; }

    /// <summary>Arşivde olup ClickHouse'ta karşılığı olmayan kayıtlar.</summary>
    public int NewRows { get; init; }

    /// <summary>
    /// Canlı yazmaya açık olduğu için <b>atlanan</b> bölümler.
    ///
    /// <para>
    /// Boş olmayan bir liste sessiz bir kısalma değil, raporun söylediği bir
    /// karar: "bu aralığın şu bölümü replay edilmedi, çünkü hâlâ yazılıyor".
    /// </para>
    /// </summary>
    public IReadOnlyList<string> SkippedOpenPartitions { get; init; } = [];

    /// <summary>Filtre dışı olduğu için değiştirilmeden kopyalanan satırlar.</summary>
    public int CopiedUnchanged { get; init; }

    /// <summary>Manifest'te olup arşivde bulunamayan nesneler.</summary>
    public IReadOnlyList<string> MissingObjects { get; init; } = [];

    /// <summary>Alan bazında değişiklik sayısı — hangi alanın etkilendiğini gösterir.</summary>
    public IReadOnlyDictionary<string, int> ChangesByField { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Örnek farklar. Sayı tek başına "doğru mu" sorusunu cevaplamıyor.</summary>
    public IReadOnlyList<EventDiff> Samples { get; init; } = [];

    public TimeSpan Duration { get; init; }

    /// <summary><see langword="false"/> ise yalnızca rapor üretildi, yazma yapılmadı.</summary>
    public bool Applied { get; init; }

    public bool HasMissingObjects => MissingObjects.Count > 0;
}
