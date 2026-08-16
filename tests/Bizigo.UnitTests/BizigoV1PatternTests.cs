using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// <c>bizigo-v1</c> lookaround'suz kaplamasının bekçisi (T08 raporu #8b).
///
/// <para>
/// Buradaki asıl soru "doğrusal motorla derleniyor mu" değil — o kolay kısım.
/// Asıl soru <b>sınır davranışının korunup korunmadığı</b>: <c>IPV4</c>'ün
/// <c>(?&lt;![0-9])</c>/<c>(?![0-9])</c> sınırları <c>1.2.3.45</c> içinde
/// <c>1.2.3.4</c> yakalanmasını engelliyor. Sınırı yanlış kurmak sessizce
/// yanlış IP yakalamak demek, ve bu hiçbir yerde patlamaz.
/// </para>
///
/// <para>
/// Bu yüzden testler legacy pattern'i <b>kâhin (oracle)</b> olarak kullanıyor:
/// aynı girdi iki sete de veriliyor ve sonuç karşılaştırılıyor. Kabul edilen
/// tek fark, <see cref="Harfe_bitisik_ip_kacirilir_bilincli_sapma"/> içinde
/// tek tek yazılı.
/// </para>
/// </summary>
public sealed class BizigoV1PatternTests
{
    private static readonly GrokPatternLibrary Legacy =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static readonly GrokPatternLibrary Overlaid = Legacy.With(OverlayEntries());

    private static readonly GrokCompiler LegacyCompiler = new(Legacy);
    private static readonly GrokCompiler OverlaidCompiler = new(Overlaid);

    /// <summary>Kaplama dizinini ad → gövde sözlüğü olarak okur.</summary>
    private static IEnumerable<KeyValuePair<string, string>> OverlayEntries()
    {
        var overlay = GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.BizigoV1PatternDirectory);

