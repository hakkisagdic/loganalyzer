using System.Globalization;
using LogAnalyzer.Cli;
using LogAnalyzer.Commands;
using LogAnalyzer.Commands.Seeding;
using LogAnalyzer.Parsing.Dispatch;
using LogAnalyzer.Parsing.Grok;
using Microsoft.Extensions.Time.Testing;

namespace LogAnalyzer.UnitTests;

/// <summary>
/// <c>seed golden</c>'in TTL kapısı <b>şimdiye</b> göre ölçüyor, çapaya göre
/// değil.
///
/// <para>
/// ClickHouse <c>TTL ts + 90 DAY</c>'i kendi saatine göre uyguluyor. Kapı
/// <c>--anchor</c>'a göre ölçtüğünde iki yönde yanılıyordu: geçmiş bir çapa,
/// ClickHouse'un hata vermeden sileceği satırları <b>geçiriyordu</b> — kapının
/// var olma sebebi olan sessiz kayıp — gelecek bir çapa ise tabloda kalacak
/// satırları <b>reddediyordu</b>. Aşağıdaki iki yön testi bu iki hâlin her
/// birinde çapaya göre ölçen kapıyla kırmızı yanıyor (ölçüldü).
/// </para>
///
/// <para>
/// Saat <see cref="FakeTimeProvider"/>; hiçbir test duvar saatine bakmıyor.
/// Kuru koşum ClickHouse'a dokunmuyor, gerçek yazım yolu ise
/// <see cref="Unreachable"/>'a bağlanmaya çalışırdı: orada <c>1</c> dönmesi
/// kapının <b>bağlantıdan önce</b> durduğunu gösteriyor.
/// </para>
/// </summary>
public sealed class SeedTtlGateTests
{
    /// <summary>Yayılımın sağ ucu — sabit; "şimdi"yi sahte saat belirliyor.</summary>
    private static readonly DateTimeOffset Anchor = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Migrations = Path.Combine(RepositoryLayout.Root, "db", "clickhouse");

    private static readonly TimeSpan Retention = EventRetention.Read(Migrations);

    /// <summary>
    /// Hiçbir şeyin dinlemediği ayrıcalıklı port. Kapı geçirseydi yükleyici
    /// buraya bağlanmaya çalışır ve test bir bağlantı istisnasıyla düşerdi —
    /// <c>1</c> dönüşü ile karışmayacak bir sonuç.
    /// </summary>
    private const string Unreachable = "Host=127.0.0.1;Port=1;Database=loganalyzer";

