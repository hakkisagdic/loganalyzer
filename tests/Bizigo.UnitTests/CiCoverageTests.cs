using System.Text.RegularExpressions;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>Depodaki her test paketi CI'da gerçekten koşuyor mu.</b>
///
/// <para>
/// Bulunuş biçimi: <c>sidecar/tests/</c> altında dört pytest dosyası,
/// <c>requirements-dev.txt</c>'de sabitlenmiş pytest, yapılandırılmış
/// <c>pytest.ini</c> — ve <c>ci.yml</c>'da onları koşturan <b>tek bir adım
/// yok</b>. Testler yazılmıştı, yazan kişi doğru şeyi yapmıştı; eksik olan bir
/// disiplin değil bir <b>bağ</b>ıdı. Ve bağın yokluğu hiçbir yerde
/// görünmüyordu.
/// </para>
///
/// <para>
/// <b>Neden bekçi, neden <c>CLAUDE.md</c>'ye kural değil:</b> kural, kuralı
/// hatırlayan bir insana bağlanmak demek ve bu depoda ölçüldü — bayat
/// <c>node_modules</c> kuralı yazılıydı, üç kişi aynı duvara tosladı. Elle
/// beslenen liste beş kez kör çıktı. Hatırlamaya dayanan mekanizma bu fazda
/// kaybetti.
/// </para>
///
/// <para>
/// <b>Şekli fazın kuralı:</b> denetlenen kümeyi <b>keşfet</b>, elle kalan tek
/// şey beklenen küme olsun. Test kökleri işaret dosyalarından bulunuyor; elle
/// tutulan bir liste yok, yani yarın eklenen bir paket kendiliğinden
/// denetleniyor.
/// </para>
/// </summary>
public sealed partial class CiCoverageTests
{
    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root, ".github", "workflows", "ci.yml"));

    /// <summary>
    /// <c>continue-on-error: true</c> — koşuyor, düşüyor, CI yeşil.
    /// </summary>
    [GeneratedRegex(@"continue-on-error:\s*true", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueOnError();

    /// <summary>
    /// Çıkış kodunu yutan kabuk kalıpları. <c>|| true</c> ve <c>|| :</c> en
    /// yaygın ikisi; ikisi de adımı daima başarılı yapıyor.
    /// </summary>
    [GeneratedRegex(@"\|\|\s*(true|:)\s*$", RegexOptions.Multiline)]
    private static partial Regex SwallowedExitCode();

    /// <summary>
    /// Bir test paketi: <b>hangi dizin</b> ve <b>hangi koşucu</b>.
    /// </summary>
    /// <param name="Root">İşaret dosyasının bulunduğu dizin, depo köküne göre.</param>
    /// <param name="Family">İşaretin belirlediği koşucu ailesi.</param>
    private readonly record struct Suite(string Root, string Family);

    /// <summary>
    /// Depodaki test paketleri — <b>işaret dosyalarından</b>, elle listeden değil.
    ///
    /// <para>
    /// Dört işaret: pytest yapılandırması, vitest yapılandırması, playwright
    /// yapılandırması, ve test SDK'sı referanslayan bir proje dosyası. Yeni bir
    /// dil eklenirse bekçi onu görmez — kapsamının sınırı bu ve
    /// <c>CLAUDE.md</c>'ye yazılan tek şey de o: <i>yeni bir test paketi
    /// eklerken tanınan bir işaret bırak ya da bekçiyi genişlet.</i>
    /// </para>
    ///
    /// <para>
    /// <b>Birim neden dizin değil (kök, aile) çifti:</b> ilk hâli dizin
    /// sayıyordu ve <c>ui</c> altına ikinci bir paket girdiğinde <b>kör
    /// kaldı</b>. <c>ui/vitest.config.ts</c> o dizini "kapsanmış" yapıyor,
    /// <c>npm test</c> adımı bekçiyi tatmin ediyordu; yanı başındaki
    /// <c>ui/playwright.config.ts</c> ise CI'da hiç koşmuyordu ve bekçi bunu
    /// söyleyemiyordu. Aynı dizinde iki paket olabiliyor, ve birinin koşuyor
    /// olması diğeri hakkında hiçbir şey söylemiyor.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Suite> TestSuites()
    {
        var root = RepositoryLayout.Root;
        var suites = new SortedSet<Suite>(SuiteOrder.Instance);

        foreach (var marker in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, marker).Replace('\\', '/');

            // Üretilmiş ve indirilmiş ağaçlar denetlenmiyor: orada bulunan bir
            // yapılandırma bizim testimiz değil, bir bağımlılığınki.
            if (relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("node_modules/", StringComparison.Ordinal)
                || relative.Contains("/.venv/", StringComparison.Ordinal)
                || relative.Contains("/.next/", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(relative);

            var family = name switch
            {
                "pytest.ini" or "conftest.py" => Families.Pytest,
                _ when name.StartsWith("vitest.config.", StringComparison.Ordinal) => Families.Vitest,
                _ when name.StartsWith("playwright.config.", StringComparison.Ordinal) => Families.Playwright,
                _ when name.EndsWith(".csproj", StringComparison.Ordinal)
                    && File.ReadAllText(marker).Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal)
                    => Families.Dotnet,
                _ => null,
            };

            if (family is null)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(relative);

            if (!string.IsNullOrEmpty(directory))
            {
                suites.Add(new Suite(directory, family));
            }
        }

        // İç içe kökleri tekilleştir: `sidecar` ve `sidecar/tests` aynı paket,
        // ve CI'ın hedeflemesi gereken dıştaki. İkisini birden raporlamak aynı
        // eksiği iki kalem gibi gösterirdi.
        //
        // Tekilleştirme AYNI AİLE içinde: `ui/vitest.config.ts` ile
        // `ui/playwright.config.ts` aynı dizinde ama ayrı paketler ve ayrı
        // adımlar istiyorlar. Aileyi göz ardı eden bir tekilleştirme ikisini
        // tek kaleme indirir ve bekçiyi tam da eklendiği kusurda kör bırakır.
        return [.. suites.Where(candidate => !suites.Any(other =>
            other.Family == candidate.Family
            && other.Root != candidate.Root
            && IsUnder(candidate.Root, other.Root)))];
    }

    /// <summary>Koşucu aileleri — işaret dosyasının belirlediği.</summary>
    private static class Families
    {
        public const string Pytest = "pytest";
        public const string Vitest = "vitest";
        public const string Playwright = "playwright";
        public const string Dotnet = "dotnet";
    }

    /// <summary>Raporun sırası koşumdan koşuma değişmesin diye.</summary>
    private sealed class SuiteOrder : IComparer<Suite>
    {
        public static readonly SuiteOrder Instance = new();

        public int Compare(Suite x, Suite y)
        {
            var byRoot = string.CompareOrdinal(x.Root, y.Root);

            return byRoot != 0 ? byRoot : string.CompareOrdinal(x.Family, y.Family);
        }
    }

    [Fact]
    public void Depodaki_her_test_paketi_ci_da_kosuyor()
    {
        var workflow = Workflow();
        var suites = TestSuites();

        Assert.NotEmpty(suites);

        var steps = Steps(workflow);

        var missing = suites
            .Where(candidate => !Covered(steps, candidate))
            .Select(candidate => $"{candidate.Root}  ({candidate.Family})")
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Bu test paketleri CI'da hiç koşmuyor:\n  " + string.Join("\n  ", missing) +
            "\n\nTest yazılmış ama koşuma girmiyorsa, girmediği hiçbir yerde görünmez. " +
            "`ci.yml`'a bir adım ekleyin.");
    }

    /// <summary>
    /// <b>Bekçi kendi kökünü de kapsıyor.</b>
    ///
    /// <para>
    /// Ayrı bir test çünkü kaybı ayrı bir hata: bu bekçi <c>Bizigo.UnitTests</c>
    /// içinde yaşıyor ve biri o işi <c>ci.yml</c>'dan kaldırırsa bekçi de
    /// kaybolur — geriye kimsenin koşmadığı bir kontrol kalır. Bu turda tam
    /// olarak böyle oldu: <c>api:check</c> doğrulama listesinden düştü ve üç
    /// birleştirme boyunca ölü kaldı, kimse fark etmedi.
    /// </para>
    /// </summary>
    [Fact]
    public void Bekcinin_kendi_paketi_de_ci_da_kosuyor()
    {
        Assert.True(
            Covered(Steps(Workflow()), new Suite("tests/Bizigo.UnitTests", Families.Dotnet)),
            "Bu bekçinin yaşadığı paket CI'da koşmuyor — yani bekçi de koşmuyor.");
    }

    /// <summary>
    /// Koşan ama <b>sonucu okunmayan</b> adım. Bekçinin ilk testi "hiç
    /// koşmuyor"u yakalıyor; bir sonraki kusur şekli bu.
    ///
    /// <para>
    /// <c>continue-on-error: true</c> ve çıkış kodunu yutan kabuk satırı, ikisi
    /// de "test var, düşüyor, CI yeşil" üretiyor — testin hiç olmamasıyla aynı
    /// sonuç, üstelik test varmış gibi görünerek.
    /// </para>
    /// </summary>
    [Fact]
    public void Hicbir_adim_hatayi_yutmuyor()
    {
        var workflow = Workflow();

        Assert.False(
            ContinueOnError().IsMatch(workflow),
            "`continue-on-error: true` bir adımı daima başarılı yapıyor: test koşuyor, " +
            "düşüyor, CI yeşil kalıyor. Testin hiç olmamasıyla aynı sonuç.");

        Assert.False(
            SwallowedExitCode().IsMatch(workflow),
            "Bir adım çıkış kodunu yutuyor (`|| true` / `|| :`). Başarısızlık " +
            "görünmez oluyor.");
    }

    /// <param name="WorkingDirectory">Adımın çalışma dizini; yoksa boş.</param>
    /// <param name="Run">Adımın koşturduğu komutlar.</param>
    private readonly record struct Step(string WorkingDirectory, string Run);

    /// <summary>
    /// İş akışını <b>adımlara</b> böler.
    ///
    /// <para>
    /// İlk hâli yalnızca "kök adı <c>ci.yml</c>'da geçiyor mu" diye soruyordu ve
    /// <b>yanlış geçti</b>: <c>sidecar/tests</c> için üst dizine çıkınca
    /// <c>sidecar</c> kelimesi imaj derleyen başka bir adımda bulunuyordu, yani
    /// bekçi tam da yakalaması gereken kusurda yeşil yandı. Yanlış geçen bir
    /// bekçi, olmayan bir bekçiden kötü — varlığı güven veriyor.
    /// </para>
    ///
    /// <para>
    /// Şimdi soru "geçiyor mu" değil, <b>"onu koşturan bir adım var mı"</b>:
    /// çalışma dizini ve komut aynı adımda eşleşmek zorunda.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Step> Steps(string workflow)
    {
        var steps = new List<Step>();

        foreach (var block in Regex.Split(workflow, @"^\s*-\s+name:", RegexOptions.Multiline).Skip(1))
        {
            var directory = Regex.Match(block, @"working-directory:\s*(?<dir>\S+)");
            var run = Regex.Match(block, @"run:\s*(?<cmd>[\s\S]*?)(?=\n\s*(?:-\s+name:|[a-z-]+:\s|$))");

            steps.Add(new Step(
                directory.Success ? directory.Groups["dir"].Value.Trim().TrimEnd('/') : string.Empty,
                WithoutComments(run.Success ? run.Groups["cmd"].Value : block)));
        }

        return steps;
    }

    /// <summary>
    /// Yorum satırları çıkarılmış komut metni.
    ///
    /// <para>
    /// <c>run: |</c> bloğundaki <c>#</c> satırı bir <b>kabuk yorumu</b>; koşan
    /// bir komut değil. Bekçi onları da sayarken yanlış sebeple yeşil
    /// yanabiliyordu: bu depoda <c>e2e</c> işinin hemen üstündeki açıklama
    /// yorumu <c>npm run e2e</c> dizgesini birebir taşıyor, yani adım silinse
    /// bile koşucu jetonu komşu adımın metninde bulunabiliyordu.
    /// </para>
    ///
    /// <para>
    /// Bu, bekçinin yakalamak için var olduğu kusurun ta kendisi — bir kapının
    /// yanlış sebeple geçmesi, hiç olmamasından kötü (§7). Yorumları çıkarmak
    /// kuralı genelleştiriyor: yorum içine yazılmış hiçbir komut adı bir adımı
    /// "koşuyor" saydırmıyor.
    /// </para>
    /// </summary>
    private static string WithoutComments(string run) =>
        string.Join(
            "\n",
            run.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>
    /// Kökü <b>koşturan</b> bir adım var mı.
    ///
    /// <para>
    /// İki yol: komut kökü doğrudan anıyor (<c>dotnet test tests/X</c>), ya da
    /// adımın çalışma dizini kökü kapsıyor ve komut o kökün koşucusunu
    /// çağırıyor (<c>working-directory: ui</c> + <c>npm test</c>).
    /// </para>
    ///
    /// <para>
    /// Koşucu <b>işaret dosyasından</b> türüyor: pytest kökü için pytest,
    /// vitest kökü için <c>npm test</c>/<c>vitest</c>, proje dosyası için
    /// <c>dotnet test</c>. Aksi hâlde bir imaj derleme adımı, altındaki test
    /// paketini koşturuyormuş gibi sayılırdı.
    /// </para>
    /// </summary>
    private static bool Covered(IReadOnlyList<Step> steps, Suite suite)
    {
        var runners = RunnersFor(suite.Family);

        foreach (var step in steps)
        {
            if (!runners.Any(runner => step.Run.Contains(runner, StringComparison.Ordinal)))
            {
                continue;
            }

            if (step.Run.Contains(suite.Root, StringComparison.Ordinal))
            {
                return true;
            }

            if (step.WorkingDirectory.Length > 0 && IsUnder(suite.Root, step.WorkingDirectory))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Aileyi koşturan komutlar.
    ///
    /// <para>
    /// Koşucu <b>aileden</b> türüyor, dizini yeniden yoklayarak değil: aynı
    /// dizinde iki aile birden olabiliyor (<c>ui</c>) ve dizine bakan bir
    /// çözüm ikisine de ilk bulduğu koşucuyu verirdi.
    /// </para>
    /// </summary>
    private static string[] RunnersFor(string family) => family switch
    {
        Families.Pytest => ["pytest"],
        Families.Vitest => ["npm test", "vitest"],
        // `npm run e2e` hazırlığı da çalıştırıyor; çıplak `playwright test`
        // hazırlığın başka bir adımda yapıldığı kurulumlar için.
        Families.Playwright => ["npm run e2e", "playwright test"],
        _ => ["dotnet test"],
    };

    /// <summary><paramref name="root"/>, <paramref name="ancestor"/>'ın altında mı.</summary>
    private static bool IsUnder(string root, string ancestor) =>
        root.Equals(ancestor, StringComparison.Ordinal)
        || root.StartsWith(ancestor + "/", StringComparison.Ordinal);
}
