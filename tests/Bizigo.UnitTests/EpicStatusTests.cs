using System.Text.RegularExpressions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>T53 — ticket <c>status</c> alanlarını gözlenebilir bir olguya bağlar.</b>
///
/// <para>
/// <c>kalan-is-raporu</c> §6 sorunu kendi kaydına yazmıştı: <i>ticket
/// <c>status</c> alanları bayatlıyor … düzelttim, ama mekanizma yok — alanları
/// güncel tutan bir bekçi olmadığı sürece bir sonraki rapor da aynı düzeltmeyi
/// yapacak.</i> Öyle de oldu: bu hafta bir ajanın brief'i bayat bir statüden
/// türedi ve <b>zaten yazılmış bir ticket'ı</b> açmak üzere gönderildi. Turun
/// yarısı yanlış öncülü düzeltmekle geçti.
/// </para>
///
/// <para>
/// Aynı raporun cümlesi hedefi de veriyor: bu,
/// <see cref="WikiSourceDigestTests"/>'in vault için çözdüğü sorunun ticket
/// katmanındaki karşılığı.
/// </para>
///
/// <para>
/// <b>Bağlanan olgu: statünün diğer gösterimleri.</b> Üç aday vardı ve seçim
/// gerekçesi ticket'ta yazılı; seçilen, statünün depoda <b>zaten dört yerde</b>
/// yazılı olması ve bunların birbirinden ayrışmasının tamamen <b>mekanik</b>
/// olması:
/// </para>
///
/// <list type="number">
/// <item>ticket dosyasının <c>status:</c> alanı,</item>
/// <item>yol haritası tablosunun o ticket'a bağ vermesi
/// (<c>tickets-*/index.md</c>),</item>
/// <item><c>tickets-fs</c> tablosunun <c>Durum</c> sütunundaki işaret
/// (⬜ · 🔄 · ✅),</item>
/// <item><c>kalan-is-raporu</c>'nun "açık ticket'lar" tablosu.</item>
/// </list>
///
/// <para>
/// <b>Ne sınamıyor — beyan:</b> bu bekçi statünün <b>gerçeğe</b> uyduğunu
/// sınamıyor, çünkü "iş gerçekten bitti mi" gözlenebilir bir olgu değil.
/// Sınadığı şey, statünün <b>kendi dört gösteriminin birbirini yalanlamaması</b>.
/// Hepsi birden yanlış olabilir ve bekçi yeşil yanar. Bu bir eksiklik değil bir
/// sınır — ama yazılmazsa okuyan kişi bekçinin "ticket'lar doğru" dediğini
/// sanar.
/// </para>
///
/// <para>
/// Kapsam dışında kalan iki aday ve neden: <b>git log'daki ticket atfı</b>
/// güvenilmez — bu depodaki commit'lerin çoğu ticket kimliği taşımıyor
/// (<c>Merge branch …</c>, <c>Restamp the two pages …</c>), yani "atıf yok"
/// ile "iş yok" ayırt edilemiyordu. <b>Kabul kriterine karşılık gelen
/// test/dosyanın varlığı</b> ise kriter metnini makineye okutmayı gerektiriyor
/// ve bu, ölçtüğü şeyden daha kırılgan bir tahmin üretirdi.
/// </para>
/// </summary>
public sealed class EpicStatusTests
{
    private const int Done = 2;

    /// <summary>
    /// <c>Durum</c> sütunundaki işaretlerin <c>status</c> karşılığı.
    ///
    /// <para>
    /// Eşleme bir <b>varsayım</b> ve burada yazılı olması bu yüzden önemli:
    /// boş kutu "yapılmadı", döngü "sürüyor", tik "bitti". Tablo başka bir
    /// anlam kastediyorsa eşleme değişmeli — sessizce yanlış hizalanmamalı.
    /// </para>
    /// </summary>
    private static readonly (string Mark, int Status)[] StatusMarks =
    [
        ("⬜", 0),
        ("🔄", 1),
        ("✅", 2),
    ];

