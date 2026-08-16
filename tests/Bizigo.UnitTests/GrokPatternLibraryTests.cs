using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Kabul kriteri: <b>Logstash pattern setinin tamamı hatasız yükleniyor.</b>
///
/// Bu test upstream yükseltmesinin bekçisi. <c>catalog/patterns/</c> güncellenince
/// bir pattern bizim derleyicimizde patlarsa burada görülür — üretimde ilk o
/// pattern'i kullanan parser'da değil.
/// </summary>
public sealed class GrokPatternLibraryTests
{
    public static TheoryData<string> PatternSets =>
    [
        RepositoryLayout.LegacyPatternDirectory,
        RepositoryLayout.EcsPatternDirectory,
    ];

    [Theory]
    [MemberData(nameof(PatternSets))]
    public void Setin_tamami_derleniyor(string directory)
    {
        var library = GrokPatternLibrary.LoadFromDirectory(directory);
        Assert.True(library.Count > 300, $"{directory}: yalnızca {library.Count} pattern yüklendi — set eksik görünüyor.");

        var compiler = new GrokCompiler(library);
        var failures = new List<string>();

        foreach (var name in library.Names.Order(StringComparer.Ordinal))
        {
            try
            {
                compiler.Compile("%{" + name + "}");
            }
#pragma warning disable CA1031 // Test raporu için tüm hataları topluyoruz.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failures.Add($"{name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} pattern derlenemedi:{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Take(25)));
    }

    [Fact]
    public void Iki_set_ayni_anda_yuklenemez_cakisma_hata_verir()
    {
        // legacy ve ecs-v1 aynı adları farklı gövdelerle tanımlar. Sessizce
        // üzerine yazmak, hangi pattern'in koştuğunu takip edilemez yapardı.
        var ex = Assert.Throws<GrokCompilationException>(
            () => GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.PatternDirectory));

        Assert.Contains("iki farklı gövdeyle tanımlı", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gercek_syslog_satiri_ayristirilir()
    {
        var library = GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);
        var compiler = new GrokCompiler(library);
        var grok = compiler.Compile("%{SYSLOGLINE}");

        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        var line = "<38>Aug 16 10:00:00 fw01 sshd[1234]: Accepted password for admin from 10.0.0.5 port 51514 ssh2";

        Assert.True(grok.Match(line, bag).Matched);
        Assert.Equal("fw01", bag["logsource"]);
        Assert.Equal("sshd", bag["program"]);
    }

    [Fact]
    public void Turkce_govde_bozulmadan_yakalanir()
    {
        var library = GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);
        var compiler = new GrokCompiler(library);
        var grok = compiler.Compile("^%{WORD:tag}: %{GREEDYDATA:body}$");

        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.True(grok.Match("uyari: INTERFACE arayüzü kapandı — bağlantı düştü", bag).Matched);
        Assert.Equal("INTERFACE arayüzü kapandı — bağlantı düştü", bag["body"]);
    }
}
