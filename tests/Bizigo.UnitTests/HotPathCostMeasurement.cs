using System.Diagnostics;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Ingest.Discovery;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// <b>K35'in sıcak yol maliyeti — ticket'ın teslim ettiği sayı.</b>
///
/// <para>
/// Cevaplaması gereken soru tek: <i>maskeleme her olayda koşunca sıcak yolun
/// maliyeti ne kadar artıyor?</i> Bugüne kadar maskeleme olayların yalnızca
/// <c>SampleRate</c> kadarında (%1) ve ayrıştırması başarısız olanlarda
/// koşuyordu; K35 onu %100'e çıkarıyor.
/// </para>
///
/// <para>
/// <b>Mutlak bir bütçe yok — bilerek.</b> F1'in en pahalı dersi tam buydu: duvar
/// saati bütçesi ölçmek istediğin şeyi ölçmez, makinenin o anki hızını ölçer.
/// Burada üretilen tek anlamlı çıktı, <b>aynı süreçte aynı satırlar üzerinde</b>
/// alınmış bir tabana <b>oran</b>. Bu yüzden test hiçbir eşik iddia etmiyor:
/// bekçi değil, ölçüm. K35'in yeniden değerlendirilip değerlendirilmeyeceği
/// kararı sayıya bakan insanın.
/// </para>
///
/// <para>
/// <b>Neden canlı sidecar gerektirmiyor:</b> ölçülen şey saf CPU işi — maskeleme
/// ve hash. Sidecar bu yolda hiç yok (K14). Ölçümü Python venv'ine bağlamak,
/// hiç koşulmamasının en kolay yolu olurdu. <c>SidecarLiveTests</c> kendi
/// sorusunu (arızalı sidecar sıcak yolu yavaşlatıyor mu) sormaya devam ediyor.
/// </para>
///
/// <para>
/// Koşturma — <b><c>-c Release</c> şart</b>:
/// <code>
/// BIZIGO_HOTPATH_BENCH=1 dotnet test tests/Bizigo.UnitTests -c Release \
///   --filter FullyQualifiedName~HotPathCostMeasurement -l "console;verbosity=detailed"
/// </code>
/// Debug ölçümü <b>yanıltıyor ve tek yönde</b>: ayrıştırma bizim kodumuz ve
/// Debug'da orantısız yavaşlıyor, maskeleme ise BCL regex olduğu için
/// etkilenmiyor (ölçüldü: imza maliyeti iki derlemede de aynı). Sonuç, Debug'ın
/// tabanı şişirip <c>C/B</c> oranını <b>olduğundan küçük</b> göstermesi — yani
/// K35'i hak etmediği kadar ucuz gösteriyor.
///
/// <para>
/// Rapor ayrıca <c>$TMPDIR/t29-hotpath.log</c>'a yazılıyor — xunit konsol
/// çıktısını yutarsa sayı yine de duruyor.
/// </para>
/// </summary>
public sealed class HotPathCostMeasurement
{
    /// <summary>Tur başına olay. Katalogdaki 86 gerçek satır döngüyle tekrarlanıyor.</summary>
    private const int EventsPerRound = 40_000;

    /// <summary>
    /// Tur sayısı. Tek tur yeterli değil: makinede başka bir ajan bir build
    /// başlatırsa o turun tamamı kirlenir. Turlar arası <b>en küçük</b> değer
    /// raporlanıyor — kıyaslamada standart tahminci, çünkü girişim ölçümü
    /// yalnızca yukarı çeker.
    /// </summary>
    private const int Rounds = 5;