    /// <summary>
    /// Üretimin derleyici kurulumu (<c>legacy</c> + <c>loganalyzer-v1</c>
    /// kaplaması), CLI'ın kurduğu yoldan.
    /// </summary>
    private static readonly ParserToolbox Toolbox = ParserToolbox.Create(
        RepositoryLayout.LegacyPatternDirectory,
        Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

    /// <summary>
    /// <b>Geçmiş çapa:</b> şimdi çapadan saklama süresi kadar ileride, yani
    /// 30 günlük yayılımın satırları ClickHouse'un saatinde çoktan süresi
    /// dolmuş. Çapaya göre ölçen kapı bunların hepsini geçiriyordu.
    /// Kuru koşum da aynı cevabı vermeli — yüklemeden önce "tutuyor mu"
    /// sorusunun Docker'sız cevabı o.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gecmis_capa_ttl_disindaki_satirlari_yazmadan_once_reddediyor(bool dryRun)
    {
        var clock = new FakeTimeProvider(Anchor + Retention);
        var request = Request(Anchor, TimeSpan.FromDays(30), dryRun);

        var exit = await SeedCommandHandlers.Golden(
            request, Toolbox, clock, TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
    }

    /// <summary>
    /// <b>Çapa şimdiye yakın:</b> ürünün varsayılanı (<c>--anchor</c>
    /// verilmezse çapa şimdi). Kapı burada hiçbir satırı reddetmemeli.
    /// </summary>
    [Fact]
    public async Task Simdiye_yakin_capa_geciyor()
    {
        var clock = new FakeTimeProvider(Anchor + TimeSpan.FromHours(1));
        var request = Request(Anchor, TimeSpan.FromDays(30), dryRun: true);

        var exit = await SeedCommandHandlers.Golden(
            request, Toolbox, clock, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    /// <summary>
    /// <b>Gelecek çapa, uzun yayılım:</b> şimdi çapadan 20 gün geride ve
    /// yayılım 100 gün, yani en eski satır şimdiden 80 gün önce — saklama
    /// süresinin içinde, ClickHouse onu tutuyor. Çapaya göre ölçen kapı en eski
    /// yazılabilir anı <c>çapa − 90 gün</c> = <c>şimdi − 70 gün</c> sanıyor ve
    /// aradaki on günün satırlarını reddediyordu.
    /// </summary>
    [Fact]
    public async Task Gelecek_capa_tabloda_kalacak_satirlari_reddetmiyor()
    {
        var clock = new FakeTimeProvider(Anchor - TimeSpan.FromDays(20));
        var request = Request(Anchor, TimeSpan.FromDays(100), dryRun: true);

        // Önşart: plan gerçekten çapa − saklama anından ESKİ satır taşıyor.
        // Taşımasaydı test çapaya göre ölçen kapıyla da yeşil kalırdı.
        Assert.Contains(PlanOf(request), occurrence => occurrence.At < Anchor - Retention);

        var exit = await SeedCommandHandlers.Golden(
            request, Toolbox, clock, TestContext.Current.CancellationToken);

        Assert.Equal(0, exit);
    }

    /// <summary>
    /// <b>Sınır anı.</b> Saat, planın en eski satırı tam <c>şimdi − saklama</c>
    /// anına denk gelecek şekilde kuruluyor: o satır kalıyor
    /// (<c>SampleClock.Survives</c>, <c>&gt;=</c>). Bir
    /// saniye sonra aynı satırın süresi dolmuş.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task Sinir_aninda_en_eski_satir_kaliyor_bir_saniye_sonra_kalmiyor(
        int secondsPastBoundary,
        int expectedExit)
    {
        var request = Request(Anchor, TimeSpan.FromDays(30), dryRun: true);
        var oldest = PlanOf(request)[0].At;
        var clock = new FakeTimeProvider(oldest + Retention + TimeSpan.FromSeconds(secondsPastBoundary));

        var exit = await SeedCommandHandlers.Golden(
            request, Toolbox, clock, TestContext.Current.CancellationToken);

        Assert.Equal(expectedExit, exit);
    }

    /// <summary>
    /// Hata cümlesi <b>gerçek</b> en eski yazılabilir anı basıyor — şimdiden
    /// türeyeni, çapadan türeyeni değil. Operatörün düzelteceği şey o sayıya
    /// bakarak seçiliyor.
    /// </summary>
    [Fact]
    public void Hata_cumlesi_simdiden_turetilen_en_eski_ani_basiyor()
    {
        var now = Anchor + TimeSpan.FromDays(45);
        var earliest = now - Retention;

        IReadOnlyList<PlannedOccurrence> plan =
        [
            new(0, 0, earliest - TimeSpan.FromSeconds(1)),
            new(0, 1, earliest),
            new(0, 2, Anchor),
        ];

        var error = SeedCommandHandlers.ExpiredRowsError(plan, now, Retention);

        Assert.NotNull(error);
        Assert.Contains("3 satırın 1 tanesi", error, StringComparison.Ordinal);
        Assert.Contains(
            earliest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z",
            error,
            StringComparison.Ordinal);

        // Sınırdaki satır tek başına geçiyor.
        Assert.Null(SeedCommandHandlers.ExpiredRowsError([plan[1], plan[2]], now, Retention));
    }

    private static SeedGoldenRequest Request(DateTimeOffset anchor, TimeSpan span, bool dryRun) => new(
        Catalog: RepositoryLayout.CatalogParserDirectory,
        MaskFile: RepositoryLayout.MaskFile,
        Migrations: Migrations,
        ConnectionString: Unreachable,
        OwnerGroup: "golden-ttl-gate",
        Plan: new SeedPlanOptions(anchor, span, TotalEvents: 200, ZipfExponent: 2.0, Seed: 39),
        BatchRows: 100,
        Replace: false,
        DryRun: dryRun);

    /// <summary>
    /// Yükleyicinin kuracağı planın aynısı — aynı derleyici, aynı sözlük, aynı
    /// tohum. Ayrışırsa sınır testi sessiz kalmıyor: en eski satır kayar ve
    /// iki sınır vakasından biri kırmızı yanar.
    /// </summary>
    private static IReadOnlyList<PlannedOccurrence> PlanOf(SeedGoldenRequest request)
    {
        var samples = GoldenSampleSeeder.ReadSamples(request.Catalog);
        var catalog = new ParserCatalog();
        Assert.Empty(catalog.LoadFromDirectory(request.Catalog, Toolbox.Compiler).Errors);

        var seeder = new GoldenSampleSeeder(
            new Dispatcher(catalog, new DispatchStats()),
            MaskCatalog.LoadFromFile(request.MaskFile));

        var plan = GoldenSamplePlan.Build(
            seeder.Signatures(samples), GoldenSampleSeeder.Vendors(samples), request.Plan);

        Assert.NotEmpty(plan);
        return plan;
    }
}
