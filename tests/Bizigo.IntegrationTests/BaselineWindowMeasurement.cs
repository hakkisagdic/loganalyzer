using System.Globalization;
using Bizigo.Cli.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.IntegrationTests;

/// <param name="Label">Rapordaki ad.</param>
/// <param name="ZipfExponent">Sıklık yasasının üssü — bu ölçümün çevirdiği DÜĞME.</param>
/// <param name="Events">Tohumlanacak toplam olay.</param>
/// <param name="Seed">Deterministik üretim tohumu.</param>
public sealed record BaselineFixtureRecipe(string Label, double ZipfExponent, int Events, int Seed);

/// <summary>
/// <b>Baseline penceresi uzunluğunun ölçüm aracı</b> (T35, T39/D6 ile
/// tohumlaması kendisine verildi) — sayıyı üretmiyor, üretilebilir kılıyor.
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
/// <h3>Ölçüm kendi verisini tohumluyor (T39 / D6)</h3>
///
/// <para>
/// Eskiden compose yığınına elle yüklenmiş veriye bakıyordu ve
/// <see cref="DevStackFixture"/> boş bir konteyner ayağa kaldırdığı için
/// <b>hiçbir şey göremiyordu</b>. Dışarıdan bağlanmak da çözüm değildi:
/// ölçümü, birinin elle kurduğu bir duruma bağlar ve <b>tekrarlanabilirlik
/// sayının bağlayıcı olmasının şartıdır</b>. Artık her eğri kendi izole
/// veritabanını kuruyor ve <c>bizigo seed golden</c>'ın kullandığı
/// <b>aynı</b> tiplerle tohumluyor — ayrı bir tohumlama kodu, ölçülen verinin
/// üretimdekinden ayrışması demek olurdu.
/// </para>
///
/// <h3>İki eğri — tavsiye değil, imza</h3>
///
/// <para>
/// Süpürmenin baktığı veri sentetik ve yayılımı bir düğmeyle kuruluyor
/// (<c>--zipf</c>). Dirsek, fixture'ın en nadir imzasının tekrar aralığı
/// civarında oluşuyor — yani düğmenin doğrudan sonucu. Tek eğri bunu asla
/// gösteremez: tablo düzgün bir dirsek gösterir ve okunan sayı verinin değil
/// tohumlamanın özelliğidir.
/// </para>
///
/// <para>
/// Bu yüzden ölçüm iki farklı düğme konumuyla koşuyor ve
/// <see cref="BaselineFixtureVerdict.Compare"/>'in <b>imzası</b> iki eğri
/// istiyor. "İki kez koşturun" diye yazılsaydı bir kez koşturulur ve
/// unutulurdu; bu depoda tavsiye edilen kapıların tutmadığı iki kez ölçüldü.
/// </para>
///
/// <para>
/// Koşturma:
/// <code>
/// BIZIGO_BASELINE_SWEEP=1 dotnet test tests/Bizigo.IntegrationTests -c Release \
///   --filter FullyQualifiedName~BaselineWindowMeasurement -l "console;verbosity=detailed"
/// </code>
/// Rapor <c>$TMPDIR/t35-baseline.log</c>'a da yazılıyor. Tohumlama artık ölçümün
/// içinde olduğu için koşum <b>tekrarlanabilir</b>: aynı tohum aynı veriyi
/// üretiyor ve iki koşumun farkı yalnızca duvar saatinden gelen çapa.
/// </para>
///
/// <para>
/// <b>Koşturulduğunda ne kanıtlayacak:</b> (1) süpürme mekanizması gerçek
/// ClickHouse'ta, gerçek boru hattından geçmiş veriyle çalışıyor; (2) her iki
/// eğri de <see cref="BaselineSweepVerdict"/>'in reddetme protokolünden geçecek
/// kadar okunabilir; (3) dirseğin sıklık düğmesine <b>bağlı olup olmadığı</b> —
/// yani bu veri kümesinden üretim için bağlayıcı bir taban çıkıp çıkmadığı.
/// Üçüncüsü ölçümün asıl teslim ettiği şey ve iki eğri olmadan sorulamıyor.
/// </para>
///
/// <para>
/// ⚠️ <b>İmza ay adına duyarlı.</b> Maskeleme sözlüğünde ay <i>adı</i> için
/// maske yok; syslog biçimli vendor'larda aynı şablon her ay yeni bir
/// <c>signature_hash</c> alıyor (ölçüldü: 87 örnek satırın 38'i). 30 günlük bir
/// yayılım kaçınılmaz olarak bir ay sınırı içeriyor, yani tabanı ayın birinden
/// öteye uzatmak "ilk-görülen" oranını beklendiği kadar düşürmüyor. Rapor bu
/// satırı her koşumda basıyor.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class BaselineWindowMeasurement(DevStackFixture stack)
{
    /// <summary>
    /// Denenen taban uzunlukları. Aralık bilinçli geniş: eğrinin nerede
    /// düzleştiğini görmek için düzleşmenin iki yanına da bakmak gerekiyor.
    /// </summary>
    internal static readonly TimeSpan[] BaselineLengths =
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
    internal static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Taban ile pencere arasındaki boşluk. Örtüşen taban "ilk-görülen"i tanım
    /// gereği boşaltır, o yüzden sözleşme onu zaten reddediyor.
    /// </summary>
    internal static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Yayılım süresi. En uzun tabanın (30 gün) sonuna kadar veri olmalı, yoksa
    /// o satırlar <c>ArchiveTooShort</c> işaretlenir ve süpürme kısalır.
    /// </summary>
    internal static readonly TimeSpan Span = TimeSpan.FromDays(31);

    /// <summary>
    /// <b>Çevrilen düğmenin iki konumu.</b>
    ///
    /// <para>
    /// <c>2.0</c> yükleyicinin varsayılanı: ağır kuyruk, en nadir imza 30 günde
    /// birkaç kez. <c>1.4</c> belirgin biçimde daha düz: aynı imza günde
    /// birkaç kez. Model doğruysa ikincisinin dirseği <b>belirgin biçimde
    /// daha kısa</b> çıkmalı — ve o fark, dirseğin fixture'a bağlı olduğunun
    /// kanıtı olur. Aynı çıkarlarsa dirsek düğmeye dayanıklı demektir ve bu
    /// da bir sonuç.
    /// </para>
    ///
    /// <para>
    /// İki konum <b>uzak</b> seçildi. Yakın iki üs (2.0 ve 1.9) aynı dirseği
    /// verir ve "düğmeye dayanıklı" diye okunurdu — düğme fiilen çevrilmemiş
    /// olduğu hâlde. Sınanmayan bir düğmeye dayanıklılık iddiası, iddianın en
    /// kötü türü.
    /// </para>
    /// </summary>
    internal static readonly BaselineFixtureRecipe Steep =
        new("dik kuyruk", ZipfExponent: 2.0, Events: 120_000, Seed: 39);

    internal static readonly BaselineFixtureRecipe Flat =
        new("düz kuyruk", ZipfExponent: 1.4, Events: 120_000, Seed: 39);

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "t35-baseline.log");

    private static bool Enabled => Environment.GetEnvironmentVariable("BIZIGO_BASELINE_SWEEP") == "1";

    /// <summary>
    /// Tabanı iki fixture üzerinde süpürür, her uzunluk için "ilk-görülen"
    /// oranını raporlar ve dirseğin düğmeye bağlı olup olmadığını söyler.
    ///
    /// <para>
    /// <b>Hiçbir eşik iddia etmiyor.</b> Bu bir ölçüm, bekçi değil: hangi
    /// uzunluğun seçileceği sayıya bakan insanın kararı ve gerekçesiyle
    /// yazılacak. Reddettiği tek şey, <i>okunacak bir sayı olmadığı hâlde sayı
    /// varmış gibi görünmesi</i>.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Baseline_uzunlugu_supurmesi()
    {
        Assert.SkipUnless(Enabled, "BIZIGO_BASELINE_SWEEP=1 gerekiyor — bu bir ölçüm, bekçi değil.");

        var report = new List<string>();

        void Say(string line)
        {
            report.Add(line);
            File.AppendAllText(LogFile, line + Environment.NewLine);
        }

        // Çapa BİR KEZ alınıyor ve iki fixture'a da veriliyor.
        //
        // Her eğri kendi "şimdi"sini alsaydı aralarında tohumlama süresi kadar
        // kayma olurdu ve iki tablo aynı olay penceresini göstermezdi — sonra
        // da dirsek farkının düğmeden mi kaymadan mı geldiği ayırt edilemezdi.
        // Duvar saatinden alınmasının tek sebebi, syslog biçimlerinin yılsız
        // olması: `SYSLOG` ayrıştırıcısı yılı bugünden çıkarıyor, yani çapa
        // gerçek "şimdi"nin yakınında olmak zorunda.
        var anchor = DateTimeOffset.UtcNow;
        anchor = new DateTimeOffset(anchor.Ticks - (anchor.Ticks % TimeSpan.TicksPerSecond), anchor.Offset);

        Say("=== T35 · baseline penceresi süpürmesi ===");
        Say(string.Create(
            CultureInfo.InvariantCulture,
            $"olay penceresi: {anchor - WindowLength:yyyy-MM-dd HH:mm}..{anchor:HH:mm} " +
            $"({WindowLength.TotalMinutes:0} dk) · boşluk {Gap.TotalMinutes:0} dk · " +
            $"yayılım {Span.TotalDays:0} gün"));

        var steep = await SweepAsync(Steep, anchor, Say);
        var flat = await SweepAsync(Flat, anchor, Say);

        // İmza iki eğri istiyor; tek eğriyle derlenmiyor.
        var comparison = BaselineFixtureVerdict.Compare(steep, flat);

        Say(string.Empty);
        Say("=== dirsek ===");

        foreach (var (label, elbow) in comparison.Elbows)
        {
            Say($"{label,14} : {(elbow is null ? "yok" : BaselineSweepVerdict.Describe(elbow.Value))}");
        }

        Say(string.Empty);
        Say(comparison.Baseline is null
            ? "SEÇİLEBİLİR TABAN YOK."
            : $"SEÇİLEBİLİR TABAN: {BaselineSweepVerdict.Describe(comparison.Baseline.Value)}");
        Say(comparison.Reading);

        Say(string.Empty);
        Say("⚠ İmza ay adına duyarlı: maskeleme sözlüğünde ay ADI için maske yok, yani");
        Say("  syslog biçimli vendor'larda aynı şablon her ay yeni bir signature_hash");
        Say("  alıyor. 31 günlük yayılım bir ay sınırı içeriyor; tabanı ayın birinden");
        Say("  öteye uzatmak oranı beklendiği kadar düşürmüyor. Bu tabloyu okurken");
        Say("  hesaba katın.");

        Emit(report);

        // REDDETME — en sonda, çünkü tablo reddedilse bile OKUNMALI: sebebi
        // teşhis edecek olan o tablo.
        //
        // Her eğri AYRI AYRI geçiyor: biri okunabilir öbürü değilse ölçüm yine
        // yapılamamıştır, çünkü karşılaştırma iki okunabilir eğri gerektiriyor.
        foreach (var curve in new[] { steep, flat })
        {
            var rejection = BaselineSweepVerdict.Reject(curve.WindowSignatures, curve.Rows);

            Assert.True(
                rejection is null,
                $"'{curve.Label}' eğrisi okunabilir bir eğri üretmedi, dolayısıyla ölçüm YAPILMADI:\n" +
                $"{rejection}\n\nBu bir başarısızlık değil bir REDDETME: yeşil bir koşum " +
                "'ölçüm yapıldı' diye okunur ve tabloya bakan kişi orada bir sayı arar. " +
                $"Ölçemediğini söyleyip yeşil bitmek, o sayıyı uydurmakla aynı kapıya çıkardı. Rapor: {LogFile}");
        }

        // Dirseklerin AYRIŞMASI kırmızı yakmıyor — bkz. BaselineFixtureVerdict.
        // Ayrışma bir arıza değil bir sonuç: "bu veri kümesinden bağlayıcı taban
        // çıkmaz". Kırmızı yakan bir kural hiçbir fixture ile yeşile dönmezdi.
    }

    /// <summary>
    /// Tek bir fixture: izole veritabanı → tohumlama → süpürme.
    ///
    /// <para>
    /// Her eğri <b>kendi veritabanını</b> alıyor. Tek veritabanında iki
    /// <c>owner_group</c> ile ayırmak da mümkündü ama bir kapsam hatası iki
    /// eğriyi birbirine karıştırır ve karışım tabloda "dirsek aynı" diye
    /// okunurdu — yani ölçümün cevaplamak için var olduğu sorunun yanlış
    /// cevabı. İzolasyon burada ucuz ve şüpheyi ortadan kaldırıyor.
    /// </para>
    /// </summary>
    private async Task<BaselineCurve> SweepAsync(
        BaselineFixtureRecipe recipe,
        DateTimeOffset anchor,
        Action<string> say)
    {
        var token = TestContext.Current.CancellationToken;

        using var context = await stack.CreateIsolatedClickHouseContextAsync(token);
        await new ClickHouseMigrator(context).MigrateAsync(RepoPath("db/clickhouse"), token);

        var seeded = await SeedAsync(context, recipe, anchor, token);
        var reader = new CorrelationReader(context);
        var scope = ScopePredicate.From(AccessScope.System("baseline-sweep"));

        var to = anchor;
        var from = to - WindowLength;

        say(string.Empty);
        say(string.Create(
            CultureInfo.InvariantCulture,
            $"--- fixture '{recipe.Label}' · zipf={recipe.ZipfExponent:0.##} · tohum {recipe.Seed} · " +
            $"{seeded} satır ---"));
        say($"{"taban",10} {"ilk-görülen",12} {"pencere ayrı imza",18} {"yeni oranı",12}");

        // Penceredeki ayrı imza sayısı tabandan bağımsız; oranın paydası bu.
        var windowSignatures = await DistinctSignaturesAsync(reader, from, to, scope, token);
        var rows = new List<BaselineSweepRow>();

        foreach (var length in BaselineLengths)
        {
            var baselineFrom = from - Gap - length;

            // ARŞİV DERİNLİĞİ — sayıyı basmadan ÖNCE.
            //
            // Bu bir yokluk kontrolü değil VARLIK kanıtı: tabanın en uzak
            // ucunda gerçekten olay var mı. Sorulmasaydı, arşivin bittiği
            // yerden sonraki her uzunluk aynı sayıyı üretir, eğri düzleşir ve
            // o düzleşme aranan dirsekle birebir aynı görünürdü.
            var reaches = await DistinctSignaturesAsync(
                reader, baselineFrom, baselineFrom + ProbeSpan(length), scope, token) > 0;

            if (!reaches)
            {
                rows.Add(new BaselineSweepRow(length, BaselineLengthStatus.ArchiveTooShort, 0, 0));
                say(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{BaselineSweepVerdict.Describe(length),10} {"—",12} {windowSignatures,18} {"arşiv ulaşmıyor",12}"));
                continue;
            }

            var window = new CorrelationWindow
            {
                From = from,
                To = to,
                BaselineTo = from - Gap,
                BaselineFrom = baselineFrom,
            };

            var firstSeen = await reader.GetFirstSeenSignaturesAsync(window, scope, 100_000, token);

            var ratio = windowSignatures == 0 ? 0 : (double)firstSeen.Count / windowSignatures;
            rows.Add(new BaselineSweepRow(length, BaselineLengthStatus.Measured, firstSeen.Count, ratio));

            say(string.Create(
                CultureInfo.InvariantCulture,
                $"{BaselineSweepVerdict.Describe(length),10} {firstSeen.Count,12} {windowSignatures,18} " +
                $"{100.0 * ratio,11:0.0}%"));
        }

        var unmeasurable = BaselineSweepVerdict.UnmeasurableCount(rows);

        if (unmeasurable > 0)
        {
            // Eksik satırı söylemeyen bir rapor, tam bir süpürme yapılmış
            // izlenimi bırakır.
            say($"⚠ {unmeasurable} taban uzunluğu ölçülemedi: arşiv o kadar geriye gitmiyor. " +
                "Bu satırlarda sayı BASILMADI — basılsaydı arşivin sınırı verinin " +
                "karakteri gibi okunurdu.");
        }

        return new BaselineCurve(recipe.Label, recipe.ZipfExponent, windowSignatures, rows);
    }

    /// <summary>
    /// <c>bizigo seed golden</c>'ın <b>aynı</b> tipleriyle tohumluyor.
    ///
    /// <para>
    /// CLI'ı süreç olarak çağırmak yerine tipleri doğrudan çağırmak bilinçli:
    /// süreç çağırmak bağlantı dizesini, çalışma dizinini ve çıkış kodunu
    /// ölçümün sorunu yapardı. Kritik olan tarafta fark yok — satırlar aynı
    /// <see cref="GoldenSampleSeeder"/> üzerinden geçiyor, yani
    /// <c>signature_hash</c>, <c>time_source</c> ve <c>attrs</c> üretimdekiyle
    /// aynı üretiliyor. Ayrı bir tohumlama kodu yazmak, ölçülen verinin
    /// üretimdekinden sessizce ayrışması demek olurdu.
    /// </para>
    /// </summary>
    private static async Task<long> SeedAsync(
        ClickHouseContext context,
        BaselineFixtureRecipe recipe,
        DateTimeOffset anchor,
        CancellationToken cancellationToken)
    {
        var catalogDirectory = RepoPath(Path.Combine("catalog", "parsers"));
        var samples = GoldenSampleSeeder.ReadSamples(catalogDirectory);

        Assert.NotEmpty(samples);

        var tables = MappingTableCatalog.LoadFromDirectory(RepoPath(Path.Combine("catalog", "mappings")));
        var library = GrokPatternLibrary.LoadWithOverlay(
            RepoPath(Path.Combine("catalog", "patterns", "legacy")),
            RepoPath(Path.Combine("catalog", "patterns", "bizigo-v1")));

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(
            catalogDirectory, new ParserCompiler(new GrokCompiler(library), tables));

        Assert.Empty(load.Errors);

        var masks = MaskCatalog.LoadFromFile(RepoPath(Path.Combine("catalog", "masks", "bizigo-masks.yaml")));
        var seeder = new GoldenSampleSeeder(new Dispatcher(catalog, new DispatchStats()), masks);

        var plan = GoldenSamplePlan.Build(
            seeder.Signatures(samples),
            GoldenSampleSeeder.Vendors(samples),
            new SeedPlanOptions(anchor, Span, recipe.Events, recipe.ZipfExponent, recipe.Seed));

        var writer = new EventWriter(context);

        var seedReport = await seeder.RunAsync(
            samples, plan, ownerGroup: "golden", batchRows: 20_000,
            (batch, token) => writer.WriteEventsAsync(batch, token),
            cancellationToken);

        return seedReport.Rows;
    }

    /// <summary>
    /// Tabanın en uzak ucundan ne kadarlık bir dilime bakılacağı.
    ///
    /// <para>
    /// Uzunlukla ölçekleniyor ama bir saatle sınırlı: 30 günlük bir taban için
    /// bütün tabanı taramak gereksiz pahalı, 1 saatlik taban için ise bir
    /// saatlik dilim zaten tabanın kendisi.
    /// </para>
    /// </summary>
    internal static TimeSpan ProbeSpan(TimeSpan length) =>
        length < TimeSpan.FromHours(1) ? length : TimeSpan.FromHours(1);

    /// <summary>
    /// Bir aralıktaki ayrı imza sayısı — hem oranın paydası hem arşivin oraya
    /// ULAŞTIĞININ kanıtı.
    ///
    /// <para>
    /// Tabanı pencerenin <b>kendisi</b> yaparak "hiçbiri yeni değil"
    /// durumundaki toplam sayıyı elde etmek yerine doğrudan sayılıyor; ikisi
    /// aynı sonucu verirdi ama bu okunması kolay ve bir sorgu daha ucuz.
    /// </para>
    /// </summary>
    private static async Task<int> DistinctSignaturesAsync(
        CorrelationReader reader,
        DateTimeOffset from,
        DateTimeOffset to,
        ScopePredicate scope,
        CancellationToken cancellationToken)
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

        var all = await reader.GetFirstSeenSignaturesAsync(probe, scope, 100_000, cancellationToken);
        return all.Count;
    }

    private static void Emit(IEnumerable<string> report)
    {
        foreach (var line in report)
        {
            Console.WriteLine(line);
        }
    }

    internal static string RepoPath(string relative)
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
