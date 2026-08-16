using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

public sealed class ParserYamlLoaderTests
{
    private const string MinimalParser = """
        apiVersion: bizigo.dev/v1
        kind: Parser
        metadata:
          id: acme.router.syslog
          version: 1.0.0
        pipeline:
          - kv: { field: message }
        tests:
          - name: temel
            input: 'a=1'
            expect:
              fields.a: "1"
        """;

    [Fact]
    public void Gecerli_parser_yuklenir()
    {
        var result = ParserYamlLoader.Load(MinimalParser);

        Assert.True(result.Ok, result.Describe());
        Assert.Equal("acme.router.syslog", result.Value.Metadata.Id);
        Assert.Equal("acme.router.syslog@1.0.0", result.Value.CacheKey);
        Assert.Single(result.Value.Pipeline);
        Assert.Single(result.Value.Tests);
    }

    [Fact]
    public void Testsiz_parser_reddedilir()
    {
        // F1 §3'ün en ucuz kalite kaldıracı. Şema düzeyinde zorlanmazsa yazılmaz.
        var yaml = MinimalParser[..MinimalParser.IndexOf("tests:", StringComparison.Ordinal)];
        var result = ParserYamlLoader.Load(yaml);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("testsiz parser yayınlanamaz", StringComparison.Ordinal));
    }

    [Fact]
    public void Bilinmeyen_anahtar_hata_verir_ve_oneri_sunar()
    {
        var yaml = MinimalParser.Replace("  - kv: { field: message }", "  - kv: { field: message, seperator: \" \" }", StringComparison.Ordinal);
        var result = ParserYamlLoader.Load(yaml);

        Assert.False(result.Ok);
        var error = Assert.Single(result.Errors);
        Assert.Contains("seperator", error.Message, StringComparison.Ordinal);
        Assert.Contains("'separator' mi demek istediniz?", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hatalar_satir_ve_sutun_tasir()
    {
        var result = ParserYamlLoader.Load(MinimalParser.Replace("kind: Parser", "kind: Parsr", StringComparison.Ordinal));

        Assert.False(result.Ok);
        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.Line);
        Assert.True(error.Column > 0);
    }

    [Fact]
    public void Yanlis_apiVersion_reddedilir()
    {
        var result = ParserYamlLoader.Load(
            MinimalParser.Replace("bizigo.dev/v1", "bizigo.dev/v2", StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Desteklenmeyen apiVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void Yaml_soz_dizimi_hatasi_konumla_bildirilir()
    {
        var result = ParserYamlLoader.Load("apiVersion: bizigo.dev/v1\n  kind: [Parser\n");

        Assert.False(result.Ok);
        var error = Assert.Single(result.Errors);
        Assert.Contains("YAML söz dizimi hatası", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bilinmeyen_core_alani_reddedilir()
    {
        var yaml = MinimalParser + """

            map:
              core:
                source_ip: "{{ a }}"
            """;

        var result = ParserYamlLoader.Load(yaml);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Bilinmeyen `map.core` alanı 'source_ip'", StringComparison.Ordinal));
    }

    [Fact]
    public void Sabit_sayi_map_degerinde_sayi_kalir()
    {
        var yaml = MinimalParser + """

            map:
              ocsf:
                class_uid: 4001
                name: "network"
            """;

        var map = ParserYamlLoader.Load(yaml).Value.Map;

        Assert.Equal(4001L, Assert.IsType<LiteralMapValue>(map.Ocsf["class_uid"]).Value);
        Assert.Equal("network", Assert.IsType<LiteralMapValue>(map.Ocsf["name"]).Value);
    }

    [Fact]
    public void Sablon_ve_tablo_degerleri_ayirt_edilir()
    {
        var yaml = MinimalParser + """

            map:
              core:
                src_ip: "{{ srcip }}"
              ocsf:
                activity_id: { from: action, table: ocsf_network_activity, default: 99 }
            """;

        var map = ParserYamlLoader.Load(yaml).Value.Map;

        var template = Assert.IsType<TemplateMapValue>(map.Core["src_ip"]);
        Assert.Equal(["srcip"], template.Fields);

        var lookup = Assert.IsType<LookupMapValue>(map.Ocsf["activity_id"]);
        Assert.Equal("action", lookup.From);
        Assert.Equal("ocsf_network_activity", lookup.Table);
        Assert.Equal(99L, lookup.Default);
    }

    [Fact]
    public void Gecersiz_parser_id_reddedilir()
    {
        var result = ParserYamlLoader.Load(
            MinimalParser.Replace("acme.router.syslog", "Acme Router", StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Geçersiz parser id", StringComparison.Ordinal));
    }

    [Fact]
    public void Gecersiz_surum_reddedilir()
    {
        var result = ParserYamlLoader.Load(
            MinimalParser.Replace("version: 1.0.0", "version: v1", StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Geçersiz sürüm", StringComparison.Ordinal));
    }

    [Fact]
    public void Bilinmeyen_adim_tipi_reddedilir()
    {
        var result = ParserYamlLoader.Load(
            MinimalParser.Replace("  - kv: { field: message }", "  - mutate: { field: message }", StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Bilinmeyen adım tipi 'mutate'", StringComparison.Ordinal));
    }

    [Fact]
    public void Bilinmeyen_saat_dilimi_reddedilir()
    {
        var yaml = MinimalParser.Replace(
            "  - kv: { field: message }",
            "  - date: { field: ts, formats: [\"ISO8601\"], default_timezone: \"Europe/Ankara\" }",
            StringComparison.Ordinal);

        var result = ParserYamlLoader.Load(yaml);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("Bilinmeyen saat dilimi", StringComparison.Ordinal));
    }

    [Fact]
    public void Varsayilan_on_failure_fail_olmali()
    {
        // Dispatcher'ın "ilk ok kazanır" kuralı ancak eşleşmeyen parser'ın açıkça
        // başarısız olmasıyla çalışır; varsayılan `continue` olsaydı her parser
        // her satırı "kısmen ayrıştırdım" diye sahiplenirdi.
        var step = Assert.Single(ParserYamlLoader.Load(MinimalParser).Value.Pipeline);
        Assert.Equal(OnFailure.Fail, step.OnFailure);
    }

    [Fact]
    public void Bos_pipeline_reddedilir()
    {
        var result = ParserYamlLoader.Load(
            MinimalParser.Replace("  - kv: { field: message }", "  []", StringComparison.Ordinal));

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("boş olamaz", StringComparison.Ordinal));
    }
}
