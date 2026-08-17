using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

/// <summary>
/// nginx parser'larının kendi sayı pattern'i (<c>NGINX_NUM</c>) için kâhin testi.
///
/// <para>
/// <b>Neden paylaşılan sette değil:</b> upstream <c>NUMBER</c> → <c>BASE10NUM</c>
/// hem lookbehind (<c>(?&lt;![0-9.+-])</c>) hem atomik grup (<c>(?&gt;...)</c>)
/// taşıyor. Atomik grup <c>YEAR</c>'daki gibi etkisiz DEĞİL: kaldırıldığında
/// <c>%{NUMBER}\.%{NUMBER}</c> bileşimi upstream'de hiç eşleşmezken eşleşir hale
/// geliyor — yani değişiklik daha geçirgen yönde, <c>IPV4</c>/<c>TIME</c>'daki
/// <c>\b</c>'nin tersine. Lookbehind'ın da lookaround'suz karşılığı yok:
/// <c>\b</c> negatif sayıyı (<c>-5</c>) kırar, karakter tüketen bir öncül
/// eşleşme uzunluğunu kaydırır. Bu yüzden <c>BASE10NUM</c>'a dokunulmadı.
/// </para>
///
/// <para>
/// Daraltma bağlama özel: nginx bu alanlara yalnızca işaretsiz ondalık yazıyor.
/// Testler bunu upstream <c>%{NUMBER}</c> kâhin alarak sabitliyor, artı
/// daraltmanın <b>bilinçli olarak kapsamadığı</b> biçimleri de yazıyor.
/// </para>
/// </summary>
public sealed class NginxNumberPatternTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static string NginxNumBody(string parserFile)
    {
        var path = Path.Combine(RepositoryLayout.CatalogParserDirectory, "nginx.access", parserFile);
        var loaded = ParserYamlLoader.LoadFile(path);

        Assert.True(loaded.Ok, loaded.Describe());
        Assert.True(loaded.Value.PatternDefinitions.TryGetValue("NGINX_NUM", out var body),
            $"'NGINX_NUM' tanımı {parserFile} içinde yok.");

        return body!;
    }

    private static GrokCompiler NginxCompiler() =>
        new GrokCompiler(Library).With(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NGINX_NUM"] = NginxNumBody("combined.yaml"),
        });

    private static string? Capture(GrokCompiler compiler, string expression, string input)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var result = compiler.Compile(expression).Match(input, fields);

        return result.Matched && fields.TryGetValue("a", out var value) ? value as string : null;
    }

    [Fact]
    public void Iki_nginx_parseri_ayni_tanimi_kullaniyor()
    {
        // İki dosyada iki ayrı tanım sürüklenirse biri güncellenip diğeri
        // unutulur; aynı olduklarını burada sabitliyoruz.
        Assert.Equal(NginxNumBody("combined.yaml"), NginxNumBody("access-json.yaml"));
    }

    /// <summary>
    /// nginx'in bu alanlara gerçekten yazdığı değerler. Altın örnek
    /// dosyalarındaki durum kodları, bayt sayıları, `$request_time` ve HTTP
    /// sürümleri buradan geliyor.
    /// </summary>
    [Theory]
    [InlineData("200")]
    [InlineData("404")]
    [InlineData("0")]
    [InlineData("612")]
    [InlineData("7648063")]
    [InlineData("26318005")]
    [InlineData("45.324")]
    [InlineData("1.1")]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public void Nginx_num_upstream_number_ile_ayni_yakaliyor(string value)
    {
        var upstream = Capture(new GrokCompiler(Library), "^%{NUMBER:a}$", value);
        var narrowed = Capture(NginxCompiler(), "^%{NGINX_NUM:a}$", value);

        Assert.Equal(value, upstream);
        Assert.Equal(upstream, narrowed);
    }

    /// <summary>
    /// Daraltmanın bilinçli olarak KAPSAMADIĞI biçimler. nginx bunları durum
    /// kodu, bayt ya da süre alanına yazmıyor; kapsamamak daraltmanın amacı.
    /// Biçim bir gün gerekirse bu test kırmızı yanar ve tanım genişletilir.
    /// </summary>
    [Theory]
    [InlineData("-5")]
    [InlineData("+3.0")]
    [InlineData(".5")]
    public void Nginx_num_isaretli_ve_bastan_noktali_bicimleri_kapsamiyor(string value)
    {
        Assert.Equal(value, Capture(new GrokCompiler(Library), "^%{NUMBER:a}$", value));
        Assert.Null(Capture(NginxCompiler(), "^%{NGINX_NUM:a}$", value));
    }

    [Fact]
    public void Nginx_num_dogrusal_motorla_derleniyor()
    {
        Assert.False(new GrokCompiler(Library).Compile("^%{NUMBER:a}$").IsLinearTime,
            "Upstream %{NUMBER} zaten doğrusal derleniyorsa bu pattern'e gerek yok.");

        var compiled = NginxCompiler().Compile("^%{NGINX_NUM:a}$");

        Assert.True(compiled.IsLinearTime, compiled.FallbackReason);
    }
}
