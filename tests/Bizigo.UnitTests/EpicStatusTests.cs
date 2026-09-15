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
/// <b>Ne sınamıyor — beyan (T53):</b> bu bekçi statünün <b>gerçeğe</b> uyduğunu
/// sınamıyor, çünkü "iş gerçekten bitti mi" gözlenebilir bir olgu değil.
/// Sınadığı şey, statünün <b>kendi dört gösteriminin birbirini yalanlamaması</b>.
/// Hepsi birden yanlış olabilir ve bekçi yeşil yanar. Bu bir eksiklik değil bir
/// sınır — ama yazılmazsa okuyan kişi bekçinin "ticket'lar doğru" dediğini
/// sanar.
/// </para>
///
/// <para>
/// <b>T61 o sınırın bir dilimini kaldırdı — ve tamamını değil.</b> T53'ün
/// beyanı bir kez daha ölçüldü ve gerçekleşti: MCP kolunun beş ticket'ı
/// <c>status: 0</c> görünürken kodları main'deydi, <b>dört gösterim de
/// birbiriyle tutarlıydı</b>, ve bekçi yeşil yandı. Yani iç tutarlılık kapısı
/// bu sınıfa yapısal olarak kör.
/// </para>
///
/// <para>
/// Bağlanan <b>beşinci</b> gösterim depo dışından geliyor: <c>git</c>'in merge
/// geçmişi. Üç aday ölçüldü ve seçim gerekçesi
/// <c>docs/epic/tickets-f3/statu-olgusu-bekcisi/index.md</c>'de; özeti,
/// seçilenin <b>yeni bir elle tutulan alan doğurmayan</b> tek aday olması.
/// </para>
///
/// <list type="table">
/// <item>
/// <term>Kodda bir sembolün varlığı</term>
/// <description>ticket → sembol eşlemesi <b>elle</b> yazılırdı; bayatlayan bir
/// alanı ikinci bir bayatlayan alanla ölçmek olurdu.</description>
/// </item>
/// <item>
/// <term>Kabul kriterinin karşılığı</term>
/// <description>kriter metnini makineye okutmak gerekirdi — T53 bunu
/// "ölçtüğü şeyden daha kırılgan bir tahmin" diye elemişti ve o gerekçe
/// hâlâ geçerli.</description>
/// </item>
/// <item>
/// <term><b>Merge geçmişindeki dal adı</b> (seçilen)</term>
/// <description><c>Merge branch 'm02-komut-cekirdegi'</c> — dal adının ön eki
/// (<c>m02</c>) ticket kimliği, ve kimlik → ticket dosyası eşlemesi
/// <b>zaten</b> yol haritası tablolarında yazılı. Yani yeni bir liste
/// doğmuyor: iki mevcut olgu birleştiriliyor.</description>
/// </item>
/// </list>
///
/// <para>
/// T53 git'i <i>"commit mesajları ticket kimliği taşımıyor"</i> diye elemişti ve
/// bu <b>doğruydu</b> — ama ölçtüğü şey commit mesajlarıydı, <b>merge dal
/// adları</b> değil. Ölçüm: 179 merge commit'inin 27'si <c>Merge branch
/// '&lt;dal&gt;'</c> biçiminde ve 24'ü ayrıştırılabilir bir kimlik ön eki
/// taşıyor; 24 kimliğin <b>hiçbirinde</b> iki farklı ticket dizinine düşen bir
/// çakışma yok.
/// </para>
///
/// <para>
/// <b>Yeni gösterimin sınırları — üçü de yazılı olmadan bırakılamaz:</b>
/// </para>
///
/// <list type="number">
/// <item>
/// <b><c>1</c> ile <c>2</c> arasını sınamıyor.</b> Merge edilmiş bir dal işin
/// <i>başladığını</i> kanıtlıyor, <i>bittiğini</i> değil — bir kol yarım da
/// merge edilebilir (bugün M03 ve T32 tam olarak bu hâlde). Kapı bu yüzden
/// yalnızca <c>0</c>'ı reddediyor. <c>1 → 2</c> geçişi bir <b>insan kararı</b>
/// ve öyle kalıyor.
/// </item>
/// <item>
/// <b>Dal adı olmayan merge'i göremiyor.</b> <c>Merge M08 and M06, bind the
/// scope seam …</c> gibi anlatısal merge mesajları kimlik taşımıyor. Kapı
/// yalnızca <b>eksik</b> yönde yanılıyor: göremediği iş için sessiz kalıyor,
/// yanlış bir ticket'ı suçlamıyor.
/// </item>
/// <item>
/// <b>Düzyazıyı hiç okumuyor.</b> <c>kalan-is-raporu</c>'nun MCP bölümü tam
/// bugün <i>"sekiz kalem"</i> diyor ve kalemleri <b>tablo değil düzyazı</b>
/// olarak sayıyor; <see cref="ReportedOpen"/> tablo satırı aradığı için o
/// bölüme kör. Düzyazıdaki kimlikleri eşleştirmek denendi ve <b>elendi</b>:
/// aynı paragraf kapanan ticket'ları da anıyor, yani "açık" ile "kapandı"
/// ayırt edilemiyordu.
/// </item>
/// </list>
/// </summary>
public sealed class EpicStatusTests
{
    private const int NotStarted = 0;
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
        // 1 · Yol haritası tablosunda hiç görünmeyen ticket dosyaları. Yazıldılar,
        //     tablolarına eklenmediler — ticket dosyası ile yol haritası
        //     birbirinden ayrıştı ve ayrışmayı kimse görmüyordu.
        //
        //     T48/T50/T53 buradan ÇIKTI: koordinatörün kararıyla
        //     `tickets-f3/index.md`'ye "Kapı ticket'ları" alt başlığı altında
        //     eklendiler ve `references/f3-detection-ve-rca-kaniti` vault
        //     sayfası okunup güncellendi, damgalandı (§11).
        //
        //     T49 da ÇIKTI (T62): `tickets-f2/index.md`'ye "F2 sonrası —
        //     dağıtım ve sağlık" alt başlığı altında T62 ile birlikte eklendi.
        //     Aynı kalıp, aynı gerekçe — tablo fazın ÜRÜNÜNÜ anlatıyor, bu iki
        //     satır o ürünün KALKIŞINI.
        ["yol-haritasi:tickets-f3/specificity-olcutu"] = "F3 tablosunda yok; kimliği de belirsiz.",
        ["yol-haritasi:tickets-f4/model-saglayicisi"] = "T42 — F4 tablosunda ticket satırı yok.",
        ["yol-haritasi:tickets-f4/senaryo-plugin-cekirdegi"] = "T43 — F4 tablosunda ticket satırı yok.",
        ["yol-haritasi:tickets/ham-arsiv-kurtarma"] = "F1 tablosunda yok.",