    /// <summary>
    /// <b>Bugün ayrışmış olanlar.</b> Her satır bir <b>bulgu</b>; düzeltme
    /// koordinatörde (T53 ölçer, düzeltmez — bazı düzeltmeler bu ajanın
    /// ölçmediği şeylere bağlı).
    ///
    /// <para>
    /// Bu liste <b>küçülmeli</b> ve <see cref="Listeler_bayat_giris_tasimiyor"/>
    /// bir satır düzeltildiğinde kırmızı yanıp silinmesini istiyor. Anahtar
    /// öneki hangi kapının konuştuğunu söylüyor: <c>yol-haritasi:</c> ·
    /// <c>durum:</c> · <c>rapor:</c>.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> KnownDivergence = new(StringComparer.Ordinal)
    {
        // 1 · Yol haritası tablosunda hiç görünmeyen ticket dosyaları. Yedisi de
        //     yazıldı, tablolarına eklenmedi — ticket dosyası ile yol haritası
        //     birbirinden ayrıştı ve ayrışmayı kimse görmüyordu.
        ["yol-haritasi:tickets-f2/ui-container"] = "T49 — yeni, F2 tablosuna eklenmedi.",
        ["yol-haritasi:tickets-f3/produces-kapisi-bagi"] = "T48 — yeni, F3 tablosuna eklenmedi.",
        ["yol-haritasi:tickets-f3/kompozisyon-koku-bagi"] = "T50 — yeni, F3 tablosuna eklenmedi.",
        ["yol-haritasi:tickets-f3/specificity-olcutu"] = "F3 tablosunda yok; kimliği de belirsiz.",
        ["yol-haritasi:tickets-f4/model-saglayicisi"] = "T42 — F4 tablosunda ticket satırı yok.",
        ["yol-haritasi:tickets-f4/senaryo-plugin-cekirdegi"] = "T43 — F4 tablosunda ticket satırı yok.",
        ["yol-haritasi:tickets/ham-arsiv-kurtarma"] = "F1 tablosunda yok.",

        // Bu ticket'ın KENDİ eklediği borç, sessizce üstlenilmedi. F3'ün
        // "Ticket listesi" tablosu T29–T38 arasında kürate edilmiş bir anlatı;
        // T48/T50/T53'ün oraya nasıl yerleşeceği koordinatörün kararı, ve o
        // dosyayı düzenlemek `references/f3-detection-ve-rca-kaniti` vault
        // sayfasının damgasını da bayatlatıyor (§11).
        ["yol-haritasi:tickets-f3/ticket-statusu-bekcisi"] = "T53 — bu bekçinin kendi ticket'ı.",

        // 2 · `tickets-fs` tablosunun Durum sütunu ile ticket dosyaları
        //     çelişiyor. `kalan-is-raporu` §6 bu dördü bir kez düzeltmişti;
        //     mekanizma olmadığı için yeniden ayrıştılar — raporun kendi
        //     öngörüsü gerçekleşti.
        ["durum:S02"] = "Tablo 🔄 diyor, ticket dosyası `status: 2`.",
        ["durum:S03"] = "Tablo 🔄 diyor, ticket dosyası `status: 2`.",
        ["durum:S04"] = "Tablo ⬜ diyor, ticket dosyası `status: 2`.",
        ["durum:S05"] = "Tablo ⬜ diyor, ticket dosyası `status: 2`.",

        // 3 · `kalan-is-raporu`'nun "açık" dediği kalemler.
        ["rapor:T38"] =
            "Rapor açık diyor, `tickets-f3/altin-kume` `status: 2`. Bu haftaki yanlış " +
            "brief'in tam kaynağı: aynı commit hem ticket'ı 2 yaptı hem raporda açık bıraktı.",
        // Gerekçe 2026-09-05'te değişti ve değiştiği yazılıyor: T44'ün dosyası
        // ARTIK VAR (merge `491d28d`) ve `status: 2` diyor, rapor hâlâ açık
        // diyor. Yani ayrışma sürüyor ama sebebi başka — eskisi "belge yok",
        // yenisi "belge var ve çelişiyor". Metni güncellemeseydik bu kapı adı
        // ile gövdesi ayrışan bir bekçiye dönerdi: liste doğru kalemi tutuyor,
        // yanındaki cümle yalan söylüyor.
        ["rapor:T44"] = "Raporda açık, `tickets-f4/llm-adimlari-ve-iki-kapi` `status: 2`.",
        ["rapor:T48"] = "Raporda açık; ticket dosyası var ama yol haritası tablosunda olmadığı için eşlenemiyor.",
        ["rapor:T49"] = "Raporda açık; ticket dosyası var ama yol haritası tablosunda olmadığı için eşlenemiyor.",
    };