    private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "t29-hotpath.log");

    private readonly List<string> _report = [];

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("BIZIGO_HOTPATH_BENCH") == "1";

    private void Report(string line)
    {
        _report.Add(line);
        File.AppendAllText(LogFile, line + Environment.NewLine);
    }

    [Fact]
    public void K35_sicak_yol_maliyeti()
    {
        Assert.SkipUnless(Enabled, "BIZIGO_HOTPATH_BENCH=1 gerekiyor — bu bir ölçüm, bekçi değil.");

        var masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);
        var (dispatcher, lines) = BuildWorkload();

        Assert.NotEmpty(lines);

        // Bugünün profili: başarılı olaylarda %1 örnekleme. Ölçüm bunu
        // değiştirmiyor — değiştirseydi "öncesi" arm'ı gerçeği temsil etmezdi.
        var options = new SidecarOptions { QueueCapacity = 2048, SampleRate = 0.01 };
        var stats = new DiscoveryStats();
        var cache = new TemplateCache(50_000);
        var queue = new DiscoveryQueue(options, stats);
        var annotator = new DiscoveryAnnotator(options, cache, queue, stats);

        // Isınma: hem `RegexOptions.Compiled` kod üretimi hem JIT. Ölçülmeden
        // ödenmezse ilk turun tamamı bu maliyeti taşır ve `Rounds` boyunca
        // minimum almak onu gizler değil, yalnızca ilk turu atar.
        Warmup(dispatcher, masks, annotator, lines);

        var parse = double.MaxValue;
        var before = double.MaxValue;
        var after = double.MaxValue;
        var signatureOnly = double.MaxValue;

        for (var round = 0; round < Rounds; round++)
        {
            // Arm'lar tur içinde iç içe koşuyor: makine turun ortasında
            // yavaşlarsa üçü birden etkilenir, biri diğerine göre kaymaz.
            parse = Math.Min(parse, Time(() => RunParseOnly(dispatcher, lines)));
            before = Math.Min(before, Time(() => RunBeforeK35(dispatcher, masks, annotator, options, lines)));
            after = Math.Min(after, Time(() => RunAfterK35(dispatcher, masks, annotator, lines)));
            signatureOnly = Math.Min(signatureOnly, Time(() => RunSignatureOnly(masks, lines)));
        }

        Report("=== T29 · K35 sıcak yol maliyeti ===");
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"girdi: katalogdaki {lines.Count} gerçek vendor satırı · " +
            $"tur başına {EventsPerRound:N0} olay × {Rounds} tur (en küçük tur raporlanıyor)"));
        Report(Row("A · yalnız ayrıştırma", parse));
        Report(Row("B · öncesi  (ayrıştırma + %1 örneklemeli etiketleme)", before));
        Report(Row("C · sonrası (ayrıştırma + her olayda imza + etiketleme)", after));
        Report(Row("   · yalnız imza (maskeleme + hash)", signatureOnly));
        Report(string.Empty);
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"SICAK YOL ORANI  C/B = {after / before:0.00}×   (ticket'ın istediği sayı)"));
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"İMZA PAYI        (C−B)/C = %{100 * (after - before) / after:0.0} · " +
            $"imzanın ayrıştırmaya oranı = {signatureOnly / parse:0.00}×"));
        Report(string.Empty);
        Report("Okuma notu: C/B sıcak yolun tamamının kaç katına çıktığı. İmzanın kendi");
        Report("maliyeti mutlak olarak küçük olsa bile ayrıştırma ucuzsa oran büyür —");
        Report("karar oran ile mutlak artışa (C−B ns/olay) birlikte bakılarak verilmeli.");

        foreach (var line in _report)
        {
            Console.WriteLine(line);
        }
    }

    private static string Row(string label, double perEventNs) => string.Create(
        CultureInfo.InvariantCulture,
        $"{label,-58} {perEventNs,8:N0} ns/olay  ({1_000_000_000 / perEventNs,11:N0} olay/sn)");

    private static double Time(Action arm)
    {
        var clock = Stopwatch.StartNew();
        arm();
        clock.Stop();
        return clock.Elapsed.TotalMilliseconds * 1_000_000 / EventsPerRound;
    }

    private static void RunParseOnly(Dispatcher dispatcher, IReadOnlyList<Sample> lines)
    {
        for (var index = 0; index < EventsPerRound; index++)
        {
            var sample = lines[index % lines.Count];
            dispatcher.Dispatch(sample.Body, sample.ParserId);
        }
    }

    /// <summary>
    /// K35 <b>öncesi</b> sıcak yol. Üretimde artık bu kod yolu yok, o yüzden
    /// burada yeniden kuruluyor: örnekleme zarı önce atılıyor ve maskeleme
    /// yalnızca zar tuttuğunda ya da ayrıştırma başarısızken koşuyor.
    ///
    /// <para>
    /// Yeniden kurulmuş bir arm ölçümün en zayıf halkası — ama alternatifi
    /// ölçümü iki ayrı commit'te koşturup <b>iki farklı süreçteki</b> sayıları
    /// karşılaştırmaktı, ki F1'in dersi tam olarak onun geçersiz olduğunu
    /// söylüyor. Yeniden kurulan mantık dört satır ve
    /// <c>DiscoveryAnnotator</c>'ın 48d8d1c'deki hâliyle birebir.
    /// </para>
    /// </summary>
    private static void RunBeforeK35(
        Dispatcher dispatcher,
        MaskCatalog masks,
        DiscoveryAnnotator annotator,
        SidecarOptions options,
        IReadOnlyList<Sample> lines)
    {
        for (var index = 0; index < EventsPerRound; index++)
        {
            var sample = lines[index % lines.Count];
            var result = dispatcher.Dispatch(sample.Body, sample.ParserId);
            var parseFailed = result.Result.Status == ParseStatus.Failed;

            var sampled = !parseFailed && options.SampleRate > 0
                && Random.Shared.NextDouble() < options.SampleRate;

            if (!parseFailed && !sampled)
            {
                continue;
            }

            annotator.Annotate(sample.SourceClass, sample.Body, masks.Compute(sample.Body), parseFailed);
        }
    }

    /// <summary>K35 <b>sonrası</b>: üretimin <c>ParsingSink</c>'iyle birebir aynı iki adım.</summary>
    private static void RunAfterK35(
        Dispatcher dispatcher,
        MaskCatalog masks,
        DiscoveryAnnotator annotator,
        IReadOnlyList<Sample> lines)
    {
        for (var index = 0; index < EventsPerRound; index++)
        {
            var sample = lines[index % lines.Count];
            var result = dispatcher.Dispatch(sample.Body, sample.ParserId);
            var signature = masks.Compute(sample.Body);

            annotator.Annotate(
                sample.SourceClass,
                sample.Body,
                signature,
                result.Result.Status == ParseStatus.Failed);
        }
    }

    private static void RunSignatureOnly(MaskCatalog masks, IReadOnlyList<Sample> lines)
    {
        for (var index = 0; index < EventsPerRound; index++)
        {
            masks.Compute(lines[index % lines.Count].Body);
        }
    }

    private static void Warmup(
        Dispatcher dispatcher,
        MaskCatalog masks,
        DiscoveryAnnotator annotator,
        IReadOnlyList<Sample> lines)
    {
        for (var index = 0; index < 5_000; index++)
        {
            var sample = lines[index % lines.Count];
            dispatcher.Dispatch(sample.Body, sample.ParserId);
            annotator.Annotate(sample.SourceClass, sample.Body, masks.Compute(sample.Body), parseFailed: false);
        }
    }

    /// <summary>
    /// Girdi <b>gerçek</b> vendor satırları — katalogdaki örnek dosyalar.
    /// Sentetik satır maskeleme maliyetini istediği yere çeker: uzunluk, maske
    /// isabet sayısı ve token dağılımı ölçülen şeyin tamamı.
    ///
    /// <para>
    /// Her satırın parser'ı bir kez, kurulum sırasında bağsız dağıtımla
    /// bulunuyor ve ölçümde <b>envanter bağı</b> olarak kullanılıyor — üretimde
    /// kaynak envanterden parser'a bağlı (1. kademe) ve ölçüm o yolu temsil
    /// etmeli.
    /// </para>
    /// </summary>
    private static (Dispatcher Dispatcher, IReadOnlyList<Sample> Lines) BuildWorkload()
    {
        var tables = MappingTableCatalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));
        var compiler = new ParserCompiler(new GrokCompiler(RepositoryLayout.DefaultLibrary), tables);

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(RepositoryLayout.CatalogParserDirectory, compiler);
        Assert.Empty(load.Errors);

        var dispatcher = new Dispatcher(catalog, new DispatchStats());
        var samples = new List<Sample>();

        foreach (var path in Directory
                     .EnumerateFiles(RepositoryLayout.CatalogParserDirectory, "*.log", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var sourceClass = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))!);

            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                samples.Add(new Sample(line, dispatcher.Dispatch(line, string.Empty).Result.ParserId, sourceClass));
            }
        }

        return (dispatcher, samples);
    }

    private sealed record Sample(string Body, string ParserId, string SourceClass);
}
