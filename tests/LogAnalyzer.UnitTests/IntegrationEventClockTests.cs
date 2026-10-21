using System.Text.RegularExpressions;

namespace LogAnalyzer.UnitTests;

/// <summary>
/// <b><c>events</c>'e yazan hiçbir entegrasyon testi takvime çakılmış bir an
/// kurmuyor.</b>
///
/// <para>
/// <c>events</c> tablosunda <c>TTL toDateTime(ts) + INTERVAL 90 DAY</c> var ve
/// ClickHouse onu <b>kendi saatiyle</b> uyguluyor. Sabit bir tarihle yazan test
/// yazıldığı gün geçiyor; doksan gün sonra ClickHouse satırı hata vermeden kabul
/// edip siliyor ve test sebebini söylemeyen bir sayıyla düşüyor. Bu depoda
/// önce <c>GoldenSeedClickHouseTests</c> ("29 gün") ısırdı, sonra aynı kusur on
/// dört sınıfta daha bulundu (2026-11-14'ten itibaren birer birer kırmızı).
/// Hepsi artık anlarını <c>EventTestClock</c>'tan alıyor.
/// </para>
///
/// <para>
/// <b>Neden birim paketinde:</b> entegrasyon paketi konteyner istiyor ve
/// ajanlar onu koşturmuyor (§2), dolayısıyla bomba ancak patladığında
/// görünürdü — yine sebebini söylemeden. Bu bekçi kaynak dosyayı okuyor,
/// konteyner istemiyor ve kusurun <b>geri girdiği</b> commit'te kırmızı yanıyor.
/// Kalıp <c>ScopeCoverageTests</c>'ten: birim paketi entegrasyon paketine
/// referans vermiyor, dosyayı okuyor.
/// </para>
///
/// <para>
/// <b>Kapsamı:</b> <c>events</c>'e satır yazan dosyalar
/// (<c>WriteEventsAsync</c>, <c>WriteEventsToAsync</c>,
/// <c>ClickHouseEventSink</c>). <c>change_events</c>'te TTL yok, ona yazan
/// <c>WriteChangeEventsAsync</c> bilerek dışarıda. Başka bir dosyadaki
/// yardımcının yazdığı satırları göremiyor; o yardımcı da anını
/// <c>EventTestClock</c>'tan ya da <c>SampleClock.Anchor()</c>'dan almalı.
/// </para>
/// </summary>
public sealed partial class IntegrationEventClockTests
{
    private static string IntegrationDirectory =>
        Path.Combine(RepositoryLayout.Root, "tests", "LogAnalyzer.IntegrationTests");

    /// <summary><c>events</c>'e satır yazan çağrılar.</summary>
    [GeneratedRegex(@"\b(?:WriteEventsAsync|WriteEventsToAsync|ClickHouseEventSink)\b")]
    private static partial Regex WritesEvents();

    /// <summary>
    /// Takvime çakılmış an: yıl-ay ile başlayan kurucu (hedef tipli
    /// <c>new(2026, 8, …)</c> dahil), yıl ile başlayan bir metnin ayrıştırılması,
    /// ya da sabit bir Unix zamanı.
    /// </summary>
    [GeneratedRegex(
        @"new\s*(?:DateTimeOffset|DateTime)?\s*\(\s*(?:19|20)\d{2}\s*,\s*\d{1,2}\s*,"
        + @"|DateTime(?:Offset)?\.Parse(?:Exact)?\(\s*""(?:19|20)\d{2}-"
        + @"|FromUnixTime(?:Seconds|Milliseconds)\(\s*\d{9,}")]
    private static partial Regex CalendarInstant();

    private static IReadOnlyList<string> EventWriters()
    {
        if (!Directory.Exists(IntegrationDirectory))
        {
            throw new DirectoryNotFoundException(
                "Entegrasyon test dizini bulunamadı. Taşındıysa bu bekçinin yolu da " +
                $"güncellenmeli — yoksa bekçi sessizce hiçbir şey denetlemez: {IntegrationDirectory}");
        }

        return Directory
            .EnumerateFiles(IntegrationDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(static file => !IsBuildOutput(file))
            .Where(static file => WritesEvents().IsMatch(File.ReadAllText(file)))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string file)
    {
        var relative = Path.GetRelativePath(IntegrationDirectory, file);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    /// <summary>
    /// Asıl bekçi. Kırmızı yandığında mesaj dosyayı, satırı ve satırın kendisini
    /// veriyor; düzeltme o anı <c>EventTestClock.Day</c>'den türetmek.
    /// </summary>
    [Fact]
    public void Events_e_yazan_entegrasyon_testleri_takvime_cakili_an_kurmuyor()
    {
        var writers = EventWriters();

        // Keşif boş dönerse bekçi hiçbir şey denetlemeden yeşil kalırdı. TTL'in
        // kendisini sınayan sınıf kümede olmak zorunda; yoksa keşif kırılmıştır.
        Assert.Contains(writers, static file => Path.GetFileName(file) == "EventRetentionChainTests.cs");

        var offenders = writers
            .SelectMany(static file => File.ReadLines(file)
                .Select(static (line, index) => (Line: line, Number: index + 1))
                .Where(static entry => CalendarInstant().IsMatch(entry.Line))
                .Select(entry =>
                    $"{Path.GetRelativePath(RepositoryLayout.Root, file)}:{entry.Number}: {entry.Line.Trim()}"))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "events'e yazan entegrasyon testlerinde takvime çakılmış an var. ClickHouse 90 günlük TTL'i " +
            "kendi saatiyle uyguluyor: bu satırlar tarihleri geçtikten doksan gün sonra hata vermeden " +
            "silinir ve test sebebini söylemeden düşer. Anı EventTestClock.Day'den türet " +
            "(sink saati ile ClickHouse saatinin aynı olması gerekiyorsa SampleClock.Anchor()):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Kalıbın yakalaması gereken biçimler — bekçinin kör olmadığının kanıtı.</summary>
    [Theory]
    [InlineData("private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);")]
    [InlineData("var ts = new DateTimeOffset(2026, 8, 16, 12, 30, 0, TimeSpan.Zero);")]
    [InlineData("new DateTimeOffset(2026, 8, 17, 9, 30, 15, TimeSpan.Zero) + TimeSpan.FromTicks(7_431_002);")]
    [InlineData("var day = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);")]
    [InlineData("var at = DateTimeOffset.Parse(\"2026-08-17T10:00:00Z\", CultureInfo.InvariantCulture);")]
    [InlineData("var at = DateTimeOffset.FromUnixTimeSeconds(1789963200);")]
    public void Kalip_takvime_cakili_ani_taniyor(string line) =>
        Assert.Matches(CalendarInstant(), line);

    /// <summary>Kalıbın yakalamaması gereken biçimler — bekçinin gürültü üretmediğinin kanıtı.</summary>
    [Theory]
    [InlineData("private static readonly DateTimeOffset Now = EventTestClock.Day.AddHours(12);")]
    [InlineData("EventTestClock.Day + new TimeSpan(9, 30, 15) + TimeSpan.FromTicks(7_431_002);")]
    [InlineData("var buffer = new List<int>(2026);")]
    [InlineData("RawRef = \"raw/net-core/2026/08/17/10/default/01J0.ndjson.zst\",")]
    [InlineData("/// Sabit bir tarih (eskiden 2026-08-17) TTL'e takılırdı.")]
    public void Kalip_turetilmis_ani_yakalamiyor(string line) =>
        Assert.DoesNotMatch(CalendarInstant(), line);
}
