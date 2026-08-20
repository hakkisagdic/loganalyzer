using System.Text.RegularExpressions;
using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.ControlPlane;

namespace Bizigo.UnitTests;

/// <summary>
/// Alarm bağlantısının <b>hedefi</b> — zincirin T23'te açık kalan halkası (T27).
///
/// <para>
/// <c>AlertLinkTests</c> bağlantının <b>şeklini</b> sınıyor: aralık doğru mu,
/// UTC mi, kök yoksa null mu. Sınamadığı şey bağlantının bir yere <b>varıp
/// varmadığı</b>: rota adı değişirse alarm yine gönderilir, kullanıcı bağlantıya
/// tıklar ve 404 görür. Bildirimin tek işi "şuna bak" demek olduğuna göre bu,
/// alarmın sessizce işe yaramaz hâle gelmesi demek.
/// </para>
///
/// <para>
/// Buradaki testler iki katmanı birbirine bağlıyor: <c>Alerting:SearchPath</c>
/// gerçek bir Next.js rotasına denk gelmeli, ve bağlantının taşıdığı parametre
/// adları arama ekranının <b>okuduğu</b> adlar olmalı. İkisi de dosya sisteminden
/// okunuyor; konteyner ya da koşan bir tarayıcı gerekmiyor.
/// </para>
/// </summary>
public sealed partial class AlertLinkTargetTests
{
    private static readonly DateTimeOffset From = new(2026, 8, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 8, 19, 10, 15, 0, TimeSpan.Zero);

    /// <summary>Üretimin varsayılanı; <c>appsettings.json</c> ile aynı olmak zorunda.</summary>
    private static AlertingOptions Options() => new()
    {
        ProductBaseUrl = "https://bizigo.example",
        SearchPath = "/olaylar",
    };

    private static AlertRuleEntity Rule() => new()
    {
        Id = Guid.Parse("0198f0c2-1a2b-7c3d-8e4f-5a6b7c8d9e01"),
        Name = "fw-core-01 sessiz",
        OwnerSubject = "esra",
        OwnerGroups = "network/core",
    };

    private static string Link(string? sourceId = null) =>
        AlertLinkBuilder.Build(Options(), Rule(), From, To, sourceId)
            ?? throw new InvalidOperationException("Bağlantı üretilemedi.");

    [GeneratedRegex(@"^\s*""SearchPath""\s*:\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex ConfiguredSearchPath();

    [GeneratedRegex(@"^\s*(?<name>\w+):\s*""(?<value>[^""]+)"",", RegexOptions.Multiline)]
    private static partial Regex ParamEntry();

    // ------------------------------------------------------------ hedef var mı

    [Fact]
    public void Baglantinin_gosterdigi_rota_gercekten_var()
    {
        // `SearchPath` bir Next.js rotası: `/olaylar` → `ui/src/app/olaylar/page.tsx`.
        // Rota yeniden adlandırılır ve bu ayar unutulursa alarm gönderilmeye
        // devam eder, bağlantı 404 verir ve kimse fark etmez.
        var path = Options().SearchPath.Trim('/');

        var page = Path.Combine(RepositoryLayout.Root, "ui", "src", "app", path, "page.tsx");

        Assert.True(
            File.Exists(page),
            $"Alerting:SearchPath '/{path}' bir ekrana denk gelmiyor — beklenen dosya: {page}");
    }

    [Fact]
    public void Yapilandirmadaki_yol_uretimin_varsayilaniyla_ayni()
    {
        // İkisi ayrışırsa test ortamı doğru, üretim yanlış bağlantı üretir —
        // ve fark yalnızca gerçek bir alarm gönderildiğinde görünür.
        var appsettings = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "src", "Bizigo.Api", "appsettings.json"));

        var configured = ConfiguredSearchPath().Match(appsettings);

        Assert.True(configured.Success, "appsettings.json içinde Alerting:SearchPath bulunamadı.");
        Assert.Equal(Options().SearchPath, configured.Groups["path"].Value);
    }

    // ------------------------------------------------ ekran parametreyi okuyor mu

