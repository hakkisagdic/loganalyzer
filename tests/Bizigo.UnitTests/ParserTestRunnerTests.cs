using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;
using Bizigo.Parsing.Testing;

namespace Bizigo.UnitTests;

public sealed class ParserTestRunnerTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static readonly MappingTableCatalog Tables =
        MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    private static CompiledParser Compile(string yaml)
    {
        var loaded = ParserYamlLoader.Load(yaml);
        Assert.True(loaded.Ok, loaded.Describe());

        var compiled = new ParserCompiler(new GrokCompiler(Library), Tables).Compile(loaded.Value);
        Assert.True(compiled.Ok, string.Join("; ", compiled.Errors.Select(e => e.Message)));

        return compiled.Value;
    }

    [Fact]
    public void Gecen_test_gecer()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.ok, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
              - convert: { fields: { port: int } }
            map:
              core: { dst_port: "{{ port }}" }
            tests:
              - name: temel
                input: 'port=53'
                expect:
                  core.dst_port: 53
                  parse_status: ok
            """));

        Assert.True(report.Passed);
        Assert.Equal(1, report.PassCount);
    }

    [Fact]
    public void Basarisiz_testte_anlamli_fark_gosterilir()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.fail, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            tests:
              - name: yanlis_beklenti
                input: 'port=53'
                expect:
                  fields.port: "80"
            """));

        Assert.False(report.Passed);

        var failure = Assert.Single(report.Tests[0].Failures);
        Assert.Equal("fields.port", failure.Key);

        var described = failure.Describe();
        Assert.Contains("beklenen: \"80\"", described, StringComparison.Ordinal);
        Assert.Contains("gerçek  : \"53\"", described, StringComparison.Ordinal);
    }

    [Fact]
    public void Eksik_alan_yok_olarak_raporlanir()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.missing, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            tests:
              - name: olmayan_alan
                input: 'a=1'
                expect:
                  fields.b: "2"
            """));

        Assert.Contains("<yok>", report.Tests[0].Failures.Single().Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sayisal_karsilastirma_tip_farkini_yok_sayar()
    {
        // YAML `53` yazınca long okunur, motor `convert: int` ile int üretir.
        // Kullanıcıya motorun iç tipini dert ettirmek anlamsız.
        Assert.True(ParserTestRunner.ValuesMatch(53L, 53));
        Assert.True(ParserTestRunner.ValuesMatch(53, 53.0));
        Assert.False(ParserTestRunner.ValuesMatch(53, 54));
    }

    [Fact]
    public void Bool_ile_string_karistirilmaz()
    {
        Assert.False(ParserTestRunner.ValuesMatch(true, "true"));
        Assert.True(ParserTestRunner.ValuesMatch(true, true));
    }

    /// <summary>
    /// <b>T08 raporu #6.</b> "Bu alan hiç olmamalı" beklentisi — düz
    /// <c>null</c>/<c>~</c> skaleri gerçek <c>null</c>'a dönüyor.
    ///
    /// <para>
    /// Üç durum <b>ayrı ayrı</b> sınanıyor ve ayrı kalmak zorundalar:
    /// <c>null</c> (alan yok), <c>~</c> (aynısının öbür yazımı) ve tırnaklı
    /// <c>"null"</c> (alanın değeri <c>null</c> <b>metni</b>). Üçünü tek testte
    /// toplamak, altı ay sonra birinin <c>"null"</c> metnini bekleyip alan
    /// yokluğunu ölçtüğünü sanmasına yol açardı.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("~")]
    public void Duz_null_skaleri_alan_yoklugunu_dogruluyor(string nullForm)
    {
        // Şablon sözdizimi (`{{ user }}`) ile C# ilişimi çakışıyor; YAML düz
        // metin bırakılıp yalnızca null yazımı yerine konuyor.
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.absent, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            map:
              core: { user_name: "{{ user }}" }
            tests:
              - name: kullanici_adi_yok
                input: 'a=1'
                expect:
                  core.user_name: NULL_FORM
            """.Replace("NULL_FORM", nullForm, StringComparison.Ordinal)));

        Assert.True(
            report.Passed,
            string.Join("; ", report.Tests.SelectMany(t => t.Failures).Select(f => f.Describe())));
    }

    /// <summary>
    /// Aynı beklenti, alan <b>varken</b> düşüyor. Bu olmadan yukarıdaki test
    /// "her şeye yeşil yanan" bir beklenti de olabilirdi — `null` beklentisinin
    /// değeri, reddedebilmesinde.
    /// </summary>
    [Fact]
    public void Alan_atanmissa_null_beklentisi_dusuyor()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.present, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            map:
              core: { user_name: "{{ user }}" }
            tests:
              - name: kullanici_adi_var
                input: 'user=admin'
                expect:
                  core.user_name: null
            """));

        Assert.False(report.Passed);

        var failure = Assert.Single(report.Tests[0].Failures);
        Assert.Contains("beklenen: <yok>", failure.Describe(), StringComparison.Ordinal);
        Assert.Contains("gerçek  : \"admin\"", failure.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Tırnaklı <c>"null"</c> hâlâ <b>metin</b>. Bu ayrım kaybolursa, değeri
    /// gerçekten <c>null</c> metni olan bir alanı sınamanın yolu kalmazdı.
    /// </summary>
    [Fact]
    public void Tirnakli_null_metin_olarak_kaliyor()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.literal, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            tests:
              - name: null_metni_esleşiyor
                input: 'user=null'
                expect:
                  fields.user: "null"
              - name: null_metni_alan_yoklugu_degil
                input: 'user=null'
                expect:
                  fields.user: null
            """));

        Assert.True(report.Tests[0].Passed, "Tırnaklı \"null\" metin olarak eşleşmeli.");
        Assert.False(report.Tests[1].Passed, "Değeri \"null\" metni olan alan, YOK sayılmamalı.");
    }

    [Fact]
    public void Etiketler_dizi_olarak_dogrulanir()
    {
        var report = ParserTestRunner.Run(Compile("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.runner.tags, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
              - grok: { field: message, patterns: ["^ASLA$"], on_failure: tag, tag: _grok_failure }
            tests:
              - name: etiket
                input: 'a=1'
                expect:
                  parse_status: partial
                  tags: ["_grok_failure"]
            """));

        Assert.True(report.Passed, string.Join("; ", report.Tests.SelectMany(t => t.Failures).Select(f => f.Describe())));
    }
}
