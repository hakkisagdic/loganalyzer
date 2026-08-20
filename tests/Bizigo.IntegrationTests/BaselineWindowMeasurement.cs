using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <summary>
/// <b>Baseline penceresi uzunluğunun ölçüm aracı</b> (T35) — sayıyı üretmiyor,
/// üretilebilir kılıyor.
///
/// <para>
/// T35 ticket'ı tabanın uzunluğunu <b>tahminle</b> seçmeyi açıkça yasaklıyor,
/// ve gerekçesi keskin: çok kısa seçilirse her yeni şey "ilk-görülen" olur ve
/// ürün gürültü makinesine döner; çok uzun seçilirse gerçek yenilik gürültüde
/// kaybolur. İkisinin de belirtisi yok — yanlış seçilmiş bir taban hata
/// vermez, yalnızca sinyali sessizce işe yaramaz kılar.
/// </para>
///
/// <para>
/// <b>Ölçtüğü şey duvar saati değil, veri.</b> Taban uzunluğu arttıkça
/// "ilk-görülen" sayısı düşer, çünkü daha çok imza daha önce görülmüş olur.
/// Aranan şey o eğrinin <b>düzleştiği</b> yer: ondan sonra taban uzatmak
/// gürültüyü azaltmıyor, yalnızca sorguyu pahalılaştırıyor.
/// </para>
///
/// <para>
/// Mutlak sayı yerine <b>orana</b> bakılıyor: penceredeki ayrı imzaların yüzde
/// kaçı "yeni". Bu, farklı hacimli veri kümeleri arasında karşılaştırılabilir
/// ve makinenin hızından bağımsız.
/// </para>
///
/// <para>
/// Koşturma — <b>gerçek veri gerekiyor</b>, en az taban süresi kadar geçmiş:
/// <code>
/// BIZIGO_BASELINE_SWEEP=1 dotnet test tests/Bizigo.IntegrationTests -c Release \
///   --filter FullyQualifiedName~BaselineWindowMeasurement -l "console;verbosity=detailed"
/// </code>
/// Rapor <c>$TMPDIR/t35-baseline.log</c>'a da yazılıyor. <b>İki koşum kaydedin</b>
/// (farklı gün / farklı yük): tek koşum bir günün karakterini ölçer.
/// </para>
///
/// <para>
/// <b>Veri nereden geliyor (T39):</b> <c>bizigo seed golden</c> altın örnekleri
/// gerçek boru hattından geçirip 30 güne yayıyor. Yayılımın <b>nasıl</b>
/// yapıldığı burada okunan sayıyı doğrudan belirliyor ve gerekçesiyle
/// <c>Bizigo.Cli.Seeding.GoldenSamplePlan</c> içinde yazılı. İki uyarı okunmadan
/// bu tablo yorumlanmamalı:
/// </para>
///
/// <list type="bullet">
/// <item><b>Eğrinin dirseği fixture'ın özelliği.</b> Yaklaşık <c>1/λ_min</c>
/// civarında oluşuyor, yani seçilen Zipf üssünün ve hacim/süre oranının sonucu.
/// Bağlayıcı bir sayı için ölçüm farklı <c>--zipf</c> ile tekrarlanmalı: dirsek
/// kayıyorsa ölçülen şey tabanın uzunluğu değil fixture'dır.</item>
/// <item><b>İmza ay adına duyarlı.</b> Maskeleme sözlüğünde ay <i>adı</i> için
/// maske yok; syslog biçimli vendor'larda aynı şablon her ay yeni bir
/// <c>signature_hash</c> alıyor. 30 günlük bir yayılım kaçınılmaz olarak bir ay
/// sınırı içeriyor, yani tabanı ayın birinden öteye uzatmak "ilk-görülen"
/// oranını beklendiği kadar düşürmüyor. Yükleyici bu sayıyı her koşumda
/// basıyor.</item>
/// </list>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class BaselineWindowMeasurement(DevStackFixture stack) : IAsyncLifetime
{
    /// <summary>
    /// Denenen taban uzunlukları. Aralık bilinçli geniş: eğrinin nerede
    /// düzleştiğini görmek için düzleşmenin iki yanına da bakmak gerekiyor.
    /// </summary>
    private static readonly TimeSpan[] BaselineLengths =
    [
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(14),
        TimeSpan.FromDays(30),
    ];

    /// <summary>Olay penceresi — RCA'nın tipik bakış aralığı.</summary>
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Taban ile pencere arasındaki boşluk. Örtüşen taban "ilk-görülen"i tanım
    /// gereği boşaltır, o yüzden sözleşme onu zaten reddediyor.
    /// </summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "t35-baseline.log");

    private ClickHouseContext _context = null!;
    private CorrelationReader _reader = null!;

    private static bool Enabled => Environment.GetEnvironmentVariable("BIZIGO_BASELINE_SWEEP") == "1";

    public async ValueTask InitializeAsync()
    {
        _context = stack.CreateClickHouseContext();
        _reader = new CorrelationReader(_context);

        var migrator = new ClickHouseMigrator(_context);
        await migrator.MigrateAsync(RepoPath("db/clickhouse"), TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tabanı süpürür ve her uzunluk için "ilk-görülen" oranını raporlar.
    ///
    /// <para>
    /// <b>Hiçbir eşik iddia etmiyor.</b> Bu bir ölçüm, bekçi değil: hangi
    /// uzunluğun seçileceği sayıya bakan insanın kararı ve gerekçesiyle
    /// yazılacak.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Baseline_uzunlugu_supurmesi()
    {
        Assert.SkipUnless(Enabled, "BIZIGO_BASELINE_SWEEP=1 gerekiyor — bu bir ölçüm, bekçi değil.");

        var scope = ScopePredicate.From(AccessScope.System("baseline-sweep"));
        var report = new List<string>();

        // Pencerenin sonu "şimdi": ölçüm gerçek veriye karşı koşuyor ve en taze
        // aralık en temsili olan.
        var to = DateTimeOffset.UtcNow;
        var from = to - WindowLength;

        void Say(string line)
        {
            report.Add(line);
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }

        Say("=== T35 · baseline penceresi süpürmesi ===");
        Say(string.Create(
            CultureInfo.InvariantCulture,
            $"olay penceresi: {from:yyyy-MM-dd HH:mm}..{to:HH:mm} ({WindowLength.TotalMinutes:0} dk) · " +
            $"boşluk {Gap.TotalMinutes:0} dk"));
        Say(string.Empty);
        Say($"{"taban",10} {"ilk-görülen",12} {"pencere ayrı imza",18} {"yeni oranı",12}");

        // Penceredeki ayrı imza sayısı tabandan bağımsız; oranın paydası bu.
        var windowSignatures = await DistinctSignaturesAsync(from, to, scope);

        if (windowSignatures == 0)
        {
            Say("⚠ Pencerede hiç imzalı olay yok — ölçüm anlamsız. Daha yoğun bir aralık seçin.");
            Emit(report);
            return;
        }

        foreach (var length in BaselineLengths)
        {
            var window = new CorrelationWindow
            {
                From = from,
                To = to,
                BaselineTo = from - Gap,
                BaselineFrom = from - Gap - length,
            };

            var firstSeen = await _reader.GetFirstSeenSignaturesAsync(
                window, scope, 100_000, TestContext.Current.CancellationToken);

            Say(string.Create(
                CultureInfo.InvariantCulture,
                $"{Describe(length),10} {firstSeen.Count,12} {windowSignatures,18} " +
                $"{100.0 * firstSeen.Count / windowSignatures,11:0.0}%"));
        }

        Say(string.Empty);
        Say("Okuma notu: oran, taban uzadıkça düşmeli. Aranan yer eğrinin");
        Say("DÜZLEŞTİĞİ nokta — ondan sonrası gürültüyü azaltmıyor, yalnızca");
        Say("sorguyu pahalılaştırıyor. Oran hiç düşmüyorsa taban yeterince");
        Say("geçmiş içermiyor demektir ve ölçüm daha uzun bir arşivle tekrarlanmalı.");

        Emit(report);
    }

    /// <summary>
    /// Penceredeki ayrı imza sayısı — oranın paydası.
    ///
    /// <para>
    /// Tabanı pencerenin <b>kendisi</b> yaparak "hiçbiri yeni değil"
    /// durumundaki toplam sayıyı elde etmek yerine doğrudan sayılıyor; ikisi
    /// aynı sonucu verirdi ama bu okunması kolay ve bir sorgu daha ucuz.
    /// </para>
    /// </summary>
    private async Task<int> DistinctSignaturesAsync(DateTimeOffset from, DateTimeOffset to, ScopePredicate scope)
    {
        // Taban penceresi olay penceresinden **önce** ve boş: dolayısıyla
        // "ilk-görülen" penceredeki bütün ayrı imzaları döndürüyor.
        var probe = new CorrelationWindow
        {
            From = from,
            To = to,
            BaselineFrom = from.AddYears(-10),
            BaselineTo = from.AddYears(-10).AddMinutes(1),
        };

        var all = await _reader.GetFirstSeenSignaturesAsync(
            probe, scope, 100_000, TestContext.Current.CancellationToken);

        return all.Count;
    }

    private static string Describe(TimeSpan length) =>
        length < TimeSpan.FromDays(1)
            ? string.Create(CultureInfo.InvariantCulture, $"{length.TotalHours:0}sa")
            : string.Create(CultureInfo.InvariantCulture, $"{length.TotalDays:0}g");

    private static void Emit(IEnumerable<string> report)
    {
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bizigo.sln")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Depo kökü bulunamadı (Bizigo.sln).")
            : Path.Combine(dir.FullName, relative);
    }
}
