using System.Diagnostics;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Kabul kriteri: <b>ReDoS corpus'u ile timeout doğrulaması; hiçbir girdi süreci
/// kilitlemiyor.</b>
///
/// <para>
/// Buradaki asıl iddia "linter kötü pattern'i yakalar" değil — o ikinci savunma.
/// Birincisi şu: kötü pattern <i>yakalanmasa bile</i> ingest durmuyor.
/// </para>
/// </summary>
public sealed class RedosTests
{
    private static readonly GrokCompiler Bare = new(GrokPatternLibrary.Empty);

    /// <summary>Klasik katastrofik geri izleme corpus'u.</summary>
    public static TheoryData<string, string> Corpus => new()
    {
        { "^(a+)+$", new string('a', 40) + "!" },
        { "^(a|a)+$", new string('a', 40) + "!" },
        { "^(a*)*$", new string('a', 40) + "!" },
        { @"^(\s*)+$", new string(' ', 40) + "!" },
        { "^(x+x+)+y$", new string('x', 40) + "z" },
        { @"^([a-zA-Z]+)*$", new string('a', 40) + "1" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Redos_corpusu_sureci_kilitlemiyor(string pattern, string input)
    {
        var grok = Bare.Compile(pattern);
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);

        var stopwatch = Stopwatch.StartNew();
        var result = grok.Match(input, bag);
        stopwatch.Stop();

        // İki kabul edilebilir sonuç var: doğrusal motorda eşleşmeme, geri izlemeli
        // motorda zaman aşımı. Kabul edilemeyen tek şey beklemek.
        Assert.True(
            result.Outcome is GrokMatchOutcome.NoMatch or GrokMatchOutcome.TimedOut,
            $"Beklenmeyen sonuç: {result.Outcome}");

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"'{pattern}' girdide {stopwatch.ElapsedMilliseconds} ms harcadı — koruma çalışmıyor.");
    }

    [Fact]
    public void Geri_izlemesiz_derlenen_pattern_dogrusal_isaretlenir()
    {
        Assert.True(Bare.Compile("^(a+)+$").IsLinearTime);
    }

    [Fact]
    public void Zaman_asimi_asilirsa_TimedOut_doner()
    {
        // Geriye bakış doğrusal motoru kapatır; corpus deseni artık gerçekten geri izler.
        var compiler = new GrokCompiler(
            GrokPatternLibrary.Empty,
            new GrokCompilerOptions { MatchTimeout = TimeSpan.FromMilliseconds(50) });

        var grok = compiler.Compile(@"(?<!x)^(a+)+$");
        Assert.False(grok.IsLinearTime);

        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var result = grok.Match(new string('a', 60) + "!", bag);
        stopwatch.Stop();

        Assert.Equal(GrokMatchOutcome.TimedOut, result.Outcome);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Linter_ic_ice_niceleyiciyi_yakalar()
    {
        var findings = RedosLinter.Inspect(Bare.Compile(@"(?<!x)^(a+)+$"));
        var nested = Assert.Single(findings, f => f.Code == "GROK001");

        Assert.Equal(RedosSeverity.Error, nested.Severity);
    }

    [Fact]
    public void Dogrusal_motorda_ayni_bulgu_bilgi_duzeyine_iner()
    {
        // Bu ayrım linter'ın işe yaramasının şartı: doğrusal motorda felç
        // imkânsızken hata vermek, gerçek bulguları gürültüde boğar.
        var findings = RedosLinter.Inspect(Bare.Compile("^(a+)+$"));
        var nested = Assert.Single(findings, f => f.Code == "GROK001");

        Assert.Equal(RedosSeverity.Info, nested.Severity);
    }

    [Fact]
    public void Atomik_grup_ic_ice_niceleyici_sayilmaz()
    {
        var findings = RedosLinter.Inspect(Bare.Compile("^(?>a+)+$"));
        Assert.DoesNotContain(findings, f => f.Code == "GROK001");
    }

    [Fact]
    public void Geri_izlemeli_motora_dusus_bildirilir()
    {
        var findings = RedosLinter.Inspect(Bare.Compile(@"(?<!x)abc"));
        Assert.Contains(findings, f => f.Code == "GROK003");
    }

    [Fact]
    public void Yan_yana_jokerler_uyari_uretir()
    {
        var findings = RedosLinter.Inspect(Bare.Compile(@"(?<!x)^.*.*$"));
        Assert.Contains(findings, f => f.Code == "GROK002");
    }

    [Fact]
    public void Temiz_pattern_bulgu_uretmez()
    {
        Assert.Empty(RedosLinter.Inspect(Bare.Compile(@"^\d{1,3}\.\d{1,3}$")));
    }
}
