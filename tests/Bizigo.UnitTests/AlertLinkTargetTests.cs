using System.Text.RegularExpressions;
using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
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

    private static AlertRuleEntity Rule(AlertSearch? search = null) => new()
    {
        Id = Guid.Parse("0198f0c2-1a2b-7c3d-8e4f-5a6b7c8d9e01"),
        Name = "fw-core-01 sessiz",
        OwnerSubject = "esra",
        OwnerGroups = "network/core",
        SearchJson = AlertSearchCodec.Serialize(search ?? new AlertSearch()),
    };

    private static string LinkFor(AlertSearch search) =>
        AlertLinkBuilder.Build(Options(), Rule(search), From, To)
            ?? throw new InvalidOperationException("Bağlantı üretilemedi.");

    private static Dictionary<string, List<string>> QueryOf(string link)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var pair in new Uri(link).Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0]);

            if (!map.TryGetValue(name, out var values))
            {
                values = [];
                map[name] = values;
            }

            values.Add(parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        return map;
    }

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

        // İkisi filtre değil ve `PARAM` sözlüğünde olmamalı: `kural` kaynak
        // göstergesi, `eksik` ise taşınamayan filtrelerin bildirimi. Ekran
        // ikisini de ayrıca ele alıyor.
        var filters = carried
            .Where(name => name is not ("kural" or AlertLinkBuilder.UnsupportedParam))
            .ToArray();

        var unknown = filters.Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            unknown.Length == 0,
            "Bağlantı arama ekranının tanımadığı parametre taşıyor: " + string.Join(", ", unknown) +
            "\n\nEkranın tanıdıkları: " + string.Join(", ", known.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// <c>kural</c> yalnızca <b>kaynak göstergesi</b> — filtrenin taşıyıcısı
    /// değil.
    ///
    /// <para>
    /// İlk tasarım ekranın kuralı kimliğinden okumasını öngörüyordu ve bu iki
    /// kere yanlıştı. Birincisi hiç bağlanmamıştı: kullanıcı doğru ekrana ve
    /// doğru aralığa gidiyor ama kuralın alan filtreleri olmadan. İkincisi
    /// bağlansaydı da yanlış olurdu: bağlantı bir kez üretilip bildirime
    /// gömülüyor, kullanıcı günler sonra tıklıyor ve kimliği çözen ekran
    /// <b>bugünkü</b> kuralı gösterirdi — tetiklenme anındakini değil.
    /// </para>
    ///
    /// <para>
    /// Artık filtreleri bağlantının kendisi taşıyor, yani bağlantı o anın
    /// fotoğrafı. Kimlik "bu aramayı hangi kural açtı" sorusunun cevabı olarak
    /// kalıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Kural_kimligi_kaynak_gostergesi_olarak_tasiniyor()
    {
        Assert.Contains(
            "kural=0198f0c2-1a2b-7c3d-8e4f-5a6b7c8d9e01", Link(), StringComparison.Ordinal);
    }

    // ------------------------------------------------ filtreler taşınıyor mu

    [Fact]
    public void Kuralin_filtreleri_baglantiya_giriyor()
    {
        // Kapatılan kusur: bu filtreler taşınmayınca "5 dakikada action=deny
        // > 100" alarmı, o beş dakikanın BÜTÜN olaylarını gösteren bir ekran
        // açıyordu.
        var query = QueryOf(LinkFor(new AlertSearch
        {
            FullText = "kullanıcı oturum",
            Filters =
            [
                FieldFilter.Eq("action", "deny"),
                FieldFilter.Eq("vendor", "fortinet"),
                FieldFilter.Eq("proto", "tcp"),
            ],
            SourceIds = ["fg-ankara-01"],
            ParseStatuses = [ParseStatus.Failed],
        }));

        Assert.Equal("deny", Assert.Single(query["action"]));
        Assert.Equal("fortinet", Assert.Single(query["vendor"]));
        Assert.Equal("tcp", Assert.Single(query["proto"]));
        Assert.Equal("kullanıcı oturum", Assert.Single(query["q"]));
        Assert.Equal("fg-ankara-01", Assert.Single(query["source_id"]));
        Assert.Equal("failed", Assert.Single(query["parse_status"]));

        // Kapsam da taşınıyor: `criteria-bridge` ileri yönde bunu "filtre değil
        // kapsam" diye işaretliyor, ters yönde ekranın parametresine denk geliyor.
        Assert.Equal("network/core", Assert.Single(query["owner_group"]));
    }

    [Fact]
    public void Siddet_esigi_ceviriden_kayiksiz_donuyor()
    {
        // İleri yönde ekranın "n ve üzeri"si `gt n-1`'e çevriliyor (operatör
        // kümesinde `gte` yok). Geri yönde 1 eklenmezse alarm bir kademe kayık
        // bir ekran açar — ve sapma tek kademe olduğu için kimse fark etmez.
        var query = QueryOf(LinkFor(new AlertSearch
        {
            Filters = [new FieldFilter("severity_num", FilterOperator.GreaterThan, ["6"])],
        }));

        Assert.Equal("7", Assert.Single(query["severity_min"]));
    }

    [Fact]
    public void Acikca_verilen_kaynak_kuralinkini_eziyor()
    {
        // Sessizlik alarmı TEK bir kaynağı işaret ediyor; kuralın listesi değil
        // o kaynak gösterilmeli.
        var link = AlertLinkBuilder.Build(
            Options(),
            Rule(new AlertSearch { SourceIds = ["fg-ankara-01", "fg-ankara-02"] }),
            From,
            To,
            "fg-ankara-02")!;

        Assert.Equal("fg-ankara-02", Assert.Single(QueryOf(link)["source_id"]));
    }

    // --------------------------------------- taşınamayan filtre sessiz düşmüyor

    [Fact]
    public void Ekranda_karsiligi_olmayan_filtre_bildiriliyor()
    {
        // Sessizce düşmesi, kullanıcının alarmın izlediğinden GENİŞ bir kümeye
        // bakıp onu alarmın kümesi sanması demek. Ekran bunu söylüyor.
        var query = QueryOf(LinkFor(new AlertSearch
        {
            Filters =
            [
                FieldFilter.Eq("action", "deny"),
                FieldFilter.Eq("src_ip", "10.0.0.1"),
                FieldFilter.Eq("user_name", "esra"),
            ],
        }));

        Assert.Equal("deny", Assert.Single(query["action"]));

        // Ad sırası belirli: iki farklı çekimde farklı sıralı bir bağlantı,
        // aynı alarmın iki farklı bağlantısı gibi görünürdü.
        Assert.Equal("src_ip,user_name", Assert.Single(query[AlertLinkBuilder.UnsupportedParam]));
    }

    [Fact]
    public void Tasinamayan_filtre_yoksa_isaret_de_yok()
    {
        var query = QueryOf(LinkFor(new AlertSearch { Filters = [FieldFilter.Eq("action", "deny")] }));

        Assert.False(query.ContainsKey(AlertLinkBuilder.UnsupportedParam));
    }

    [Fact]
    public void Eksik_isaretini_ekran_okuyor()
    {
        // Diğer parametrelerle aynı kural: ekranın tanımadığı bir işaret koymak,
        // filtreyi sessizce düşürmekle aynı sonucu verirdi.
        var criteria = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "ui", "src", "lib", "events", "criteria.ts"));

        Assert.Contains($"\"{AlertLinkBuilder.UnsupportedParam}\"", criteria, StringComparison.Ordinal);

        var page = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "ui", "src", "app", "olaylar", "page.tsx"));

        Assert.Contains("unsupportedFilters", page, StringComparison.Ordinal);
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