    /// <summary>
    /// <b>Hiçbir zaman hizalanmayacak olanlar.</b> Gerekçesiyle, ve sayısı
    /// <see cref="ExpectedStructuralCount"/> ile çivili.
    ///
    /// <para>
    /// <c>CLAUDE.md</c> §8: <i>"bir gün kapanacak" ile "hiç kapanmayacak" aynı
    /// listede duramaz</i> — ikisi tek listedeyken
    /// <see cref="KnownDivergence"/>'ın boşalıp boşalmadığı sorulamaz hâle
    /// gelir. Bugün <b>boş</b>: yukarıdaki on altı satırın hangisinin kalıcı
    /// olduğu bu ajanın kararı değil.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> StructurallyUnaligned =
        new(StringComparer.Ordinal);

    private const int ExpectedStructuralCount = 0;

    // ---------------------------------------------------------------------
    // Okuma
    // ---------------------------------------------------------------------

    private sealed record Ticket(string Key, string Path, string Kind, int? Status);

    private sealed record RoadmapRow(string Id, string Key, string Story, int? DeclaredStatus);

    private static string EpicRoot => Path.Combine(RepositoryLayout.Root, "docs", "epic");

    /// <summary>Ticket dizininin <c>docs/epic</c>'e göre yolu — listelerin anahtarı.</summary>
    private static string KeyOf(string indexPath) =>
        Path.GetRelativePath(EpicRoot, Path.GetDirectoryName(indexPath)!)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static readonly Lazy<IReadOnlyList<Ticket>> Documents = new(ReadDocuments);

