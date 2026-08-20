using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Evidence;

/// <summary>
/// Saklanan kanıt paketi (T36, RCA §4.1).
///
/// <para>
/// <b>Bu tip bir anlık görüntü, bir görünüm değil.</b> Bugün yazılan bir paket
/// altı ay sonra, o günkü kodla okunacak — F4'ün "aynı kanıt üzerinde farklı
/// model koşturup karşılaştır" ihtiyacının tamamı buna dayanıyor. Saklanmasaydı
/// her karşılaştırma kanıtı yeniden toplamak zorunda kalırdı ve o sırada veri
/// değişmiş olurdu; yani karşılaştırma modeli değil, veriyi ölçerdi.
/// </para>
///
/// <para>
/// Bunun bedeli: <b>şekil donuyor.</b> <see cref="SchemaVersion"/> o donmayı
/// görünür kılıyor ve bir bekçi testi, diske yazılmış eski bir paketi bugünkü
/// kodun okuyabildiğini sınıyor.
/// </para>
/// </summary>
public sealed record EvidenceBundle
{
    /// <summary>
    /// Paket biçiminin sürümü.
    ///
    /// <para>
    /// Artırmak <b>iki ayrı bilinçli hareket</b> gerektiriyor: burayı değiştirmek
    /// ve <c>EvidenceBundleTests</c>'teki eski sürüm fixture'ının hâlâ
    /// okunabildiğini göstermek. Sessizce artırmak, geçmiş paketleri okunamaz
    /// yapıp bunu kimseye söylememek olurdu.
    /// </para>
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public required Guid Id { get; init; }

    /// <summary>
    /// Paketin üretildiği an. <b>İçerik hash'ine girmiyor</b> — girseydi aynı
    /// girdiden üretilen iki paket asla eşleşmezdi ve determinizm iddiası
    /// sınanamaz olurdu.
    /// </summary>
    public required DateTimeOffset GatheredAt { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required RcaWindow Window { get; init; }

    /// <summary>
    /// Paketi üreten kapsam — <b>kimin gözüyle</b> toplandığı. Kanıt kapsam
    /// altında toplandığı için (K17) bu bilgi olmadan paket yeniden
    /// yorumlanamaz: aynı pencerede farklı kapsamla toplanmış iki paket farklı
    /// şeyler görür ve ikisi de doğrudur.
    /// </summary>
    public required BundleScope Scope { get; init; }

    public required IReadOnlyList<EvidenceSlice> Slices { get; init; }

    /// <summary>
    /// Pencerenin zaman güvenilirliği — raporun "zaman dürüstlüğü" satırının
    /// kaynağı. Pakette duruyor, rapor üretiminde yeniden sorgulanmıyor: saklanan
    /// bir paketten üretilen rapor, o günkü veriye tekrar gitmek zorunda kalmadan
    /// aynı cümleyi kurabilmeli.
    /// </summary>
    public required WindowTrust Trust { get; init; }

    /// <summary>
    /// Kapsam dışı toplam (RCA §3.2) — <b>yalnızca sayı</b>, içerik değil.
    /// </summary>
    public long OutOfScopeCount => Slices.Sum(s => s.OutOfScopeCount);

    /// <summary>
    /// Aynı pencere + kapsam + kanıt için aynı değer.
    ///
    /// <para>
    /// Duvar saati taşıyan her şey <b>dışarıda</b>: <see cref="GatheredAt"/>,
    /// <see cref="Id"/> ve dilimlerin <c>Duration</c>'ı. İçeride bırakmak, "aynı
    /// girdiyle aynı paket" kabul kriterini tanım gereği yanlış yapardı. Aynı
    /// ayrım <c>ReplayDiff</c>'te de var ve aynı sebeple: her koşumda değişen bir
    /// alanı karşılaştırmaya sokmak, gerçek farkları görünmez kılar.
    /// </para>
    /// </summary>
    public string ContentHash => BundleSerializer.HashOf(this);

    public IEnumerable<EvidenceItem> Items => Slices.SelectMany(s => s.Items);

    /// <summary>Rapor eksik kanıtla mı kuruluyor — okuyanın görmesi gereken.</summary>
    public bool IsPartial =>
        Slices.Any(s => s.Status is EvidenceStatus.Failed or EvidenceStatus.Unavailable || s.Truncated);

    /// <summary>Bakılamayan türler: kapalı, patlamış ya da hiç kayıtlı olmayan.</summary>
    public IReadOnlyList<EvidenceSlice> NotConsulted => [.. Slices.Where(s => !s.IsEvidence)];
}

/// <summary>
/// Paketin üretildiği kapsam — <c>AccessScope</c>'un <b>saklanabilir</b> özeti.
///
/// <para>
/// <c>AccessScope</c>'un kendisi saklanmıyor: o bir çalışma zamanı yetki nesnesi
/// ve içinde denetim kimliği taşıyor. Pakete yazılması gereken şey yetkinin
/// kendisi değil, <b>hangi grupların görüldüğü</b> — paketi altı ay sonra okuyan
/// kişinin "bu rapor neyi görebiliyordu" sorusunun cevabı.
/// </para>
/// </summary>
/// <param name="OwnerGroups">Boş liste = sistem kapsamı (her şey).</param>
/// <param name="IsSystem">Kapsam sınırsız mıydı.</param>
public sealed record BundleScope(IReadOnlyList<string> OwnerGroups, bool IsSystem);

/// <summary>
/// Pencerenin <b>ne kadarına güvenilebileceği</b> (T35 → T36 zaman dürüstlüğü).
///
/// <para>
/// Yayılma sırası <c>ts</c>'e dayanıyor ve zamanı <c>parsed</c> olmayan bir
/// olayın gerçek zamanı dakikalarca önce olabilir. Sıralamayı sunup bunu
/// söylememek, ölçülmemiş bir kesinlik iddia etmek olurdu.
/// </para>
///
/// <para>
/// <b>Neden sağlayıcıdan türetilmiyor:</b> yayılma sağlayıcısı yalnızca
/// <b>bozulma sayılan</b> olayları görüyor. Ondan türetilen bir sayı, yayılma
/// hiçbir şey döndürmediğinde sessizce sıfır olurdu — yani pencere baştan sona
/// güvenilmez zamanlı olsa bile rapor "sorun yok" derdi. Tam da bu ticket'ın
/// kaçındığı hata sınıfı.
/// </para>
/// </summary>
/// <param name="TotalEvents">Penceredeki toplam olay (kapsam altında).</param>
/// <param name="UnreliableTimeEvents">Zamanı <c>parsed</c> olmayanlar.</param>
/// <param name="Measured">
/// Ölçüm gerçekten yapıldı mı. <c>false</c> ise sayılar <b>bilinmiyor</b>
/// demektir, sıfır demek değil — rapor ikisini farklı yazıyor.
/// </param>
public sealed record WindowTrust(long TotalEvents, long UnreliableTimeEvents, bool Measured = true)
{
    /// <summary>Ölçülemedi. Sıfırla karıştırılmaması için ayrı bir değer.</summary>
    public static WindowTrust Unmeasured { get; } = new(0, 0, Measured: false);

