using Bizigo.Cli.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.UnitTests;

/// <summary>
/// Altın örnek yükleyicisinin (T39) bekçileri.
///
/// <para>
/// Yükleyici tam da "sessiz yanlış davranış" sınırında duruyor: yanlış
/// yüklenmiş bir veri kümesi hata vermez, yalnızca iki F3 ölçümünü sessizce
/// yanlış yapar ve o ölçümler F3'ün kapsamına karar veriyor. Buradaki testler
/// o sessizliği kırmak için var.
/// </para>
/// </summary>
public sealed class GoldenSeedTests
{
    /// <summary>Yayılımın sağ ucu — testler duvar saatine bağlı olmasın diye sabit.</summary>
    private static readonly DateTimeOffset Anchor = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Span = TimeSpan.FromDays(30);

    private static readonly MaskCatalog Masks = MaskCatalog.LoadFromFile(RepositoryLayout.MaskFile);

    /// <summary>
    /// <b>Sigma ölçümünün ön kontrolü bu testin konusu.</b>
    ///
    /// <para>
    /// <c>prototypes/t30-sigma/measure.py</c> her vendor'ın örnek dizinindeki
    /// <b>en uzun satırın ortasından 60 karakter</b> alıp <c>events_ocsf</c>'in
    /// <c>raw_data</c> kolonunda arıyor; bulamazsa ölçüm hiç başlamıyor. Sonda
    /// çalışma anında dosyadan türetiliyor, yani yükleyicinin satırı bozması
    /// ölçümü sessizce değil <b>gürültülü</b> durduruyor — ama o gürültü
    /// koordinatörün ClickHouse koşumunda çıkardı, burada çıkması daha ucuz.
    /// </para>
    ///
    /// <para>
    /// Sondanın damgayla kesişmediği bir <b>tesadüf değil</b>: en uzun satırın
    /// ortası, damganın durduğu satır başından uzakta. Ama tesadüfe benziyor ve
    /// örnek dosyaları değişince bozulabilir — bu test tam olarak o günü
    /// yakalamak için duruyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Sigma_on_kontrolunun_sondasi_yeniden_yazilmis_satirda_duruyor()
    {
        var probes = GoldenProbes();

        // Dört vendor da sonda üretebilmeli; üretemeyen vendor ön kontrolde
        // sessizce atlanır ve "hiç altın örnek yok" kararı eksik bilgiyle verilir.
        Assert.Equal(4, probes.Count);

        foreach (var (file, probe) in probes)
        {
            var longest = LongestLine(Path.GetDirectoryName(file)!);

            foreach (var at in new[] { Anchor, Anchor - (Span / 2), Anchor - Span })
            {
                var rewritten = SampleTimeRewriter.Rewrite(longest, at).Text;

                Assert.True(
                    rewritten.Contains(probe, StringComparison.Ordinal),
                    $"""
                     {file}: measure.py'nin sondası yeniden yazılmış satırda YOK ({at:yyyy-MM-dd}).
                       sonda : {probe}
                       satır : {rewritten}
                     Sigma ölçümü bu hâlde "hiçbiri altın örnek değil" deyip çıkış kodu 3 verir.
                     """);
            }
        }
    }

    /// <summary>
    /// <b>Asıl bekçi:</b> kataloğun <b>her</b> altın örnek satırı, ekilen ana
    /// eşit bir <c>ts</c> ile normalize olmalı.
    ///
    /// <para>
    /// Damga biçimi bilgisi iki yerde yazılı (parser YAML'ının <c>date</c> adımı
    /// ve <see cref="SampleTimeRewriter"/>); ayrıştıkları gün satır ya orijinal
    /// tarihine ya yanlış saat dilimine düşer, ikisi de hata üretmez. Üç ayrı an
    /// deneniyor: ay sınırı, yıl içi kayma ve saat dilimi hataları tek bir anda
    /// gizlenebilir.
    /// </para>
    /// </summary>
    [Fact]
    public void Her_ornek_satiri_ekilen_ana_dusuyor()
    {
        var seeder = Seeder();
        var samples = GoldenSampleSeeder.ReadSamples(RepositoryLayout.CatalogParserDirectory);

        Assert.NotEmpty(samples);

        var probes = new[] { Anchor, Anchor - TimeSpan.FromDays(11), Anchor - Span };

        foreach (var sample in samples)
        {
            foreach (var at in probes)
            {
                // Compose bekçiyi kendi içinde koşturuyor; buradaki iddia,
                // bekçinin gerçekten koştuğunun ve sonucun doğru olduğunun kaydı.
                var normalized = seeder.Compose(sample, at, "golden", Guid.NewGuid());
                Assert.Equal(at, normalized.Timestamp);
            }
        }
    }