        foreach (var name in overlay.Names)
        {
            if (overlay.TryGet(name, out var pattern))
            {
                yield return new KeyValuePair<string, string>(name, pattern);
            }
        }
    }

    private static string? Capture(GrokCompiler compiler, string expression, string field, string input)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var result = compiler.Compile(expression).Match(input, fields);

        return result.Matched && fields.TryGetValue(field, out var value) ? value as string : null;
    }

    // ------------------------------------------------------------------ kapsam

    [Fact]
    public void Kaplama_yalnizca_bilinen_pattern_leri_degistiriyor()
    {
        var overlay = GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.BizigoV1PatternDirectory);

        Assert.Equal(["IPV4", "TIME", "YEAR"], overlay.Names.Order(StringComparer.Ordinal));

        // Kaplama TAM SET DEĞİL: legacy'nin üstüne bindiğinde pattern sayısı
        // değişmemeli. Değişiyorsa kaplamaya yeni bir ad girmiş demektir ve
        // upstream'de karşılığı olmayan bir pattern sessizce doğmuştur.
        Assert.Equal(Legacy.Count, Overlaid.Count);
    }

    [Theory]
    [InlineData("IPV4")]
    [InlineData("TIME")]
    [InlineData("YEAR")]
    public void Kaplanan_pattern_dogrusal_motorla_derleniyor(string name)
    {
        Assert.False(
            LegacyCompiler.Compile("%{" + name + "}").IsLinearTime,
            $"{name}: legacy tanımı zaten doğrusal derleniyorsa kaplamaya gerek yok.");

        var compiled = OverlaidCompiler.Compile("%{" + name + "}");

        Assert.True(compiled.IsLinearTime, $"{name}: {compiled.FallbackReason}");
    }

    // -------------------------------------------------------------- IPV4 sınır

    /// <summary>
    /// Sınır davranışının korunduğu durumlar. Beklenen değer legacy'den
    /// bağımsız olarak da yazılı: kâhin bozulursa test sessizce onaylamasın.
    /// </summary>
    [Theory]
    // düz eşleşme
    [InlineData("10.1.2.3", "10.1.2.3")]
    [InlineData("255.255.255.255", "255.255.255.255")]
    // kelime dışı karakterlerle sınırlanmış — log alanlarının normal hâli
    [InlineData("srcip=10.1.2.3 dstip=8.8.8.8", "10.1.2.3")]
    [InlineData("inside:172.31.98.44/1772", "172.31.98.44")]
    [InlineData("::ffff:10.10.4.4/0", "10.10.4.4")]
    [InlineData(" 10.0.0.1/24", "10.0.0.1")]
    // ASIL MESELE: sondaki rakam eşleşmeyi uzatmalı, kırpmamalı
    [InlineData("1.2.3.45", "1.2.3.45")]
    [InlineData("1.2.3.4.5", "1.2.3.4")]
    // baştaki sıfır oktetin parçası
    [InlineData("01.2.3.4", "01.2.3.4")]
    public void Ipv4_sinir_davranisi_legacy_ile_ayni(string input, string expected)
    {
        var legacy = Capture(LegacyCompiler, "%{IPV4:ip}", "ip", input);
        var overlaid = Capture(OverlaidCompiler, "%{IPV4:ip}", "ip", input);

        Assert.Equal(expected, legacy);
        Assert.Equal(expected, overlaid);
    }

    [Theory]
    // dördüncü oktet üç rakamdan uzun → geçerli IPv4 yok
    [InlineData("1.2.3.456")]
    // ilk oktet 255'ten büyük; kalan parçalar rakam komşuluğu yüzünden yakalanamaz
    [InlineData("256.1.1.1")]
    [InlineData("999.1.1.1")]
    // üç oktet
    [InlineData("1.2.3")]
    public void Ipv4_eslesmemesi_gereken_girdiler(string input)
    {
        Assert.Null(Capture(LegacyCompiler, "%{IPV4:ip}", "ip", input));
        Assert.Null(Capture(OverlaidCompiler, "%{IPV4:ip}", "ip", input));
    }

    /// <summary>
    /// Kabul edilen TEK sapma. <c>\b</c> harf komşuluğunu da engelliyor;
    /// lookaround engellemiyordu. Yön önemli: kaplama <b>daha az</b> yakalıyor,
    /// fazla değil — yanlış pozitif üretmesi imkânsız.
    /// </summary>
    [Theory]
    [InlineData("host1.2.3.4", "1.2.3.4")]
    [InlineData("1.2.3.4abc", "1.2.3.4")]
    [InlineData("_10.0.0.1", "10.0.0.1")]
    public void Harfe_bitisik_ip_kacirilir_bilincli_sapma(string input, string legacyMatch)
    {
        Assert.Equal(legacyMatch, Capture(LegacyCompiler, "%{IPV4:ip}", "ip", input));
        Assert.Null(Capture(OverlaidCompiler, "%{IPV4:ip}", "ip", input));
    }

    // -------------------------------------------------------------- TIME sınır

    [Theory]
    [InlineData("14:49:33", "14:49:33")]
    [InlineData("Oct 10 2018 12:34:56 localhost", "12:34:56")]
    [InlineData("13:34:06: %ASA-5-111007", "13:34:06")]
    // kesirli saniye SECOND'ın kendi işi
    [InlineData("02:51:22.573113-06:00", "02:51:22.573113")]
    public void Time_sinir_davranisi_legacy_ile_ayni(string input, string expected)
    {
        var legacy = Capture(LegacyCompiler, "%{TIME:t}", "t", input);
        var overlaid = Capture(OverlaidCompiler, "%{TIME:t}", "t", input);

        Assert.Equal(expected, legacy);
        Assert.Equal(expected, overlaid);
    }

    /// <summary>
    /// Upstream'in <c>(?!<[0-9])</c> yazım hatası zararsız DEĞİL.
    ///
    /// <para>
    /// Niyet "rakamla başlama" (<c>(?&lt;![0-9])</c>) ama yazılan "`&lt;` ve
    /// ardından rakam gelmesin". Böyle bir şart pratikte hep sağlandığı için
    /// <c>%{TIME}</c> bir sayının ORTASINDAN eşleşmeye başlayabiliyor:
    /// <c>25/Oct/2016:14:49:33</c> girdisinde legacy, yıl olan <c>2016</c>'nın
    /// içindeki <c>16</c>'dan başlıyor ve <c>SECOND</c>'ın isteğe bağlı
    /// <c>[:.,][0-9]+</c> kuyruğu da <c>:33</c>'ü yutuyor.
    /// </para>
    ///
    /// <para>
    /// Kataloğumuz bunu görmüyor çünkü <c>TIME</c> her yerde daha büyük bir
    /// pattern'in içinde (<c>HTTPDATE</c>, <c>CISCOTIMESTAMP</c>) ve öncesindeki
    /// bağlam konumu zorluyor. Tek başına kullanılan <c>%{TIME}</c> ise sessizce
    /// yanlış saat üretiyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Time_sayinin_ortasindan_baslamiyor_upstream_yazim_hatasi_duzeliyor()
    {
        const string input = "25/Oct/2016:14:49:33 +0200";

        Assert.Equal("16:14:49:33", Capture(LegacyCompiler, "%{TIME:t}", "t", input));
        Assert.Equal("14:49:33", Capture(OverlaidCompiler, "%{TIME:t}", "t", input));
    }

    [Fact]
    public void Time_sonunda_fazladan_rakam_eslesmeyi_kirpmiyor()
    {
        // `12:34:567` içinde `12:34:56` yakalanırsa saniye sessizce yanlış olur.
        Assert.Null(Capture(LegacyCompiler, "%{TIME:t}", "t", "12:34:567"));
        Assert.Null(Capture(OverlaidCompiler, "%{TIME:t}", "t", "12:34:567"));
    }

    // -------------------------------------------------------------- YEAR sınır

    /// <summary>
    /// <c>YEAR</c>'daki atomik grup ETKİSİZ: <c>\d\d</c> sabit uzunlukta ve
    /// içinde seçim yok, dolayısıyla geri izlenecek bir şey yok; <c>{1,2}</c> de
    /// grubun dışında. Bu test o iddiayı ölçüyor — akıl yürütme doğru olsa da
    /// sabitlenmemiş bir iddia, sonraki değişiklikte sessizce bozulur.
    /// </summary>
    [Theory]
    [InlineData("2016", "2016")]
    [InlineData("16", "16")]
    // üç rakam: iki rakamlık tekrar bir kez uyuyor, kalan rakam dışarıda
    [InlineData("201", "20")]
    // beş rakam: greedy {1,2} dört rakamı alıyor
    [InlineData("20165", "2016")]
    [InlineData("Oct 10 2018 12:34:56", "10")]
    public void Year_legacy_ile_ayni_eslesiyor(string input, string expected)
    {
        var legacy = Capture(LegacyCompiler, "%{YEAR:y}", "y", input);
        var overlaid = Capture(OverlaidCompiler, "%{YEAR:y}", "y", input);

        Assert.Equal(expected, legacy);
        Assert.Equal(expected, overlaid);
    }

    [Fact]
    public void Year_kullanan_ust_pattern_de_ayni_kaliyor()
    {
        // CISCOTIMESTAMP ve TIMESTAMP_ISO8601 `%{YEAR}`'a dayanıyor; değişikliğin
        // etkisi tek başına YEAR'da değil, onları kullanan zarflarda görülür.
        const string cisco = "Oct 10 2018 12:34:56 localhost";
        Assert.Equal(
            Capture(LegacyCompiler, "%{CISCOTIMESTAMP:t}", "t", cisco),
            Capture(OverlaidCompiler, "%{CISCOTIMESTAMP:t}", "t", cisco));

        const string iso = "2022-04-12T02:51:22.573113-06:00";
        Assert.Equal(
            Capture(LegacyCompiler, "%{TIMESTAMP_ISO8601:t}", "t", iso),
            Capture(OverlaidCompiler, "%{TIMESTAMP_ISO8601:t}", "t", iso));
    }

    // ------------------------------------------------- katalog üzerinde etkisi

    /// <summary>
    /// Kaplama gerçek katalogda hiçbir şeyi bozmamalı: sekiz parser'ın gömülü
    /// testleri ve altın örneklerin kapsamı legacy ile aynı kalmalı.
    /// </summary>
    [Fact]
    public void Katalog_kaplamayla_da_ayni_sonucu_veriyor()
    {
        var tables = MappingTableCatalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

        var compiler = new ParserCompiler(new GrokCompiler(Overlaid), tables);

        foreach (var path in Directory.EnumerateFiles(
                     RepositoryLayout.CatalogParserDirectory, "*.yaml", SearchOption.AllDirectories))
        {
            var compiled = compiler.CompileFile(path);
            Assert.True(compiled.Ok, string.Join(Environment.NewLine, compiled.Errors.Select(e => e.ToString())));

            var report = ParserTestRunner.Run(compiled.Value);
            Assert.True(report.Passed, $"{Path.GetFileName(path)}: {report.FailCount} gömülü test kaldı.");
        }

        var coverage = SampleCoverage.Run(RepositoryLayout.CatalogParserDirectory, compiler);

        Assert.Equal(0, coverage.Failed);
        Assert.Equal(86, coverage.Ok);
        Assert.Equal(1, coverage.Partial);
    }
}
