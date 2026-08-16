using System.Diagnostics;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Kabul kriteri: <b>rastgele pattern → derlenir veya anlamlı hata verir.</b>
///
/// <para>
/// Buradaki asıl iddia "derleyici her şeyi anlar" değil. İddia şu: bir parser
/// YAML'ı ne kadar bozuk olursa olsun, motor ya çalışır ya da <b>tek bir hata
/// tipiyle</b> reddeder. <see cref="NullReferenceException"/>, sonsuz döngü veya
/// yakalanmamış <see cref="IndexOutOfRangeException"/> ingest sürecini düşürür;
/// <see cref="GrokCompilationException"/> yalnızca o parser'ı reddeder.
/// </para>
/// </summary>
public sealed class GrokPropertyTests
{
    private static readonly GrokPatternLibrary Library =
        GrokPatternLibrary.LoadFromDirectory(RepositoryLayout.LegacyPatternDirectory);

    private static readonly string[] Names = [.. Library.Names];

    private const string Alphabet = @"abc019 .*+?[]{}()|^$\-:%<>'""!#,/=~@&;_" + "\t";

    [Fact]
    public void Rastgele_pattern_ya_derlenir_ya_anlamli_hata_verir()
    {
        var compiler = new GrokCompiler(Library);
        var random = new Random(20260816);   // sabit tohum: kırılan durum yeniden üretilebilsin
        var compiled = 0;
        var rejected = 0;

        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var pattern = Generate(random);

            try
            {
                var grok = compiler.Compile(pattern);
                compiled++;

                // Derlendiyse çalıştırılabilir de olmalı; eşleşme sonucu önemsiz,
                // önemli olan burada patlamaması.
                var stopwatch = Stopwatch.StartNew();
                grok.Match("test 10.0.0.1 GET /x 200", new Dictionary<string, object?>(StringComparer.Ordinal));
                stopwatch.Stop();

                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                    $"Eşleşme {stopwatch.ElapsedMilliseconds} ms sürdü. Pattern: {pattern}");
            }
            catch (GrokCompilationException ex)
            {
                rejected++;
                Assert.False(string.IsNullOrWhiteSpace(ex.Message), $"Hata mesajı boş. Pattern: {pattern}");
            }
#pragma warning disable CA1031 // Testin amacı tam olarak "başka hiçbir istisna tipi çıkmamalı".
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Assert.Fail(
                    $"Beklenmeyen istisna tipi {ex.GetType().Name}: {ex.Message}{Environment.NewLine}Pattern: {pattern}");
            }
        }

        // Üretim gerçekten iki tarafa da düşmeli; hepsi reddedilseydi test boş geçerdi.
        Assert.True(compiled > 100, $"Yalnızca {compiled} pattern derlendi — üreteç çok bozuk pattern üretiyor.");
        Assert.True(rejected > 100, $"Yalnızca {rejected} pattern reddedildi — üreteç yeterince zorlamıyor.");
    }

    [Fact]
    public void Rastgele_gecerli_referans_dizileri_daima_derlenir()
    {
        var compiler = new GrokCompiler(Library);
        var random = new Random(4242);

        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var count = random.Next(1, 5);
            var parts = new List<string>(count);

            for (var i = 0; i < count; i++)
            {
                var name = Names[random.Next(Names.Length)];
                parts.Add(random.Next(2) == 0 ? $"%{{{name}}}" : $"%{{{name}:alan{i}}}");
            }

            var pattern = string.Join(" ", parts);

            var grok = compiler.Compile(pattern);
            Assert.NotNull(grok.RegexSource);
        }
    }

    private static string Generate(Random random)
    {
        var length = random.Next(1, 40);
        var buffer = new char[length];

        for (var i = 0; i < length; i++)
        {
            buffer[i] = Alphabet[random.Next(Alphabet.Length)];
        }

        var pattern = new string(buffer);

        // Vakaların bir kısmına gerçek bir grok referansı serpiştir; saf rastgele
        // metin neredeyse hep düzenli ifade hatasına düşer ve referans yolunu hiç
        // sınamaz.
        if (random.Next(3) == 0)
        {
            var name = Names[random.Next(Names.Length)];
            var insertAt = random.Next(pattern.Length + 1);
            pattern = pattern[..insertAt] + $"%{{{name}:f}}" + pattern[insertAt..];
        }

        return pattern;
    }
}