    public bool HasUnreliableTime => Measured && UnreliableTimeEvents > 0;

    /// <summary>Güvenilmez zamanlı olayların oranı; ölçülmediyse <c>null</c>.</summary>
    public double? UnreliableRatio => Measured && TotalEvents > 0
        ? (double)UnreliableTimeEvents / TotalEvents
        : null;
}

/// <summary>
/// Paketin diske yazılan biçimi. <b>Tek yer</b>: kaydetme, okuma ve hash aynı
/// seçenekleri kullanmak zorunda, yoksa hash yazılanla okunanı farklı hesaplar.
/// </summary>
public static class BundleSerializer
{
    /// <summary>
    /// JSON adlandırma <c>snake_case</c> (depo kuralı §8). camelCase politikası
    /// bu depoda bir kez sözleşmeyi sessizce kırdı; saklanan bir belgede aynı
    /// hata geri dönülemez olurdu.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        WriteIndented = false,
    };

    public static string Serialize(EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, Options);
    }

    public static EvidenceBundle Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        return JsonSerializer.Deserialize<EvidenceBundle>(json, Options)
            ?? throw new InvalidOperationException("Kanıt paketi çözülemedi: boş belge.");
    }

    /// <summary>
    /// İçerik hash'i — <b>duvar saati taşıyan alanlar dışarıda</b>.
    ///
    /// <para>
    /// Dilimler sağlayıcı kimliğine göre sıralanıyor: sağlayıcılar paralel
    /// koşuyor ve kayıt sırası DI'nin insafında; sıralamadan hash almak, aynı
    /// girdinin iki koşumda farklı hash üretmesine kapı açardı.
    /// </para>
    ///
    /// <para>
    /// Dilim <b>içindeki</b> satırlar sıralanmıyor — sıra sinyalin kendisi
    /// (yayılma zaman sıralı, ilk-görülen hacim sıralı). Bu, sorguların kararlı
    /// bir <c>ORDER BY</c> eşitlik bozucusu taşımasını şart koşuyor; taşımayan
    /// tek sorgu bu ticket'ta düzeltildi.
    /// </para>
    /// </summary>
    public static string HashOf(EvidenceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var canonical = new StringBuilder();
        canonical.Append(CultureInfo.InvariantCulture, $"v{bundle.SchemaVersion}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"{bundle.Window.From:O}|{bundle.Window.To:O}\n");
        canonical.Append(
            CultureInfo.InvariantCulture,
            $"{bundle.Window.BaselineFrom:O}|{bundle.Window.BaselineTo:O}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"groups:{Join(bundle.Scope.OwnerGroups)}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"system:{bundle.Scope.IsSystem}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"narrow:{Join(bundle.Window.OwnerGroups)}\n");
        canonical.Append(CultureInfo.InvariantCulture, $"sources:{Join(bundle.Window.SourceIds)}\n");
        canonical.Append(
            CultureInfo.InvariantCulture,
            $"trust:{bundle.Trust.Measured}|{bundle.Trust.TotalEvents}|{bundle.Trust.UnreliableTimeEvents}\n");

        foreach (var slice in bundle.Slices.OrderBy(s => s.ProviderId, StringComparer.Ordinal))
        {
            canonical.Append(
                CultureInfo.InvariantCulture,
                $"# {slice.ProviderId}|{slice.Kind}|{slice.Status}|{slice.OutOfScopeCount}|" +
                $"{slice.Truncated}|{slice.Detail}\n");

            foreach (var item in slice.Items)
            {
                canonical.Append(
                    CultureInfo.InvariantCulture,
                    $"- {item.Id}|{item.Timestamp:O}|{item.Weight.ToString("R", CultureInfo.InvariantCulture)}|" +
                    $"{item.Summary}\n");

                foreach (var (key, value) in item.Payload.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    canonical.Append(CultureInfo.InvariantCulture, $"  {key}={value}\n");
                }
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(",", values.OrderBy(v => v, StringComparer.Ordinal));
}