    [Fact]
    public void Baglantinin_tasidigi_parametreleri_ekran_okuyor()
    {
        // Ekranın okuduğu adlar `ui/src/lib/events/criteria.ts` içindeki tek
        // sözlükte. Bağlantı başka bir ad taşırsa ekran onu sessizce yok sayar:
        // kullanıcı doğru sayfaya gider, YANLIŞ aralığı görür — ve bu, hiç
        // gitmemekten kötü, çünkü olayın olmadığına ikna olur.
        var known = ScreenParameters();

        var query = new Uri(Link("fg-ankara-01")).Query.TrimStart('?');

        var carried = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)[0])
            .ToArray();

        Assert.NotEmpty(carried);

        // `kural` ekranın filtre sözlüğünde değil ve olmamalı: kaydedilmiş
        // aramanın filtrelerini URL'e kodlamak yerine kural KİMLİĞİ taşınıyor
        // (AlertLinkBuilder'ın gerekçesi). Ekran onu ayrıca ele alıyor.
        var filters = carried.Where(name => name != "kural").ToArray();

        var unknown = filters.Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unknown.Length == 0,
            "Bağlantı arama ekranının tanımadığı parametre taşıyor: " + string.Join(", ", unknown) +
            "\n\nEkranın tanıdıkları: " + string.Join(", ", known.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Bağlantı kural kimliğini <b>taşıyor</b>.
    ///
    /// <para>
    /// <b>Zincirin açık halkası burada</b> (T27 bulgusu): kimlik taşınıyor ama
    /// arama ekranı onu <b>okumuyor</b>. <c>AlertLinkBuilder</c>'ın kendi
    /// yorumu "ekran kuralı kimliğinden okuyor" diyor; bu, yazıldığı gün
    /// planlanmış ama hiç bağlanmamış.
    /// </para>
    ///
    /// <para>
    /// Sonucu 404 değil, daha sinsisi: kullanıcı doğru ekrana ve doğru zaman
    /// aralığına gidiyor ama kuralın <b>alan filtreleri olmadan</b>. "5 dakikada
    /// <c>action=deny</c> &gt; 100" alarmı, o beş dakikanın <b>bütün</b>
    /// olaylarını gösteren bir ekran açıyor. Alarm "şuna bak" diyor, ekran daha
    /// geniş bir şey gösteriyor.
    /// </para>
    ///
    /// <para>
    /// Bu test kimliğin taşındığını sabitliyor; tüketilmesi
    /// <c>F2FlowTests</c>'in zincir haritasında <b>açık halka</b> olarak
    /// kayıtlı. Kapatan kişi burayı da genişletmeli.
    /// </para>
    /// </summary>
    [Fact]
    public void Kural_kimligi_baglantida_tasiniyor()
    {
        Assert.Contains(
            "kural=0198f0c2-1a2b-7c3d-8e4f-5a6b7c8d9e01", Link(), StringComparison.Ordinal);

        // Ekranın okumadığını da sabitliyoruz: bu satır düştüğü gün halka
        // kapanmış demektir ve zincir haritası güncellenmeli.
        var page = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "ui", "src", "app", "olaylar", "page.tsx"));

        Assert.False(
            page.Contains("kural", StringComparison.Ordinal),
            "Arama ekranı artık `kural` parametresini okuyor gibi görünüyor — " +
            "F2FlowTests'teki zincir haritasında bu halkayı 'açık' bırakmayın.");
    }

    [Fact]
    public void Kaynak_filtresi_ekranin_adiyla_gidiyor()
    {
        // F1 ölçümü: keyset sayfalama ancak `owner_group` + `source_id` ile
        // sabit süreli. Bağlantı kaynak taşıyorsa ekranın onu tanıması şart,
        // yoksa alarmdan açılan arama derin sayfada yavaşlıyor.
        Assert.Contains("source_id=fg-ankara-01", Link("fg-ankara-01"), StringComparison.Ordinal);
        Assert.Contains("source_id", ScreenParameters());
    }

    /// <summary>
    /// Arama ekranının tanıdığı sorgu parametreleri — <c>PARAM</c> sözlüğünden
    /// okunuyor.
    ///
    /// <para>
    /// TypeScript'i ayrıştırmak yerine sözlüğü metin olarak okumak kırılgan
    /// görünüyor ama alternatifi daha kötü: adları burada elle tekrarlamak, tam
    /// da sınamak istediğimiz ayrışmayı test tarafına kopyalamak olurdu.
    /// Sözlüğün biçimi değişirse test bulamadığını söyleyerek düşüyor.
    /// </para>
    /// </summary>
    private static HashSet<string> ScreenParameters()
    {
        var source = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "ui", "src", "lib", "events", "criteria.ts"));

        var start = source.IndexOf("export const PARAM = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "criteria.ts içinde PARAM sözlüğü bulunamadı.");

        var end = source.IndexOf("} as const;", start, StringComparison.Ordinal);
        Assert.True(end > start, "PARAM sözlüğünün sonu bulunamadı.");

        var names = ParamEntry()
            .Matches(source[start..end])
            .Select(m => m.Groups["value"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(names);

        return names;
    }
}
