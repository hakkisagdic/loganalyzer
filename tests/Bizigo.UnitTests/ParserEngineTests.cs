using Bizigo.Contracts;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

public sealed class ParserEngineTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static readonly MappingTableCatalog Tables =
        MappingTableCatalog.LoadFromDirectory(Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    private static CompiledParser Build(string pipelineAndMap)
    {
        var yaml = $"""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata:
              id: test.engine.parser
              version: 1.0.0
            {pipelineAndMap}
            tests:
              - name: yer tutucu
                input: 'x'
                expect:
                  parse_status: failed
            """;

        var loaded = ParserYamlLoader.Load(yaml);
        Assert.True(loaded.Ok, loaded.Describe());

        var compiler = new ParserCompiler(new GrokCompiler(Library), Tables);
        var compiled = compiler.Compile(loaded.Value);
        Assert.True(compiled.Ok, string.Join("; ", compiled.Errors.Select(e => e.Message)));

        return compiled.Value;
    }

    // -------------------------------------------------------------------- kv

    [Fact]
    public void Kv_tirnakli_degeri_bolmez()
    {
        // FortiGate `msg="Denied by policy"` yazar. Naif Split(' ') alanı sessizce bozar.
        var parser = Build("""
            pipeline:
              - kv: { field: message, separator: " ", assign: "=" }
            """);

        var result = parser.Parse("""action=deny msg="Denied by policy" srcip=10.0.0.1""");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal("Denied by policy", result.Fields["msg"]);
        Assert.Equal("10.0.0.1", result.Fields["srcip"]);
    }

    [Fact]
    public void Kv_include_exclude_uygulanir()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message, include: [a, b] }
            """);

        var result = parser.Parse("a=1 b=2 c=3");

        Assert.True(result.Fields.ContainsKey("a"));
        Assert.True(result.Fields.ContainsKey("b"));
        Assert.False(result.Fields.ContainsKey("c"));
    }

    [Fact]
    public void Kv_hicbir_cift_bulamazsa_basarisiz_olur()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
            """);

        var result = parser.Parse("burada anahtar değer yok");

        Assert.Equal(ParseStatus.Failed, result.Status);
    }

    // ------------------------------------------------------------------ json

    [Fact]
    public void Json_ic_ice_nesneyi_duzlestirir()
    {
        var parser = Build("""
            pipeline:
              - json: { field: message }
            """);

        var result = parser.Parse("""{"level":"warn","http":{"status":404},"ok":false}""");

        Assert.Equal("warn", result.Fields["level"]);
        Assert.Equal(404L, result.Fields["http.status"]);
        Assert.Equal(false, result.Fields["ok"]);
    }

    [Fact]
    public void Bozuk_json_basarisiz_olur()
    {
        var parser = Build("""
            pipeline:
              - json: { field: message }
            """);

        Assert.Equal(ParseStatus.Failed, parser.Parse("{bozuk").Status);
    }

    // ------------------------------------------------------------------- csv

    [Fact]
    public void Csv_tirnakli_alani_ve_kacisi_cozer()
    {
        var parser = Build("""
            pipeline:
              - csv: { field: message, columns: [ts, host, msg], separator: "," }
            """);

        var result = parser.Parse("""2026-08-16,fw01,"virgül, ve ""tırnak"" içeren mesaj" """.TrimEnd());

        Assert.Equal("fw01", result.Fields["host"]);
        Assert.Equal("""virgül, ve "tırnak" içeren mesaj""", result.Fields["msg"]);
    }

    [Fact]
    public void Csv_eksik_kolonda_basarisiz_olur()
    {
        var parser = Build("""
            pipeline:
              - csv: { field: message, columns: [a, b, c] }
            """);

        Assert.Equal(ParseStatus.Failed, parser.Parse("1,2").Status);
    }

    // ------------------------------------------------------------------ date

    [Fact]
    public void Date_unix_ms_cozer()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date: { field: eventtime, formats: ["UNIX_MS"] }
            """);

        var result = parser.Parse("eventtime=1786000000000");

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1786000000000), result.Timestamp);
    }

    [Fact]
    public void Date_yerel_saati_verilen_dilime_gore_cozer()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date: { field: ts, formats: ["yyyy-MM-dd HH:mm:ss"], default_timezone: "Europe/Istanbul" }
            """);

        var result = parser.Parse("""ts="2026-01-15 10:00:00" """.TrimEnd());

        Assert.NotNull(result.Timestamp);
        Assert.Equal(TimeSpan.FromHours(3), result.Timestamp!.Value.Offset);
        Assert.Equal(new DateTime(2026, 1, 15, 7, 0, 0, DateTimeKind.Utc), result.Timestamp.Value.UtcDateTime);
    }

    [Fact]
    public void Date_cihazin_verdigi_dilimi_tercih_eder()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date:
                  field: ts
                  formats: ["yyyy-MM-dd HH:mm:ss"]
                  timezone_field: tz
                  default_timezone: "UTC"
            """);

        var result = parser.Parse("""ts="2026-01-15 10:00:00" tz=Europe/Istanbul""");

        Assert.Equal(TimeSpan.FromHours(3), result.Timestamp!.Value.Offset);
    }

    [Fact]
    public void Syslog_zaman_damgasi_gelecege_dusmez()
    {
        // RFC3164 yıl taşımaz. Yılbaşı gecesi gelen 31 Aralık logunu 11 ay ileri
        // yazmak, "son 15 dakika" sorgusunu sessizce boşaltır.
        var parser = Build("""
            pipeline:
              - grok: { field: message, patterns: ["^%{SYSLOGTIMESTAMP:ts}$"] }
              - date: { field: ts, formats: ["SYSLOG"], default_timezone: "UTC" }
            """);

        var future = DateTimeOffset.UtcNow.AddMonths(3);
        var result = parser.Parse(future.ToString("MMM d HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotNull(result.Timestamp);
        Assert.True(result.Timestamp!.Value <= DateTimeOffset.UtcNow.AddDays(1),
            $"Zaman damgası geleceğe düştü: {result.Timestamp}");
    }

    // --------------------------------------------------------- convert / drop

    [Fact]
    public void Convert_ve_drop_calisir()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - convert: { fields: { port: int, bytes: long } }
              - drop: { fields: [secret] }
            """);

        var result = parser.Parse("port=53 bytes=1200 secret=abc");

        Assert.Equal(53, result.Fields["port"]);
        Assert.Equal(1200L, result.Fields["bytes"]);
        Assert.False(result.Fields.ContainsKey("secret"));
    }

    // ------------------------------------------------------------ on_failure

    [Fact]
    public void On_failure_continue_partial_uretir()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - grok: { field: message, patterns: ["^ASLA_ESLESMEZ$"], on_failure: continue }
            """);

        var result = parser.Parse("a=1");

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal("1", result.Fields["a"]);
    }

    [Fact]
    public void On_failure_tag_etiket_ekler()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - grok: { field: message, patterns: ["^ASLA$"], on_failure: tag, tag: _fg_grok_failure }
            """);

        var result = parser.Parse("a=1");

        Assert.Equal(ParseStatus.Partial, result.Status);
        Assert.Equal(["_fg_grok_failure"], result.Tags);
    }

    [Fact]
    public void On_failure_fail_boru_hattini_durdurur()
    {
        var parser = Build("""
            pipeline:
              - grok: { field: message, patterns: ["^ASLA$"] }
              - kv: { field: message }
            """);

        var result = parser.Parse("a=1");

        Assert.Equal(ParseStatus.Failed, result.Status);
        Assert.False(result.Fields.ContainsKey("a"));   // ikinci adım hiç koşmadı
        Assert.Empty(result.Core);
    }

    // -------------------------------------------------------------------- map

    [Fact]
    public void Cozulemeyen_sablon_atanmaz()
    {
        // Boş string yazmak, olayda "kaynak IP boş" gibi görünüp sorguları kirletir.
        var parser = Build("""
            pipeline:
              - kv: { field: message }
            map:
              core:
                src_ip: "{{ srcip }}"
                dst_ip: "{{ dstip }}"
            """);

        var result = parser.Parse("srcip=10.0.0.1");

        Assert.Equal("10.0.0.1", result.Core["src_ip"]);
        Assert.False(result.Core.ContainsKey("dst_ip"));
    }

    [Fact]
    public void Tek_yer_tutuculu_sablon_tipi_korur()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - convert: { fields: { dstport: int } }
            map:
              core:
                dst_port: "{{ dstport }}"
            """);

        Assert.Equal(53, parser.Parse("dstport=53").Core["dst_port"]);
    }

    [Fact]
    public void Karisik_sablon_birlestirir()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
            map:
              core:
                host: "{{ site }}-{{ device }}"
            """);

        Assert.Equal("ist-fw01", parser.Parse("site=ist device=fw01").Core["host"]);
    }

    [Fact]
    public void Esleme_tablosu_cozer()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
            map:
              ocsf:
                class_uid: 4001
                activity_id: { from: action, table: ocsf_network_activity, default: 99 }
            """);

        Assert.Equal(6L, parser.Parse("action=accept").Ocsf["activity_id"]);
        Assert.Equal(5L, parser.Parse("action=DROP").Ocsf["activity_id"]);
        Assert.Equal(99L, parser.Parse("action=bilinmeyen").Ocsf["activity_id"]);
    }

    [Fact]
    public void Bilinmeyen_esleme_tablosu_derleme_zamaninda_yakalanir()
    {
        var yaml = """
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata:
              id: test.unknown.table
              version: 1.0.0
            pipeline:
              - kv: { field: message }
            map:
              ocsf:
                activity_id: { from: action, table: yok_boyle_bir_tablo }
            tests:
              - name: t
                input: 'a=1'
                expect: { parse_status: ok }
            """;

        var compiler = new ParserCompiler(new GrokCompiler(Library), Tables);
        var compiled = compiler.Compile(ParserYamlLoader.Load(yaml).Value);

        Assert.False(compiled.Ok);
        Assert.Contains(compiled.Errors, e => e.Message.Contains("bilinmeyen eşleme tablosu", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- önbellek

    [Fact]
    public void Ayni_id_ve_surum_onbellekten_gelir()
    {
        var compiler = new ParserCompiler(new GrokCompiler(Library), Tables);
        var definition = ParserYamlLoader.Load("""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.cache, version: 1.0.0 }
            pipeline:
              - kv: { field: message }
            tests:
              - name: t
                input: 'a=1'
                expect: { parse_status: ok }
            """).Value;

        Assert.Same(compiler.Compile(definition).Value, compiler.Compile(definition).Value);
    }

    [Fact]
    public void Farkli_surum_ayri_derlenir()
    {
        // Replay sırasında aynı id'nin iki sürümü aynı süreçte koşar. Önbellek
        // sürümü unutursa düzeltilmiş parser eski pattern'lerle çalışır.
        var compiler = new ParserCompiler(new GrokCompiler(Library), Tables);

        static string Yaml(string version, string assign) => $$"""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata: { id: test.cache, version: {{version}} }
            pipeline:
              - kv: { field: message, assign: "{{assign}}" }
            tests:
              - name: t
                input: 'a=1'
                expect: { parse_status: ok }
            """;

        var v1 = compiler.Compile(ParserYamlLoader.Load(Yaml("1.0.0", "=")).Value).Value;
        var v2 = compiler.Compile(ParserYamlLoader.Load(Yaml("2.0.0", ":")).Value).Value;

        Assert.NotSame(v1, v2);
        Assert.Equal("1", v1.Parse("a=1").Fields["a"]);
        Assert.Equal("1", v2.Parse("a:1").Fields["a"]);
    }
}
