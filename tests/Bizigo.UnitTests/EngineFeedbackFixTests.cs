using Bizigo.Contracts;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

/// <summary>
/// T08'in gerçek vendor logu yazarken bulduğu üç sessiz hata.
///
/// <para>
/// Üçünün ortak yanı: <b>hiçbiri istisna atmıyordu</b>. Damga saatlerce
/// kayıyordu, yıl 51345 oluyordu, doğru ayrıştırılmış satır <c>failed</c>
/// düşüyordu — hepsi sessizce. Bu yüzden testleri de sonucun kendisine değil,
/// sessizliğin bittiğine bakıyor.
/// </para>
/// </summary>
public sealed class EngineFeedbackFixTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static CompiledParser Build(string pipeline)
    {
        var yaml = $"""
            apiVersion: bizigo.dev/v1
            kind: Parser
            metadata:
              id: test.feedback
              version: 1.0.0
            {pipeline}
            tests:
              - name: yer tutucu
                input: 'x'
                expect:
                  parse_status: failed
            """;

        var loaded = ParserYamlLoader.Load(yaml);
        Assert.True(loaded.Ok, loaded.Describe());

        var compiled = new ParserCompiler(new GrokCompiler(Library)).Compile(loaded.Value);
        Assert.True(compiled.Ok, string.Join("; ", compiled.Errors.Select(e => e.Message)));

        return compiled.Value;
    }

    // ---- Madde 1: timezone_field sayısal ofset ----

    [Theory]
    [InlineData("-0500", -5)]
    [InlineData("+0300", 3)]
    [InlineData("+03:00", 3)]
    [InlineData("-08", -8)]
    [InlineData("Z", 0)]
    [InlineData("+0000", 0)]
    public void Sayisal_ofset_saat_dilimi_cozuluyor(string value, int expectedHours)
    {
        var zone = TimeZoneResolver.Resolve(value);

        Assert.NotNull(zone);
        Assert.Equal(TimeSpan.FromHours(expectedHours), zone.BaseUtcOffset);
    }

    [Theory]
    [InlineData("+9900")]
    [InlineData("-0099")]
    [InlineData("bilinmeyen")]
    [InlineData("+")]
    public void Gecersiz_ofset_cozulmuyor(string value) =>
        Assert.Null(TimeZoneResolver.Resolve(value));

    [Fact]
    public void IANA_kimligi_calismaya_devam_ediyor()
    {
        var zone = TimeZoneResolver.Resolve("Europe/Istanbul");

        Assert.NotNull(zone);
        Assert.Equal(TimeSpan.FromHours(3), zone.BaseUtcOffset);
    }

    [Fact]
    public void Ofset_alandan_okunup_damgaya_uygulaniyor()
    {
        // FortiGate `tz="-0500"` yazıyor. Eskiden çözülemeyip default_timezone'a
        // düşüyordu: 5 saat kaymış damga, `ok` statüsü, hiçbir iz yok.
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date:
                  field: date_time
                  timezone_field: tz
                  default_timezone: UTC
                  formats: ["yyyy-MM-dd HH:mm:ss"]
            """);

        var result = parser.Parse("""date_time="2026-08-16 12:00:00" tz="-0500" """);

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero), result.Timestamp);
        Assert.DoesNotContain("_tz_unresolved", result.Tags);
    }

    [Fact]
    public void Cozulemeyen_saat_dilimi_artik_etiket_birakiyor()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date:
                  field: date_time
                  timezone_field: tz
                  default_timezone: UTC
                  formats: ["yyyy-MM-dd HH:mm:ss"]
            """);

        var result = parser.Parse("""date_time="2026-08-16 12:00:00" tz="Mars/Olympus" """);

        // Varsayılana düşmek meşru; sessizce düşmek değil.
        Assert.Contains("_tz_unresolved", result.Tags);
    }

    // ---- Madde 2: UNIX_NS / UNIX_AUTO ----

    [Fact]
    public void Nanosaniye_damgasi_dogru_cozuluyor()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date: { field: eventtime, formats: ["UNIX_NS"] }
            """);

        // FortiOS 7.x. UNIX_MS ile okunsaydı yıl 51345 çıkardı.
        var result = parser.Parse("eventtime=1702257752722386015");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(2023, result.Timestamp!.Value.Year);
    }

    [Theory]
    [InlineData("1554039772", 2019)]              // saniye (FortiOS 6.x)
    [InlineData("1702257752722", 2023)]           // milisaniye
    [InlineData("1702257752722386", 2023)]        // mikrosaniye
    [InlineData("1702257752722386015", 2023)]     // nanosaniye (FortiOS 7.x)
    public void UNIX_AUTO_olcegi_basamak_sayisindan_cikariyor(string raw, int expectedYear)
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date: { field: eventtime, formats: ["UNIX_AUTO"] }
            """);

        var result = parser.Parse($"eventtime={raw}");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(expectedYear, result.Timestamp!.Value.Year);
    }

    [Fact]
    public void Tasan_damga_reddediliyor()
    {
        // Yıl 51345'e giden bir damga yazmaktansa adım başarısız olmalı.
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - date:
                  field: eventtime
                  formats: ["UNIX_MS"]
                  on_failure: tag
            """);

        var result = parser.Parse("eventtime=99999999999999999");

        Assert.NotEqual(ParseStatus.Ok, result.Status);
    }

    // ---- Madde 6: convert boş listede başarılı ----

    [Fact]
    public void Donusturulecek_alan_yoksa_adim_basarili()
    {
        // ASA 733100: ne port var ne bayt, ama satır tamamen doğru ayrıştırılmış.
        // Eskiden `failed` düşüyordu; `on_failure: continue` de yanlış cevaptı
        // çünkü eksik bir şey yokken satırı `partial` gösterirdi.
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - convert:
                  fields:
                    src_port: int
                    bytes: int
            """);

        var result = parser.Parse("msg=\"drop rate exceeded\"");

        Assert.Equal(ParseStatus.Ok, result.Status);
    }

    [Fact]
    public void Var_olan_alan_hala_donusturuluyor()
    {
        var parser = Build("""
            pipeline:
              - kv: { field: message }
              - convert:
                  fields:
                    src_port: int
                    bytes: int
            """);

        var result = parser.Parse("src_port=41022");

        Assert.Equal(ParseStatus.Ok, result.Status);
        Assert.Equal(41022L, Convert.ToInt64(result.Fields["src_port"], System.Globalization.CultureInfo.InvariantCulture));
    }
}