        // 2 · `tickets-fs` tablosunun Durum sütunu ile ticket dosyaları
        //     çelişiyor. `kalan-is-raporu` §6 bu dördü bir kez düzeltmişti;
        //     mekanizma olmadığı için yeniden ayrıştılar — raporun kendi
        //     öngörüsü gerçekleşti.
        ["durum:S02"] = "Tablo 🔄 diyor, ticket dosyası `status: 2`.",
        ["durum:S03"] = "Tablo 🔄 diyor, ticket dosyası `status: 2`.",
        ["durum:S04"] = "Tablo ⬜ diyor, ticket dosyası `status: 2`.",
        ["durum:S05"] = "Tablo ⬜ diyor, ticket dosyası `status: 2`.",

        // 3 · `kalan-is-raporu`'nun "açık" dediği kalemler.
        //
        // T38, T44 ve T47/T48 girişleri 2026-09-05'te SİLİNDİ, çünkü rapor o
        // gün bugünkü hâline getirildi ve üçü artık ayrışmıyor. Silinmeleri bu
        // kapının ikinci yarısının istediği şey: küçülmeyen bir ayrışma listesi
        // bir süre sonra hiçbir şey ifade etmiyor, ve birinci yarıyı dürüst
        // tutan da bu.
        //
        // Silinmeden önce T44'ün gerekçesi bir kez GÜNCELLENDİ ve o da kayda
        // değer: ayrışma sürüyordu ama sebebi değişmişti — eskisi "belge yok",
        // yenisi "belge var ve çelişiyor". Metin güncellenmeseydi liste doğru
        // kalemi tutup yanındaki cümle yalan söyleyecekti.
        //
        // T48'de aynı şey İKİNCİ kez görüldü ve mekanizması T44'ünkinden
        // farklıydı: yol haritası satırı eklenince kimlik çözülebilir hâle
        // geldi ve bulgu "eşlenemiyor"dan "rapor açık diyor, dosya `status: 2`"
        // hâline döndü. Yani BİRİNCİ ayrışma ikincisini maskeliyormuş —
        // eşleme boşluğu kapatılana kadar altındaki çelişki hiç görünmüyordu.
        // İki örnek bir desen: bir ayrışmanın kapanması, aynı satırın
        // kapandığı anlamına gelmiyor.
        //
        // ÜÇÜNCÜ örnek T49 ve maskenin ALTINDA çelişki YOKTU: yol haritası
        // satırı eklenince kimlik çözüldü, dosya `status: 1` çıktı ve raporun
        // "açık" demesi ÇELİŞMİYOR. Yani bu kez maske gerçek bir hizasızlığı
        // değil, sonradan doğru çıkan bir kaydı saklıyordu. Giriş bu yüzden
        // güncellenmedi, SİLİNDİ — ve iki hâlin ayrı ayrı görülmesi listeyi
        // dürüst tutan şey (T62 ölçtü, `Listeler_bayat_giris_tasimiyor` söyledi).
    };

    /// <summary>
    /// <b>Hiçbir zaman hizalanmayacak olanlar.</b> Gerekçesiyle, ve sayısı
    /// <see cref="ExpectedStructuralCount"/> ile çivili.
    ///
    /// <para>
    /// <c>CLAUDE.md</c> §8: <i>"bir gün kapanacak" ile "hiç kapanmayacak" aynı
    /// listede duramaz</i> — ikisi tek listedeyken
    /// <see cref="KnownDivergence"/>'ın boşalıp boşalmadığı sorulamaz hâle
    /// gelir. Bugün <b>boş</b>: <see cref="KnownDivergence"/>'daki satırların
    /// hangisinin kalıcı olduğu bir ölçüm değil bir karar, ve o karar
    /// koordinatörde.
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
    /// <b>Beşinci gösterim: main'e girmiş dallar.</b> <c>Merge branch
    /// '&lt;dal&gt;'</c> mesajlarından kimlik → dal adı eşlemesi.
    ///
    /// <para>
    /// <c>--first-parent</c> <b>bilerek</b> kullanılmıyor. Bir konu dalında
    /// koşarken main'in merge'leri ikinci ebeveyn tarafında kalıyor ve
    /// <c>--first-parent</c> onları eliyor — yani bekçinin kapsamı hangi dalda
    /// koştuğuna göre değişirdi. Bedeli, dalın kendi içindeki merge'lerin de
    /// sayılması; o da yalnızca <b>daha erken</b> uyarı üretiyor: kendi dalında
    /// merge ettiği işi <c>status: 0</c> bırakan ajanı main'e girmeden yakalıyor.
    /// </para>
    ///
    /// <para>
    /// Ön ek eşleştirmesi <b>tam</b>: <c>^([tsmb])(\d+)-</c>. Bulanık ad
    /// eşleştirmesi denenmedi ve denenmemesi bir karar — <c>t44-llm-adimlari</c>
    /// ile <c>llm-adimlari-ve-iki-kapi</c> dizini yalnızca ön ekle örtüşüyor, ve
    /// bulanık eşleştirme <b>yanlış ticket'ı</b> suçlayabilirdi. Kimlikten
    /// dizine giden yolu bulanıklaştırmak yerine yol haritası tablosuna
    /// bırakıyor: orası zaten kimlik → dosya eşlemesinin tek kaynağı.
    /// </para>
    /// </summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Merged = new(ReadMerged);

    private static IReadOnlyDictionary<string, string> ReadMerged()
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var subject in Git.Lines("log", "--merges", "--format=%s"))
        {
            var branch = Regex.Match(subject, @"^Merge branch '([^']+)'");

            if (!branch.Success)
            {
                continue;
            }

            var id = Regex.Match(branch.Groups[1].Value, @"^([tsmbTSMB])(\d+)-");

            if (id.Success)
            {
                merged.TryAdd(
                    id.Groups[1].Value.ToUpperInvariant() + id.Groups[2].Value,
                    branch.Groups[1].Value);
            }
        }

        return merged;
    }

    /// <summary>
    /// <b>Bir tablo satırında adı geçen her kimlik</b> — <c>docs/epic</c>
    /// altındaki <b>bütün</b> belgelerden, bağ verip vermediğine bakılmadan.
    ///
    /// <para>
    /// <see cref="Roadmap"/>'ten iki noktada ayrılıyor ve ikisi de bilinçli:
    /// yalnızca <c>tickets*</c> dizinlerine değil <b>her</b> belgeye bakıyor
    /// (<c>mcp-teknik-plan</c>, <c>kalan-is-raporu</c>,
    /// <c>kapasite-olcumu</c> hepsi kimlik anıyor), ve <b>bağsız</b> satırları
    /// da sayıyor. Sorduğu soru "kimliğin dosyası var mı" değil, çok daha
    /// düşük bir eşik: <b>bu kimlik depoda herhangi bir yerde kayıtlı mı.</b>
    /// </para>
    /// </summary>
    private static readonly Lazy<IReadOnlySet<string>> Declared = new(ReadDeclared);

    private static IReadOnlySet<string> ReadDeclared()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in Directory.EnumerateFiles(EpicRoot, "index.md", SearchOption.AllDirectories))
        {
            foreach (Match row in Regex.Matches(
                File.ReadAllText(path),
                @"^\|\s*\*{0,2}([TSMB]\d+)\*{0,2}\s*\|",
                RegexOptions.Multiline))
            {
                declared.Add(row.Groups[1].Value);
            }
        }

        return declared;
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
    /// <b>Main'e girmiş bir iş "başlamadı" görünmüyor.</b> T61'in çekirdeği ve
    /// bu sınıfın <b>gerçeğe</b> bağlanan tek kapısı.
    ///
    /// <para>
    /// <c>status: 0</c> <i>hiç başlanmadı</i> demek. Bir dalın main'e girmiş
    /// olması bunun <b>mekanik</b> olarak yanlış olduğunu söylüyor: insan
    /// kararı gerektiren hiçbir yeri yok, çünkü kod orada.
    /// </para>
    ///
    /// <para>
    /// <b><c>1</c> reddedilmiyor</b> ve bu kapının en önemli satırı: yarım bir
    /// kol da merge edilebiliyor (bugün M03 ve T32 tam olarak bu hâlde ve
    /// ikisi de <b>doğru</b>). <c>1 → 2</c> geçişi bir insan kararı; onu
    /// mekanikleştirmek, bekçinin ölçemediği bir şeyi ölçüyormuş gibi
    /// göstermek olurdu.
    /// </para>
    /// </summary>
    [Fact]
    public void Birlestirilmis_bir_is_baslamamis_gorunmuyor()
    {
        var byId = Roadmap.Value
            .GroupBy(static row => row.Id, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First().Key, StringComparer.Ordinal);

        var actual = Tickets().ToDictionary(static t => t.Key, static t => t.Status, StringComparer.Ordinal);

        var contradictions = Merged.Value
            .Where(pair => !Excused($"birlesme:{pair.Key}"))
            .Where(pair => byId.TryGetValue(pair.Key, out var key)
                && actual.TryGetValue(key, out var status)
                && status == NotStarted)
            .Select(pair => $"{pair.Key}: `{pair.Value}` main'de, ticket dosyası `status: 0` ({byId[pair.Key]})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            contradictions.Length == 0,
            "Bu ticket'ların dalı merge edilmiş ama dosyaları hâlâ \"başlanmadı\" diyor:\n  " +
            string.Join("\n  ", contradictions) +
            "\n\nBelge \"yapılacak\", kod \"yapıldı\" diyor — ve bir sonraki brief belgeyi " +
            "okuyor. İş bitmediyse `status: 1`, bittiyse `status: 2`; `0` bu noktadan " +
            "sonra hiçbir hâli ifade etmiyor.");
    }

    /// <summary>
    /// <b>Main'e girmiş her kimlik bir tabloda anılıyor.</b> Kayıtsız iş
    /// aramıyor — <b>hiç</b> kayıt aramıyor.
    ///
    /// <para>
    /// Eşik bilerek çok düşük: ticket dosyası değil, bağ değil, statü değil —
    /// yalnızca <b>bir tablo satırı</b>. Ticket dosyası istemek yazılmamış işi
    /// zorunlu kılardı (T53 aynı gerekçeyle "bütün çocukları bitmiş bir story
    /// bitmiş olmak zorunda değil" demişti); bir satır istemek ise yalnızca
    /// <i>bu iş oldu</i> demenin bedelini soruyor.
    /// </para>
    ///
    /// <para>
    /// <b><c>kapasite-olcumu</c>'nun B01–B05'i bu kapıya takılmıyor ve bu bir
    /// karar.</b> Beşinin de tablo satırı var, ticket dosyası yok, ve hiçbiri
    /// merge edilmedi — yani <i>plan yazıldı, iş başlamadı</i>. "Belgede adı
    /// geçen ama dosyası olmayan ticket" hâli bu bekçinin
    /// <b>kapsamında değil</b>: bir planın dilimleme önerisini ticket dosyası
    /// yazma zorunluluğuna çevirirdi, ve <c>kapasite-olcumu</c> §6'nın üç açık
    /// sorusu cevaplanmadan o dosyalar zaten yazılamaz. Kapı ilk <c>b01-*</c>
    /// dalı merge edildiği gün konuşmaya başlıyor — yani kapsamı, bağlandığı
    /// olguyla birlikte kendiliğinden büyüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Birlestirilmis_her_kimlik_bir_tabloda_aniliyor()
    {
        var unrecorded = Merged.Value.Keys
            .Where(id => !Declared.Value.Contains(id))
            .Where(id => !Excused($"kayit:{id}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unrecorded.Length == 0,
            "Bu dallar main'e girdi ama kimlikleri `docs/epic` altındaki hiçbir tabloda " +
            "geçmiyor:\n  " +
            string.Join("\n  ", unrecorded.Select(id => $"{id} (`{Merged.Value[id]}`)")) +
            "\n\nİş oldu ve hiçbir yerde kayıtlı değil: ne yol haritası sayıyor, ne rapor " +
            "biliyor, ne de bir sonraki tur ondan haberdar.");
    }

    /// <summary>
    /// <b>İşi başlamış bir story "başlamadı" görünmüyor.</b>
    ///
    /// <para>
    /// <see cref="Tamamlanmis_bir_story_yarim_ticket_tasimiyor"/>'un aynası ve
    /// T53'ün bilerek sınamadığı yön <b>değil</b>: o, "bütün çocuklar bitti ⇒
    /// story bitti" çıkarımını reddediyor (yazılmamış ticket'lar olabilir).
    /// Buradaki çıkarım tek yönlü ve yazılmamış işe hiç dokunmuyor: bir
    /// çocuğu <b>bitmişse</b> story'nin <c>0</c> olması, mevcut bir olgunun
    /// inkârı.
    /// </para>
    ///
    /// <para>
    /// <c>2</c> talep edilmiyor, yalnızca <c>0</c> reddediliyor — aynı
    /// gerekçeyle: <c>1 → 2</c> insan kararı.
    /// </para>
    /// </summary>
    [Fact]
    public void Isi_baslamis_bir_story_baslamamis_gorunmuyor()
    {
        var broken = new List<string>();

        foreach (var story in Stories().Where(static s => s.Status == NotStarted))
        {
            var prefix = story.Key + "/";

            var done = Tickets()
                .Where(ticket => ticket.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(static ticket => ticket.Status == Done)
                .Select(static ticket => ticket.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (done.Length > 0 && !Excused($"story:{story.Key}"))
            {
                broken.Add($"{story.Key} (status 0) → bitmiş çocuk: {string.Join(", ", done)}");
            }
        }

        Assert.True(
            broken.Count == 0,
            "Story \"başlanmadı\" diyor ama altında bitmiş ticket var:\n  " +
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
    ///
    /// <para>
    /// <b>Git tarafı ayrıca sorulmak zorunda</b> ve arıza biçimi tamamen
    /// farklı: yüzeysel bir klon, ihracat edilmiş bir ağaç ya da merge mesajı
    /// biçiminin değişmesi kümeyi boşaltıyor, ve T61'in üç kapısı birden
    /// sessizleşiyor. <see cref="Git.Lines"/> boş çıktıyı zaten reddediyor,
    /// ama ayrıştırılabilir <b>kimlik</b> sayısının sıfırdan büyük olması ayrı
    /// bir soru — merge mesajları var ve hiçbiri kimlik taşımıyor olabilir.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekci_bos_kume_uzerinde_donmuyor()
    {
        Assert.NotEmpty(Tickets());
        Assert.NotEmpty(Stories());
        Assert.NotEmpty(Roadmap.Value);
        Assert.NotEmpty(ReportedOpen());
        Assert.NotEmpty(Declared.Value);

        Assert.True(
            Merged.Value.Count > 0,
            "`git log --merges` içinde ayrıştırılabilir tek bir ticket kimliği bulunamadı — " +
            "merge mesajı biçimi (`Merge branch '<dal>'`) değişmiş ya da geçmiş kesilmiş " +
            "olabilir. T61'in üç kapısı bu kümenin üstünde duruyor.");

        var byId = Roadmap.Value.Select(static row => row.Id).ToHashSet(StringComparer.Ordinal);

        Assert.True(
            Merged.Value.Keys.Any(byId.Contains),
            "Merge edilmiş kimliklerin hiçbiri bir yol haritası satırına bağlanamadı — " +
            "kimlik → ticket dosyası eşlemesi kopmuş ve " +
            $"`{nameof(Birlestirilmis_bir_is_baslamamis_gorunmuyor)}` boş küme üzerinde dönüyor.");

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

        foreach (var (id, _) in Merged.Value)
        {
            if (byId.TryGetValue(id, out var key)
                && actual.TryGetValue(key, out var status)
                && status == NotStarted)
            {
                keys.Add($"birlesme:{id}");
            }

            if (!Declared.Value.Contains(id))
            {
                keys.Add($"kayit:{id}");
            }
        }

        foreach (var story in Stories().Where(static s => s.Status == NotStarted))
        {
            var prefix = story.Key + "/";

            if (Tickets().Any(ticket =>
                    ticket.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && ticket.Status == Done))
            {
                keys.Add($"story:{story.Key}");
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
