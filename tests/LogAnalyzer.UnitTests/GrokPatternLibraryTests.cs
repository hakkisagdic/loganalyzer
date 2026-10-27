using LogAnalyzer.Parsing.Grok;

namespace LogAnalyzer.UnitTests;

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

    /// <summary>
    /// Ayrıştırma doğruluğu <b>üretimin kütüphanesiyle</b> sınanıyor
    /// (<see cref="RepositoryLayout.DefaultLibrary"/>: <c>legacy</c> +
    /// <c>loganalyzer-v1</c> kaplaması).
    ///
    /// <para>
    /// <c>legacy</c> tek başına yüklenince <c>SYSLOGLINE</c> geri izlemeli
    /// motorda derleniyor (<c>IPV4</c>'ün <c>(?&lt;![0-9])</c>/<c>(?![0-9])</c>
    /// lookaround'ları; ölçüldü — bu iddia o kurulumda kırmızı yanıyor) ve oradaki 50 ms
    /// <c>MatchTimeout</c> duvar saatini ölçüyor: yüklü makinede sağlıklı satır
    /// zaman aşımına uğrayıp test kararsız kırmızı yanıyordu. Doğrusal motor
    /// iddiası bunun yerine <b>deterministik</b> kırmızı: kaplama bir gün bu
    /// pattern'i doğrusal tutamazsa sebep burada adıyla görünüyor. 50 ms'lik
    /// üretim zaman aşımının bekçisi <c>RedosTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Gercek_syslog_satiri_ayristirilir()
    {
        var compiler = new GrokCompiler(RepositoryLayout.DefaultLibrary);
        var grok = compiler.Compile("%{SYSLOGLINE}");

        Assert.True(grok.IsLinearTime, grok.FallbackReason);

        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        var line = "<38>Aug 16 10:00:00 fw01 sshd[1234]: Accepted password for admin from 10.0.0.5 port 51514 ssh2";

        Assert.True(grok.Match(line, bag).Matched);
        Assert.Equal("fw01", bag["logsource"]);
        Assert.Equal("sshd", bag["program"]);
    }

    /// <summary>
    /// Üretimin kütüphanesiyle ve doğrusal motor iddiasıyla.
    ///
    /// <para>
    /// Bu ifade <c>legacy</c> tek başına yüklendiğinde de doğrusal derleniyor
    /// (ölçüldü: <c>WORD</c> ile <c>GREEDYDATA</c> lookaround taşımıyor), yani
    /// kararsızlığın kaynağı bu test değildi — o
    /// <see cref="Gercek_syslog_satiri_ayristirilir"/>. Yine de üretimin
    /// kütüphanesine bağlandı, çünkü ayrıştırma testleri sevk edilen
    /// yapılandırmayı sınamalı. İddia da aynı işi görüyor: bu pattern'lerden
    /// biri bir gün lookaround kazanırsa geri izlemeli motora dönüş, 50 ms'lik
    /// bir zaman aşımı kumarı olarak değil adıyla görünüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Turkce_govde_bozulmadan_yakalanir()
    {
        var compiler = new GrokCompiler(RepositoryLayout.DefaultLibrary);
        var grok = compiler.Compile("^%{WORD:tag}: %{GREEDYDATA:body}$");

        Assert.True(grok.IsLinearTime, grok.FallbackReason);

        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        Assert.True(grok.Match("uyari: INTERFACE arayüzü kapandı — bağlantı düştü", bag).Matched);
        Assert.Equal("INTERFACE arayüzü kapandı — bağlantı düştü", bag["body"]);
    }
}
