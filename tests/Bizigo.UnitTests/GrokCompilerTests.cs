using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

public sealed class GrokCompilerTests
{
    private static GrokCompiler Compiler(params (string Name, string Pattern)[] patterns) =>
        new(GrokPatternLibrary.FromDictionary(
            patterns.Select(p => new KeyValuePair<string, string>(p.Name, p.Pattern))));

    private static Dictionary<string, object?> NewBag() => new(StringComparer.Ordinal);

    [Fact]
    public void Adlandirilmis_alan_yakalanir()
    {
        var grok = Compiler(("WORD", @"\b\w+\b")).Compile("%{WORD:action}");
        var bag = NewBag();

        Assert.True(grok.Match("accept", bag).Matched);
        Assert.Equal("accept", bag["action"]);
    }

    [Fact]
    public void Adsiz_referans_yakalama_uretmez_ama_ic_yakalamalar_kalir()
    {
        var compiler = Compiler(
            ("NUM", @"\d+"),
            ("PAIR", "%{NUM:left}-%{NUM:right}"));

        var grok = compiler.Compile("%{PAIR}");
        var bag = NewBag();

        Assert.True(grok.Match("10-20", bag).Matched);
        Assert.Equal("10", bag["left"]);
        Assert.Equal("20", bag["right"]);
        Assert.DoesNotContain("PAIR", bag.Keys);
    }

    [Theory]
    [InlineData("int", 53)]
    [InlineData("long", 53L)]
    public void Tip_soneki_donusturur(string type, object expected)
    {
        var grok = Compiler(("NUM", @"\d+")).Compile($"%{{NUM:port:{type}}}");
        var bag = NewBag();

        Assert.True(grok.Match("53", bag).Matched);
        Assert.Equal(expected, bag["port"]);
    }

    [Fact]
    public void Donusturulemeyen_deger_string_kalir_olay_kaybolmaz()
    {
        // Ağ cihazı bazen `port=-` gönderir. Tek bozuk alan yüzünden satırı
        // düşürmek kabul edilemez.
        var grok = Compiler(("ANY", @"\S+")).Compile("%{ANY:port:int}");
        var bag = NewBag();

        Assert.True(grok.Match("-", bag).Matched);
        Assert.Equal("-", bag["port"]);
    }

    [Fact]
    public void Ondalik_daima_invariant_kulturle_okunur()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
        try
        {
            var grok = Compiler(("ANY", @"[\d.]+")).Compile("%{ANY:duration:float}");
            var bag = NewBag();

            Assert.True(grok.Match("1.5", bag).Matched);
            Assert.Equal(1.5d, bag["duration"]);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Ayni_alan_iki_alternatifte_ilk_basarili_yakalamayi_alir()
    {
        var compiler = Compiler(
            ("A", "a(?<value>1)"),
            ("B", "b(?<value>2)"));

        var grok = compiler.Compile("(?:%{A}|%{B})");
        var bag = NewBag();

        Assert.True(grok.Match("b2", bag).Matched);
        Assert.Equal("2", bag["value"]);
    }

    [Fact]
    public void Bracket_alan_adlari_noktali_hale_gelir()
    {
        var grok = Compiler(("IP", @"\d+\.\d+\.\d+\.\d+")).Compile("%{IP:[source][ip]}");
        var bag = NewBag();

        Assert.True(grok.Match("10.0.0.5", bag).Matched);
        Assert.Equal("10.0.0.5", bag["source.ip"]);
    }

    [Fact]
    public void Oniguruma_adlandirilmis_grubu_bracket_adiyla_calisir()
    {
        var grok = Compiler().Compile("(?<[exim][log][flags]>[a-z]+)");
        var bag = NewBag();

        Assert.True(grok.Match("abc", bag).Matched);
        Assert.Equal("abc", bag["exim.log.flags"]);
    }

    [Fact]
    public void Geriye_bakis_adlandirilmis_grup_sanilmaz()
    {
        var grok = Compiler().Compile("(?<![0-9])42");
        var bag = NewBag();

        Assert.True(grok.Match("x42", bag).Matched);
        Assert.False(grok.Match("142", bag).Matched);
        Assert.False(grok.IsLinearTime); // geriye bakış → geri izlemeli motor
    }

    [Fact]
    public void Oniguruma_hex_kisayolu_cevrilir()
    {
        var grok = Compiler().Compile(@"^\h{4}$");
        var bag = NewBag();

        Assert.True(grok.Match("dead", bag).Matched);
        Assert.False(grok.Match("zzzz", bag).Matched);
    }

    [Fact]
    public void Posix_karakter_sinifi_cevrilir()
    {
        var grok = Compiler().Compile("^[[:alnum:]]+$");
        var bag = NewBag();

        Assert.True(grok.Match("abc123", bag).Matched);
        Assert.False(grok.Match("abc-123", bag).Matched);
    }

    [Fact]
    public void Basit_pattern_dogrusal_motorda_derlenir()
    {
        var grok = Compiler(("WORD", @"\w+")).Compile("%{WORD:a} %{WORD:b}");
        Assert.True(grok.IsLinearTime);
        Assert.Null(grok.FallbackReason);
    }

    [Fact]
    public void Bilinmeyen_pattern_anlamli_hata_verir()
    {
        var compiler = Compiler(("SYSLOGHOST", @"\S+"));
        var ex = Assert.Throws<GrokCompilationException>(() => compiler.Compile("%{SYSLOGHOSTNAME:h}"));

        Assert.Contains("SYSLOGHOSTNAME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SYSLOGHOST", ex.Message, StringComparison.Ordinal); // öneri
    }

    [Fact]
    public void Ozyinelemeli_pattern_yakalanir()
    {
        var compiler = Compiler(("LOOP", "x%{LOOP}"));
        var ex = Assert.Throws<GrokCompilationException>(() => compiler.Compile("%{LOOP}"));

        Assert.Contains("Özyinelemeli", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bilinmeyen_tip_soneki_hata_verir()
    {
        var compiler = Compiler(("NUM", @"\d+"));
        var ex = Assert.Throws<GrokCompilationException>(() => compiler.Compile("%{NUM:x:uint}"));

        Assert.Contains("uint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_ifade_onbellekten_gelir()
    {
        var compiler = Compiler(("WORD", @"\w+"));
        Assert.Same(compiler.Compile("%{WORD:a}"), compiler.Compile("%{WORD:a}"));
    }

    [Fact]
    public void Pattern_definitions_ile_genisletme()
    {
        var compiler = Compiler(("WORD", @"\w+"))
            .With(new Dictionary<string, string>(StringComparer.Ordinal) { ["FGID"] = @"id=\d+" });

        var grok = compiler.Compile("%{FGID:fgid} %{WORD:rest}");
        var bag = NewBag();

        Assert.True(grok.Match("id=42 ok", bag).Matched);
        Assert.Equal("id=42", bag["fgid"]);
    }
}
