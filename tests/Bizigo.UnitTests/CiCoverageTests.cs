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
    /// Depodaki test kökleri — <b>işaret dosyalarından</b>, elle listeden değil.
    ///
    /// <para>
    /// Üç işaret: pytest yapılandırması, vitest yapılandırması, ve test SDK'sı
    /// referanslayan bir proje dosyası. Yeni bir dil eklenirse bekçi onu
    /// görmez — kapsamının sınırı bu ve <c>CLAUDE.md</c>'ye yazılan tek şey de
    /// o: <i>yeni bir test paketi eklerken tanınan bir işaret bırak ya da
    /// bekçiyi genişlet.</i>
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> TestRoots()
    {
        var root = RepositoryLayout.Root;
        var roots = new SortedSet<string>(StringComparer.Ordinal);

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

            var isMarker = name is "pytest.ini" or "conftest.py"
                || name.StartsWith("vitest.config.", StringComparison.Ordinal)
                || (name.EndsWith(".csproj", StringComparison.Ordinal)
                    && File.ReadAllText(marker).Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal));

            if (!isMarker)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(relative);

            if (!string.IsNullOrEmpty(directory))
            {
                roots.Add(directory);
            }
        }

        // İç içe kökleri tekilleştir: `sidecar` ve `sidecar/tests` aynı paket,
        // ve CI'ın hedeflemesi gereken dıştaki. İkisini birden raporlamak aynı
        // eksiği iki kalem gibi gösterirdi.
        return [.. roots.Where(candidate =>
            !roots.Any(other => other != candidate && IsUnder(candidate, other)))];
    }

    [Fact]
    public void Depodaki_her_test_paketi_ci_da_kosuyor()
    {
        var workflow = Workflow();
        var roots = TestRoots();

        Assert.NotEmpty(roots);

        var steps = Steps(workflow);

        var missing = roots
            .Where(candidate => !Covered(steps, candidate))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Bu test kökleri CI'da hiç koşmuyor:\n  " + string.Join("\n  ", missing) +
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
            Covered(Steps(Workflow()), "tests/Bizigo.UnitTests"),
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
                run.Success ? run.Groups["cmd"].Value : block));
        }

        return steps;
    }

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
    private static bool Covered(IReadOnlyList<Step> steps, string root)
    {
        var runners = RunnersFor(root);

        foreach (var step in steps)
        {
            if (!runners.Any(runner => step.Run.Contains(runner, StringComparison.Ordinal)))
            {
                continue;
            }

            if (step.Run.Contains(root, StringComparison.Ordinal))
            {
                return true;
            }

            if (step.WorkingDirectory.Length > 0 && IsUnder(root, step.WorkingDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] RunnersFor(string root)
    {
        var full = Path.Combine(RepositoryLayout.Root, root);

        if (File.Exists(Path.Combine(full, "pytest.ini")) || File.Exists(Path.Combine(full, "conftest.py")))
        {
            return ["pytest"];
        }

        if (Directory.EnumerateFiles(full, "vitest.config.*").Any())
        {
            return ["npm test", "vitest"];
        }

        return ["dotnet test"];
    }

    /// <summary><paramref name="root"/>, <paramref name="ancestor"/>'ın altında mı.</summary>
    private static bool IsUnder(string root, string ancestor) =>
        root.Equals(ancestor, StringComparison.Ordinal)
        || root.StartsWith(ancestor + "/", StringComparison.Ordinal);
}