    private static IReadOnlyList<Ticket> ReadDocuments()
    {
        var found = new List<Ticket>();

        foreach (var path in Directory
            .EnumerateFiles(EpicRoot, "index.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            var kind = Regex.Match(text, @"^kind:\s*(\S+)", RegexOptions.Multiline);
            var status = Regex.Match(text, @"^status:\s*(\d+)", RegexOptions.Multiline);

            if (kind.Success)
            {
                found.Add(new Ticket(
                    KeyOf(path),
                    path,
                    kind.Groups[1].Value,
                    status.Success ? int.Parse(status.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null));
            }
        }

        return found;
    }

    private static IEnumerable<Ticket> Tickets() =>
        Documents.Value.Where(static d => d.Kind == "ticket");

    private static IEnumerable<Ticket> Stories() =>
        Documents.Value.Where(static d => d.Kind == "story");

    /// <summary>
    /// Yol haritası tablolarının satırları: <c>| T38 | [başlık](yol/index.md) | … |</c>.
    ///
    /// <para>
    /// Kimlik → ticket dosyası eşlemesinin <b>tek</b> kaynağı burası; ticket
    /// dosyaları kendi kimliklerini frontmatter'da taşımıyor. Bu bir kırılganlık
    /// ve beyanı <see cref="Bekci_bos_kume_uzerinde_donmuyor"/>: tablolar
    /// yeniden biçimlenip satırlar okunamaz hâle gelirse küme boşalır ve o test
    /// kırmızı yanar — sessiz bir yeşil olmaz.
    /// </para>
    /// </summary>
    private static readonly Lazy<IReadOnlyList<RoadmapRow>> Roadmap = new(ReadRoadmap);

    private static IReadOnlyList<RoadmapRow> ReadRoadmap()
    {
        var rows = new List<RoadmapRow>();

        foreach (var story in Directory
            .EnumerateDirectories(EpicRoot, "tickets*")
            .Select(directory => Path.Combine(directory, "index.md"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(story);

            foreach (Match row in Regex.Matches(
                text,
                @"^\|\s*\*{0,2}([TSM]\d+)\*{0,2}\s*\|\s*\[[^\]]*\]\(([^)]+)\)\s*\|(.*)$",
                RegexOptions.Multiline))
            {
                var target = Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(story)!, row.Groups[2].Value));

                rows.Add(new RoadmapRow(
                    row.Groups[1].Value,
                    KeyOf(target),
                    KeyOf(story),
                    DeclaredStatus(row.Groups[3].Value)));
            }
        }

        return rows;
    }

    private static int? DeclaredStatus(string cells)
    {
        foreach (var cell in cells.Split('|').Select(static c => c.Trim()))
        {
            foreach (var (mark, status) in StatusMarks)
            {
                if (cell.StartsWith(mark, StringComparison.Ordinal))
                {
                    return status;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <c>kalan-is-raporu</c>'nun "açık ticket'lar" bölümündeki kimlikler.
    /// </summary>
    private static IReadOnlyList<string> ReportedOpen()
    {
        var path = Path.Combine(EpicRoot, "kalan-is-raporu", "index.md");

        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        var heading = Regex.Match(text, @"^##[^\n]*Açık ticket[^\n]*$", RegexOptions.Multiline);

        if (!heading.Success)
        {
            return [];
        }

        var body = text[(heading.Index + heading.Length)..];
        var next = Regex.Match(body, @"^## ", RegexOptions.Multiline);
        var section = next.Success ? body[..next.Index] : body;

        return
        [
            .. Regex.Matches(section, @"^\|\s*\*{0,2}([TSM]\d+)\*{0,2}\s*\|", RegexOptions.Multiline)
                .Select(static m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static bool Excused(string key) =>
        KnownDivergence.ContainsKey(key) || StructurallyUnaligned.ContainsKey(key);

    // ---------------------------------------------------------------------
    // Kapılar
    // ---------------------------------------------------------------------

    /// <summary>
    /// Her ticket dosyası bir yol haritası tablosunda <b>görünüyor</b>.
    ///
    /// <para>
    /// Görünmeyen bir ticket, yol haritasının onu saymaması demek: faz sayıları
    /// ("9/12") onu içermiyor, ve kimliği hiçbir yere bağlanmadığı için rapor
    /// ondan söz ettiğinde ticket dosyasına ulaşılamıyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_ticket_bir_yol_haritasi_tablosunda_gorunuyor()
    {
        var listed = Roadmap.Value.Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);

        var invisible = Tickets()
            .Select(static t => t.Key)
            .Where(key => !listed.Contains(key))
            .Where(key => !Excused($"yol-haritasi:{key}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            invisible.Length == 0,
            "Bu ticket dosyaları hiçbir yol haritası tablosunda görünmüyor:\n  " +
            string.Join("\n  ", invisible) +
            "\n\nYa ilgili `tickets-*/index.md` tablosuna satır ekleyin, ya " +
            "`KnownDivergence`'a `yol-haritasi:<yol>` anahtarıyla gerekçesini yazın.");
    }

    /// <summary>
    /// Yol haritasındaki her bağ var olan bir ticket dosyasına gidiyor —
    /// tablonun kendisi bayatlamasın.
    /// </summary>
    [Fact]
    public void Yol_haritasi_baglari_var_olan_ticketa_gidiyor()
    {
        var known = Documents.Value.Select(static d => d.Key).ToHashSet(StringComparer.Ordinal);

        var dangling = Roadmap.Value
            .Where(row => !known.Contains(row.Key))
            .Select(static row => $"{row.Story} → {row.Id} ({row.Key})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            dangling.Length == 0,
            "Yol haritası var olmayan ticket'lara bağ veriyor:\n  " + string.Join("\n  ", dangling));
    }

    /// <summary>
    /// <b>Statünün ikinci gösterimi birinciyi yalanlamıyor.</b>
    /// <c>tickets-fs</c> tablosu her satırda bir <c>Durum</c> işareti taşıyor;
    /// işaret ile ticket dosyasının <c>status</c>'ü aynı şeyi söylemeli.
    /// </summary>
    [Fact]
    public void Durum_isaretleri_ticket_dosyasiyla_ayni_seyi_soyluyor()
    {
        var actual = Tickets().ToDictionary(static t => t.Key, static t => t.Status, StringComparer.Ordinal);

        var contradictions = Roadmap.Value
            .Where(static row => row.DeclaredStatus is not null)
            .Where(row => actual.TryGetValue(row.Key, out var status) && status != row.DeclaredStatus)
            .Where(row => !Excused($"durum:{row.Id}"))
            .Select(row => $"{row.Id}: tablo {row.DeclaredStatus}, dosya {actual[row.Key]} ({row.Key})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            contradictions.Length == 0,
            "Yol haritası tablosunun `Durum` sütunu ticket dosyasıyla çelişiyor:\n  " +
            string.Join("\n  ", contradictions) +
            "\n\nİki gösterim ayrıştığında hangisinin okunacağı okuyana kalıyor — ve bu " +
            "hafta yanlış olanı okundu.");
    }

    /// <summary>
    /// <b>Raporun "açık" dediği her kalem gerçekten açık.</b> Bu hafta kırılan
    /// bağ tam olarak buydu: rapor T38'i açık gösterdi, ticket dosyası
    /// <c>status: 2</c> taşıyordu, ve brief raporu okudu.
    /// </summary>
    [Fact]
    public void Rapor_acik_dedigi_her_kalem_gercekten_acik()
    {
        var byId = Roadmap.Value
            .GroupBy(static row => row.Id, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First().Key, StringComparer.Ordinal);

        var actual = Tickets().ToDictionary(static t => t.Key, static t => t.Status, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var id in ReportedOpen().Where(id => !Excused($"rapor:{id}")))
        {
            if (!byId.TryGetValue(id, out var key))
            {
                problems.Add($"{id}: raporda açık, ama hiçbir yol haritası satırı bu kimliği bir ticket dosyasına bağlamıyor.");
            }
            else if (!actual.TryGetValue(key, out var status))
            {
                problems.Add($"{id}: yol haritası {key} diyor, orada ticket dosyası yok.");
            }
            else if (status == Done)
            {
                problems.Add($"{id}: raporda AÇIK, ticket dosyası `status: {Done}` ({key}).");
            }
        }

        Assert.True(
            problems.Count == 0,
            "`kalan-is-raporu` ile ticket dosyaları çelişiyor:\n  " +
            string.Join("\n  ", problems.Order(StringComparer.Ordinal)) +
            "\n\nBir brief bu rapordan türediğinde yanlış öncülle başlıyor.");
    }

    /// <summary>
    /// Tamamlanmış bir story yarım ticket taşıyamaz.
    ///
    /// <para>
    /// <b>Tersi bilerek sınanmıyor:</b> bütün çocukları <c>2</c> olan bir story
    /// <c>2</c> olmak <b>zorunda değil</b> — henüz yazılmamış ticket'ları
    /// olabilir. O yönü sınamak, yazılmamış işi "yok" saymak olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Tamamlanmis_bir_story_yarim_ticket_tasimiyor()
    {
        var broken = new List<string>();

        foreach (var story in Stories().Where(static s => s.Status == Done))
        {
            var prefix = story.Key + "/";

            broken.AddRange(Tickets()
                .Where(ticket => ticket.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(static ticket => ticket.Status != Done)
                .Select(ticket => $"{story.Key} (status 2) → {ticket.Key} (status {ticket.Status})"));
        }

        Assert.True(
            broken.Count == 0,
            "Tamamlanmış story'nin altında bitmemiş ticket var:\n  " +
            string.Join("\n  ", broken.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// <b>Bekçi boş küme üzerinde dönmüyor.</b>
    ///
    /// <para>
    /// Bu bekçinin girdisi markdown ayrıştırması: bir başlık yeniden
    /// adlandırılırsa ya da tablo biçimi değişirse okunan küme <b>sessizce</b>
    /// boşalır ve yukarıdaki bütün kapılar yeşil yanar — kapsamını yitirmiş bir
    /// bekçi, olmayan bekçiyle aynı sonucu verir. Sayıların sıfırdan büyük
    /// olması bu yüzden ayrı bir kapı.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekci_bos_kume_uzerinde_donmuyor()
    {
        Assert.NotEmpty(Tickets());
        Assert.NotEmpty(Stories());
        Assert.NotEmpty(Roadmap.Value);
        Assert.NotEmpty(ReportedOpen());

        Assert.True(
            Roadmap.Value.Any(static row => row.DeclaredStatus is not null),
            "Hiçbir yol haritası satırında `Durum` işareti okunamadı — işaret eşlemesi " +
            "(⬜ · 🔄 · ✅) tablodan ayrışmış olabilir ve o kapı boş küme üzerinde dönüyor.");

        Assert.True(
            Tickets().All(static t => t.Status is not null),
            "Bazı ticket belgelerinde `status` alanı hiç yok: " +
            string.Join(", ", Tickets().Where(static t => t.Status is null).Select(static t => t.Key)));
    }

    /// <summary>
    /// Bir ayrışma düzeltildiğinde listeden <b>silinmeli</b>; yoksa liste
    /// küçülmeyi bırakır ve "boşaldı mı" sorusu cevapsız kalır.
    /// </summary>
    [Fact]
    public void Listeler_bayat_giris_tasimiyor()
    {
        var live = LiveDivergenceKeys();

        var settled = KnownDivergence.Keys.Concat(StructurallyUnaligned.Keys)
            .Where(key => !live.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            settled.Length == 0,
            "Artık ayrışmayan giriş(ler) hâlâ listede: " + string.Join(", ", settled) +
            " — silin, yoksa liste küçülmeyi bırakır.");
    }

    /// <summary>
    /// Listeler dışlamalar kaldırıldığında hangi anahtarların gerçekten
    /// ayrıştığını söyler — <see cref="Listeler_bayat_giris_tasimiyor"/>'un
    /// ölçüm tabanı.
    /// </summary>
    private static HashSet<string> LiveDivergenceKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var listed = Roadmap.Value.Select(static r => r.Key).ToHashSet(StringComparer.Ordinal);
        var actual = Tickets().ToDictionary(static t => t.Key, static t => t.Status, StringComparer.Ordinal);

        foreach (var ticket in Tickets().Where(t => !listed.Contains(t.Key)))
        {
            keys.Add($"yol-haritasi:{ticket.Key}");
        }

        foreach (var row in Roadmap.Value.Where(static r => r.DeclaredStatus is not null))
        {
            if (actual.TryGetValue(row.Key, out var status) && status != row.DeclaredStatus)
            {
                keys.Add($"durum:{row.Id}");
            }
        }

        var byId = Roadmap.Value
            .GroupBy(static row => row.Id, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First().Key, StringComparer.Ordinal);

        foreach (var id in ReportedOpen())
        {
            if (!byId.TryGetValue(id, out var key)
                || !actual.TryGetValue(key, out var status)
                || status == Done)
            {
                keys.Add($"rapor:{id}");
            }
        }

        return keys;
    }

    /// <summary>
    /// Kalıcı muafiyet sessizce büyüyemez — <c>CLAUDE.md</c> §8 ve
    /// <c>ProducesContractTests.ExpectedExemptCount</c> emsali.
    /// </summary>
    [Fact]
    public void Muafiyet_sessizce_buyuyemez()
    {
        Assert.True(
            StructurallyUnaligned.Count == ExpectedStructuralCount,
            $"Kalıcı muafiyet sayısı {ExpectedStructuralCount} olmalı, " +
            $"{StructurallyUnaligned.Count} bulundu. Muafiyet eklemek bu sabiti de " +
            "değiştirmeyi gerektiriyor.");

        foreach (var (key, reason) in KnownDivergence.Concat(StructurallyUnaligned))
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{key} girişi gerekçesiz.");
        }

        Assert.Empty(KnownDivergence.Keys.Intersect(StructurallyUnaligned.Keys, StringComparer.Ordinal));
    }
}
