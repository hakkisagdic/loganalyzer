using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;
using Bizigo.Parsing.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// T08 vendor kataloğunun bekçisi.
///
/// <para>
/// CI zaten <c>bizigo parser lint|test|coverage</c> koşturuyor; bu sınıf aynı
/// kapıyı birim test tarafında da tutuyor. Sebebi pratik: motor değiştiğinde
/// (T05'e geri besleme sonrası) kırılmayı <c>dotnet test</c> adımında görmek,
/// CLI adımını beklemekten hızlı ve hata mesajı ayrıntılı geliyor.
/// </para>
/// </summary>
public sealed class VendorCatalogTests
{
    // Üretimin kurulumu (legacy + bizigo-v1). Kaplamasız yüklemek kataloğun
    // pattern'lerini geri izlemeli motora düşürüyordu ve oradaki duvar saati
    // yüklü makinede sağlıklı satırları `failed` yapıyordu.
    private static readonly GrokPatternLibrary Library = RepositoryLayout.DefaultLibrary;

    private static readonly MappingTableCatalog Tables =
        MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    private static ParserCompiler NewCompiler() => new(new GrokCompiler(Library), Tables);

    public static TheoryData<string> ParserFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Directory.EnumerateFiles(
                         RepositoryLayout.CatalogParserDirectory, "*.yaml", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(RepositoryLayout.CatalogParserDirectory, path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ParserFiles))]
    public void Katalogdaki_parser_derleniyor_ve_gomulu_testleri_geciyor(string relativePath)
    {
        var path = Path.Combine(RepositoryLayout.CatalogParserDirectory, relativePath);

        var compiled = NewCompiler().CompileFile(path);
        Assert.True(compiled.Ok, string.Join(Environment.NewLine, compiled.Errors.Select(e => e.ToString())));

        var report = ParserTestRunner.Run(compiled.Value);

        Assert.NotEmpty(report.Tests);
        Assert.True(report.Passed, Describe(report));
    }

    /// <summary>
    /// Kataloğun <b>tamamı doğrusal motorda</b> derleniyor — üretimin kütüphanesiyle
    /// sıfır GROK003.
    ///
    /// <para>
    /// Bu değişmez şimdiye kadar yalnızca CI'daki <c>bizigo parser lint</c> adımında
    /// tutuluyordu, ve tutulmadığında sessizce bozuluyor: geri izlemeye düşen bir
    /// pattern <c>MatchTimeout</c> ödüyor, o da <b>duvar saatini</b> ölçüyor.
    /// Yüklü makinede sağlıklı bir satır zaman aşımına uğrayıp <c>failed</c>
    /// oluyor — yani "motor meşguldü" ile "bu satır uymuyor" ayırt edilemiyor.
    /// Testin kendisi de bu yüzden kararsızdı.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ParserFiles))]
    public void Katalogdaki_parser_geri_izlemeye_dusmuyor(string relativePath)
    {
        var path = Path.Combine(RepositoryLayout.CatalogParserDirectory, relativePath);
        var report = ParserLinter.LintFile(path, NewCompiler());

        var fallbacks = report.RedosFindings.Where(static f => f.Code == "GROK003").ToArray();

        Assert.True(
            fallbacks.Length == 0,
            $"{relativePath}: {fallbacks.Length} ifade geri izlemeli motorda derlendi." +
            Environment.NewLine +
            string.Join(Environment.NewLine, fallbacks.Select(static f => $"  {f.Message} → {f.Fragment}")));
    }

    private static string Describe(ParserTestReport report) => string.Join(
        Environment.NewLine,
        report.Tests
            .Where(static test => !test.Passed)
            .Select(static test => $"{test.Name} (satır {test.Line})" + Environment.NewLine +
                string.Join(Environment.NewLine, test.Failures.Select(static f => f.Describe()))));

    /// <summary>
    /// Kabul kriteri: <b>negatif testler geçiyor — bir vendor'ın satırı başka
    /// vendor'ın parser'ına düşmüyor.</b> Gömülü testler bunu tek parser
    /// düzeyinde gösteriyor; burada aynı soru kataloğun tamamı ayaktayken
    /// dispatcher üzerinden soruluyor.
    /// </summary>
    [Fact]
    public void Altin_ornekler_kendi_vendor_parserina_dusuyor()
    {
        var report = SampleCoverage.Run(RepositoryLayout.CatalogParserDirectory, NewCompiler());

        Assert.NotEmpty(report.Files);
        Assert.Equal(0, report.Failed);

        foreach (var file in report.Files)
        {
            // `catalog/parsers/<id>/samples/<ad>.log` → beklenen parser öneki `<id>`.
            var parserDirectory = Path.GetFileName(
                Path.GetDirectoryName(Path.GetDirectoryName(file.Path))!);

            foreach (var (parserId, count) in file.ByParser)
            {
                Assert.True(
                    parserId.StartsWith(parserDirectory, StringComparison.Ordinal),
                    $"{Path.GetFileName(file.Path)}: {count} satır '{parserId}' parser'ına düştü, " +
                    $"beklenen önek '{parserDirectory}'.");
            }
        }
    }

    /// <summary>
    /// Kabul kriteri: <b>Cisco ASA'nın en az 5 farklı mesaj kodu doğru
    /// ayrıştırılıyor.</b> Sayım örnek dosyadan yapılıyor, gömülü testlerden
    /// değil: örnekler gerçek cihaz çıktısı, testler onların alt kümesi.
    /// </summary>
    [Fact]
    public void Cisco_asa_en_az_bes_farkli_mesaj_kodunu_ayristiriyor()
    {
        var directory = Path.Combine(RepositoryLayout.CatalogParserDirectory, "cisco.asa");
        var parser = NewCompiler().CompileFile(Path.Combine(directory, "network.yaml"));
        Assert.True(parser.Ok, string.Join(Environment.NewLine, parser.Errors.Select(e => e.ToString())));

        var codes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(Path.Combine(directory, "samples", "network.log")))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var result = parser.Value.Parse(line);
            if (result.Status == ParseStatus.Failed)
            {
                continue;
            }

            if (result.Fields.TryGetValue("message_code", out var code) && code is string text)
            {
                codes.Add(text);
            }
        }

        Assert.True(codes.Count >= 5, $"Yalnızca {codes.Count} farklı mesaj kodu ayrıştırıldı: " +
            string.Join(", ", codes.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Ön filtre literalleri gerçekten ayırt edici olmalı (F1 §4.2). Çok genel
    /// bir literal, Aho-Corasick otomatını her satırda tetikler ve 3. kademeyi
    /// tüm katalogla denemeye çevirir — ön filtrenin var olma sebebini yok eder.
    /// </summary>
    [Fact]
    public void Her_parserin_ayirt_edici_literali_var()
    {
        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(RepositoryLayout.CatalogParserDirectory, NewCompiler());

        Assert.Empty(load.Errors);
        Assert.True(catalog.Count > 0);

        // Literali olmayan parser her satırda aday olmak zorunda kalıyor.
        Assert.Empty(catalog.Current.LiteralFree);

        foreach (var parser in catalog.Current.Parsers)
        {
            foreach (var literal in parser.Definition.Match.Contains)
            {
                Assert.True(literal.Length >= 5,
                    $"{parser.Id}: '{literal}' ön filtre için fazla kısa.");
            }
        }
    }
}