    /// <summary>
    /// <b>Bekçinin kırmızı yanabildiği ölçüldü.</b>
    ///
    /// <para>
    /// Senaryo uydurma değil: nginx'in <c>date</c> adımı <c>dd/MMM/yyyy:HH:mm:ss zzz</c>
    /// biçimini kullanıyor ve .NET'in <c>zzz</c>'si hem <c>+0000</c> hem
    /// <c>+00:00</c> yazımını kabul ediyor — <b>ölçüldü</b>. Yeniden yazıcının
    /// deseni ise yalnızca <c>[+-]\d{4}</c> tanıyor. Yani örnek dosyaya iki
    /// noktalı ofset taşıyan bir satır eklendiği gün parser onu 2015'e
    /// ayrıştırır, yeniden yazıcı damgayı görmez ve satır sessizce yanlış zamana
    /// düşerdi. Bekçi bunu görüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Yeniden_yazicinin_tanimadigi_damga_bekciyi_kirmizi_yakiyor()
    {
        var seeder = Seeder();

        const string Line = """
            {"time": "17/May/2015:08:05:32 +00:00", "remote_ip": "203.0.113.3", "remote_user": "-", "request": "GET /downloads/product_1 HTTP/1.1", "response": 304, "bytes": 0, "referrer": "-", "agent": "curl/7.22.0"}
            """;

        var sample = new GoldenSampleLine(Line, "nginx.access", "<test>", 1);

        // Önce yeniden yazıcının bu damgayı gerçekten TANIMADIĞI ölçülüyor;
        // yoksa test bekçiyi değil kendi kurgusunu doğrulardı.
        Assert.False(SampleTimeRewriter.Rewrite(Line, Anchor).Rewritten);

        var error = Assert.Throws<InvalidOperationException>(
            () => seeder.Compose(sample, Anchor, "golden", Guid.NewGuid()));

        Assert.Contains("Zaman damgası yeniden yazımı tutmadı", error.Message, StringComparison.Ordinal);

        // Ve geri alındı: aynı satır tanınan ofset yazımıyla sorunsuz geçiyor.
        var healthy = sample with { Text = Line.Replace("+00:00", "+0000", StringComparison.Ordinal) };
        Assert.Equal(Anchor, seeder.Compose(healthy, Anchor, "golden", Guid.NewGuid()).Timestamp);
    }

    /// <summary>
    /// Sigma ölçümü vendor başına sayıyor ve verisi olmayan vendor'ın
    /// kurallarını <b>paydadan düşüyor</b>: dördü de temsil edilmezse ölçülen
    /// oran başka bir sorunun cevabı olur.
    /// </summary>
    [Fact]
    public void Dort_vendor_da_planda_temsil_ediliyor()
    {
        var seeder = Seeder();
        var samples = GoldenSampleSeeder.ReadSamples(RepositoryLayout.CatalogParserDirectory);
        var plan = Plan(seeder, samples);

        var byVendor = plan
            .GroupBy(occurrence => samples[occurrence.LineIndex].ParserDirectory, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var vendor in new[]
                 {
                     "fortinet.fortigate", "cisco.asa", "mikrotik.routeros", "nginx.access",
                 })
        {
            Assert.True(
                byVendor.GetValueOrDefault(vendor) > 0,
                $"{vendor} planda hiç yok — Sigma ölçümü onun kurallarını `no_data` işaretler.");
        }
    }

    /// <summary>
    /// Her satır en az bir kez yazılmalı. Sigma sondası kataloğun <b>en uzun</b>
    /// satırından türüyor ve o satır sıklık kuyruğuna düşerse ölçüm hiç başlamaz.
    /// </summary>
    [Fact]
    public void Her_satir_planda_en_az_bir_kez_geciyor()
    {
        var seeder = Seeder();
        var samples = GoldenSampleSeeder.ReadSamples(RepositoryLayout.CatalogParserDirectory);
        var covered = Plan(seeder, samples).Select(static o => o.LineIndex).ToHashSet();

        Assert.Equal(samples.Count, covered.Count);
    }

