using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.UnitTests;

/// <summary>
/// Cisco ASA parser'ının kendi adres pattern'i (<c>ASA_IP</c>) için kâhin testi.
///
/// <para>
/// <b>Neden parser'a özel bir adres pattern'i var:</b> ASA'nın 302013–302016 ve
/// 302020/302021 gövdeleri üç-dört <c>%{IP}</c> referansı taşıyor ve <c>IP</c>
/// upstream'de <c>%{IPV6}|%{IPV4}</c>'e açılıyor. Upstream <c>IPV6</c> her
/// <c>::</c> konumunu ayrı ayrı sayan devasa bir alternasyon; dört kopyası
/// <c>NonBacktracking</c> otomatını düğüm bütçesinin (10000) üstüne çıkarıyor
/// (ölçüldü: 16060 ve 11695). Pattern geri izlemeli motora düşüyor ve 50 ms
/// zaman aşımına tabi kalıyor — kataloğun kalan tek zaman aşımı maruziyeti.
/// </para>
///
/// <para>
/// Çözüm, <c>%{IP}</c> yerine ASA parser'ının <c>pattern_definitions</c>
/// bloğunda tanımlı daha dar bir adres pattern'i. Dar olmak <b>davranış kaybı
/// riski</b> demek, o yüzden burada upstream <c>IPV6</c> kâhin olarak
/// kullanılıyor: aynı girdi ikisine de veriliyor ve sonuç karşılaştırılıyor.
/// </para>
/// </summary>
public sealed class CiscoAsaAddressPatternTests
{
    /// <summary>
    /// Ölçüm <c>bizigo-v1</c> kaplaması uygulanmış kütüphaneyle yapılıyor:
    /// <c>ASA_IP</c> içindeki <c>%{IPV4}</c> legacy'den gelirse lookaround'u
    /// miras alır ve pattern zaten doğrusal derlenemez. Kaplamanın varsayılan
    /// yola bağlanması ayrı bir adım; bu test hedef durumu ölçüyor.
    /// </summary>
    private static readonly GrokPatternLibrary Legacy =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory)
            .With(OverlayEntries());

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

    /// <summary>ASA parser'ının YAML'ındaki tanımın aynısı — tek kaynak orada.</summary>
    private static string AsaIpBody => AsaPatternDefinition("ASA_IP");

    private static string AsaPatternDefinition(string name)
    {
        var path = Path.Combine(RepositoryLayout.CatalogParserDirectory, "cisco.asa", "network.yaml");
        var loaded = ParserYamlLoader.LoadFile(path);

        Assert.True(loaded.Ok, loaded.Describe());
        Assert.True(loaded.Value.PatternDefinitions.TryGetValue(name, out var body),
            $"'{name}' tanımı network.yaml içinde yok.");

        return body!;
    }

    private static GrokCompiler AsaCompiler() =>
        new GrokCompiler(Legacy).With(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASA_IP"] = AsaIpBody,
        });

    private static string? Capture(GrokCompiler compiler, string expression, string input)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var result = compiler.Compile(expression).Match(input, fields);

        return result.Matched && fields.TryGetValue("a", out var value) ? value as string : null;
    }

    /// <summary>
    /// Gerçek cihaz çıktısında görülen ve görülebilecek adres biçimleri.
    /// <c>%{IP}</c> ne yakalıyorsa <c>%{ASA_IP}</c> de aynısını yakalamalı.
    /// </summary>
    [Theory]
    // IPv4
    [InlineData("192.168.205.104")]
    [InlineData("10.10.10.10")]
    [InlineData("255.255.255.255")]
    // IPv4-eşlenmiş IPv6 — ASA 302020 satırlarının gerçek hâli
    [InlineData("::ffff:10.10.4.4")]
    [InlineData("::ffff:192.168.2.2")]
    // sıkıştırılmış IPv6
    [InlineData("2001:db8:85a3::8a2e:370:7334")]
    [InlineData("2001:470:1:c84::24")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    [InlineData("::")]
    // tam yazım
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    // baştan sıkıştırma
    [InlineData("::8a2e:370:7334")]
    // sondan sıkıştırma
    [InlineData("2001:db8::")]
    public void Asa_ip_upstream_ip_ile_ayni_adresi_yakaliyor(string address)
    {
        var upstream = Capture(new GrokCompiler(Legacy), "^%{IP:a}$", address);
        var asa = Capture(AsaCompiler(), "^%{ASA_IP:a}$", address);

        Assert.Equal(address, upstream);
        Assert.Equal(upstream, asa);
    }

    /// <summary>
    /// Sorun <c>%{IP}</c>'nin TEK BAŞINA doğrusal derlenememesi değil — kaplama
    /// uygulandıktan sonra tek kopyası sorunsuz derleniyor. Sorun, aynı gövdede
    /// üç-dört kopyasının otomat düğüm bütçesini birlikte aşması. O yüzden asıl
    /// ölçüt <see cref="Asa_govde_pattern_leri_dogrusal_motorla_derleniyor"/>;
    /// burada yalnızca dar pattern'in kendisinin sağlam olduğu doğrulanıyor.
    /// </summary>
    [Fact]
    public void Asa_ip_dogrusal_motorla_derleniyor()
    {
        var compiled = AsaCompiler().Compile("^%{ASA_IP:a}$");

        Assert.True(compiled.IsLinearTime, compiled.FallbackReason);
    }

    /// <summary>
    /// Asıl kazanç: <c>ASA_IP</c> kullanan iki büyük gövde pattern'i artık
    /// doğrusal motorda. Bu, kataloğun kalan tek zaman aşımı maruziyetini
    /// kapatıyor.
    /// </summary>
    [Theory]
    [InlineData("CISCOFW302013_302014_302015_302016")]
    [InlineData("CISCOFW302020_302021")]
    public void Asa_govde_pattern_leri_dogrusal_motorla_derleniyor(string name)
    {
        var compiler = new GrokCompiler(Legacy).With(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASA_IP"] = AsaIpBody,
            ["CISCO_REASON"] = AsaPatternDefinition("CISCO_REASON"),
            [name] = AsaPatternDefinition(name),
        });

        var compiled = compiler.Compile("^%{" + name + "}");

        Assert.True(compiled.IsLinearTime, compiled.FallbackReason);
    }
}