    /// <summary>
    /// Aynı tohum aynı planı vermeli: iki koşumun aynı veriyi üretip üretmediği
    /// ancak böyle karşılaştırılabiliyor.
    /// </summary>
    [Fact]
    public void Plan_ayni_tohumla_ayni()
    {
        var seeder = Seeder();
        var samples = GoldenSampleSeeder.ReadSamples(RepositoryLayout.CatalogParserDirectory);

        Assert.Equal(Plan(seeder, samples), Plan(seeder, samples));
    }

    /// <summary>
    /// Yayılım gerçekten <b>geçmişe</b> gidiyor mu — baseline ölçümü tabanı 30
    /// güne kadar süpürüyor ve hepsi son saate yığılırsa eğri hiç oluşmaz.
    /// </summary>
    [Fact]
    public void Yayilim_otuz_gune_gercekten_uzaniyor()
    {
        var seeder = Seeder();
        var samples = GoldenSampleSeeder.ReadSamples(RepositoryLayout.CatalogParserDirectory);
        var plan = Plan(seeder, samples);

        Assert.True(plan[0].At <= Anchor - Span + TimeSpan.FromHours(6));
        Assert.True(plan[^1].At >= Anchor - TimeSpan.FromHours(1));

        // Her gün temsil edilmeli: bir günlük boşluk taban süpürmesinde
        // açıklanamayan bir basamak üretirdi.
        var days = plan.Select(static o => o.At.UtcDateTime.Date).Distinct().Count();
        Assert.True(days >= 30, $"Yayılım yalnızca {days} ayrı güne dokunuyor.");
    }

    private static IReadOnlyList<PlannedOccurrence> Plan(
        GoldenSampleSeeder seeder,
        IReadOnlyList<GoldenSampleLine> samples) =>
        GoldenSamplePlan.Build(
            seeder.Signatures(samples),
            GoldenSampleSeeder.Vendors(samples),
            new SeedPlanOptions(Anchor, Span, TotalEvents: 20_000, ZipfExponent: 2.0, Seed: 39));

    private static GoldenSampleSeeder Seeder()
    {
        var tables = MappingTableCatalog.LoadFromDirectory(
            Path.Combine(RepositoryLayout.Root, "catalog", "mappings"));

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(
            RepositoryLayout.CatalogParserDirectory,
            new ParserCompiler(new GrokCompiler(RepositoryLayout.DefaultLibrary), tables));

        Assert.Empty(load.Errors);

        return new GoldenSampleSeeder(new Dispatcher(catalog, new DispatchStats()), Masks);
    }

    /// <summary>
    /// <c>measure.py</c>'nin <c>golden_probes()</c> fonksiyonunun birebir
    /// karşılığı. <b>Kopya olması bilinçli ve tehlikeli</b> — ama alternatifi
    /// Python'u .NET testinden çağırmak olurdu. Ayrışma riski, sondanın
    /// üretiminin iki tarafta da <i>aynı dosyadan</i> ve aynı kuralla
    /// (en uzun satır, ortadan 60 karakter) yapılmasıyla sınırlı tutuldu.
    /// </summary>
    private static Dictionary<string, string> GoldenProbes()
    {
        var probes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var directory in Directory.EnumerateDirectories(
                     RepositoryLayout.CatalogParserDirectory, "samples", SearchOption.AllDirectories))
        {
            var longest = LongestLine(directory);

            if (longest.Length >= 100)
            {
                probes[Path.Combine(directory, "*.log")] = longest.Substring((longest.Length - 60) / 2, 60);
            }
        }

        return probes;
    }

    private static string LongestLine(string directory)
    {
        var longest = string.Empty;

        foreach (var path in Directory.EnumerateFiles(directory, "*.log").OrderBy(static p => p, StringComparer.Ordinal))
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();

                if (line.Length > longest.Length)
                {
                    longest = line;
                }
            }
        }

        return longest;
    }
}
