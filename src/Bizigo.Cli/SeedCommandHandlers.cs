using System.Globalization;
using Bizigo.Cli.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Cli;

/// <param name="Catalog">Parser kataloğu dizini (altın örnekler burada).</param>
/// <param name="MaskFile">Maskeleme sözlüğü — <c>signature_hash</c>'in tanımı.</param>
/// <param name="ConnectionString">ClickHouse bağlantısı; <c>--dry-run</c> ile kullanılmıyor.</param>
/// <param name="OwnerGroup">Yükleyicinin yazdığı <b>tek</b> kapsam grubu.</param>
/// <param name="Plan">Zaman yayılımının parametreleri.</param>
/// <param name="BatchRows">Tek <c>INSERT</c>'e giden satır sayısı.</param>
/// <param name="Replace">Grubun mevcut satırlarını silip yeniden yaz.</param>
/// <param name="DryRun">ClickHouse'a hiç dokunma; üret, doğrula, raporla.</param>
internal sealed record SeedGoldenRequest(
    string Catalog,
    string MaskFile,
    string ConnectionString,
    string OwnerGroup,
    SeedPlanOptions Plan,
    int BatchRows,
    bool Replace,
    bool DryRun);

/// <summary>
/// <c>bizigo seed golden</c> — altın örnekleri gerçek boru hattından geçirip
/// ClickHouse'a yazar (T39).
///
/// <para>
/// CLI'da olmasının sebebi <c>schema migrate</c> ile aynı: ClickHouse'a dokunan
/// operatör komutlarının yeri burası ve bağlantı bilgisi zaten dışarıdan
/// geliyor.
/// </para>
/// </summary>
internal static class SeedCommandHandlers
{
    public static async Task<int> Golden(
        SeedGoldenRequest request,
        ParserToolbox toolbox,
        CancellationToken cancellationToken)
    {
        var samples = GoldenSampleSeeder.ReadSamples(request.Catalog);

        if (samples.Count == 0)
        {
            Console.Error.WriteLine(
                $"hata   {request.Catalog} altında hiç `samples/*.log` yok — yüklenecek satır bulunamadı.");
            return 1;
        }

        if (!File.Exists(request.MaskFile))
        {
            Console.Error.WriteLine(
                $"hata   Maskeleme sözlüğü bulunamadı: {request.MaskFile}. " +
                "İmza olmadan yüklenen veri F3'ün iki korelasyonunu da beslemiyor.");
            return 1;
        }

        var catalog = new ParserCatalog();
        var load = catalog.LoadFromDirectory(request.Catalog, toolbox.Compiler);

        foreach (var error in load.Errors)
        {
            Console.Error.WriteLine($"hata   {error}");
        }

        if (load.Errors.Count > 0)
        {
            return 1;
        }

        var masks = MaskCatalog.LoadFromFile(request.MaskFile);
        var seeder = new GoldenSampleSeeder(new Dispatcher(catalog, new DispatchStats()), masks);

        var signatures = seeder.Signatures(samples);
        var plan = GoldenSamplePlan.Build(signatures, GoldenSampleSeeder.Vendors(samples), request.Plan);
        var timeSensitive = seeder.TimeSensitiveLines(samples, request.Plan.Anchor, request.Plan.Span);

        Describe(request, samples.Count, signatures, plan.Count, timeSensitive);

        if (request.DryRun)
        {
            // Kuru koşum ClickHouse'a bağlanmıyor ama **doğrulamayı atlamıyor**:
            // zaman damgası bekçisi her satırda koşuyor. Yüklemeden önce
            // "tutuyor mu" sorusunun Docker'sız cevabı bu.
            var dry = await seeder.RunAsync(
                samples, plan, request.OwnerGroup, request.BatchRows,
                static (_, _) => Task.CompletedTask, cancellationToken);

            Report(dry, written: false);
            return 0;
        }

        using var context = new ClickHouseContext(new ClickHouseOptions
        {
            ConnectionString = request.ConnectionString,
        });

        var maintenance = new SeedMaintenance(context);
        var existing = await maintenance.CountAsync(request.OwnerGroup, cancellationToken);

        if (existing > 0 && !request.Replace)
        {
            // Üstüne yazmak veriyi ikiye katlar ve bu hiçbir yerde hata
            // üretmez — yalnızca hacmi ve "ilk-görülen" oranını bozar. O yüzden
            // varsayılan davranış durmak.
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 hata   `{request.OwnerGroup}` grubunda zaten {existing} satır var.
                        Üstüne yazmak veriyi ikiye katlar ve ölçüm bunu göremez.
                        Yeniden yüklemek için: --replace  (yalnızca BU grubun
                        satırlarını siler; tablodaki diğer veriye dokunmaz).
                 """));
            return 1;
        }

        if (existing > 0)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"siliniyor  owner_group='{request.OwnerGroup}' ({existing} satır) — başka grup etkilenmiyor"));
            await maintenance.DeleteAsync(request.OwnerGroup, cancellationToken);
        }

        var writer = new EventWriter(context);

        var report = await seeder.RunAsync(
            samples, plan, request.OwnerGroup, request.BatchRows,
            async (batch, token) =>
            {
                var result = await writer.WriteEventsAsync(batch, token);

                if (result.RowsWritten != batch.Count)
                {
                    // Yutulmuyor: eksik yüklenmiş bir veri kümesi hata vermeden
                    // ölçümü bozar.
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"ClickHouse {batch.Count} satırın {result.RowsWritten} tanesini yazdı."));
                }

                Console.Write('.');
            },
            cancellationToken);

        Console.WriteLine();
        Report(report, written: true);
        return 0;
    }

    private static void Describe(
        SeedGoldenRequest request,
        int sampleCount,
        IReadOnlyList<ulong> signatures,
        int plannedRows,
        int timeSensitive)
    {
        var distinct = signatures.Where(static hash => hash != 0).Distinct().Count();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             === altın örnek yükleyici (T39) ===
             katalog        : {request.Catalog}
             örnek satır    : {sampleCount}  ({distinct} ayrı imza)
             kapsam grubu   : {request.OwnerGroup}   ← yükleyicinin yazdığı TEK grup
             yayılım        : {request.Plan.Anchor - request.Plan.Span:yyyy-MM-dd HH:mm}Z .. {request.Plan.Anchor:yyyy-MM-dd HH:mm}Z
             sıklık yasası  : Zipf s={request.Plan.ZipfExponent:0.##}, sıra imza üzerinden,
                              vendor'lar arasında sırayla dağıtılmış, tohum {request.Plan.Seed}
             varış zamanı   : düzgün (uniform) — günlük ritim BİLEREK yok, bkz. GoldenSamplePlan
             planlanan satır: {plannedRows}
             """));

        if (timeSensitive > 0)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 ⚠ ÜRÜN ÖZELLİĞİ (yükleyicinin kusuru değil): {sampleCount} satırın {timeSensitive} tanesinin
                   `signature_hash`'i bu yayılım boyunca SABİT DEĞİL. Maskeleme sözlüğünde ay ADI
                   için maske yok; `NUMBER` günü ve saati yutuyor ama `May`/`Oct` imzada kalıyor.
                   Sonucu: syslog biçimli vendor'larda aynı şablon her ay yeni bir imza alıyor ve
                   ayın birinde hepsi "ilk-görülen" olarak yanıyor. Baseline eğrisini okurken
                   ay sınırını hesaba katın.
                 """));
        }
    }

    private static void Report(SeedReport report, bool written)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""

             {(written ? "yazıldı" : "üretildi (kuru koşum, ClickHouse'a dokunulmadı)")}: {report.Rows} satır
             ayrı imza      : {report.DistinctSignatures}
             zaman aralığı  : {report.From:yyyy-MM-dd HH:mm:ss}Z .. {report.To:yyyy-MM-dd HH:mm:ss}Z
             son 45 dk      : {report.RowsInLastWindow} satır, {report.SignaturesInLastWindow} ayrı imza
                              ← baseline oranının PAYDASI ikinci sayı
             """));

        Line("vendor", report.ByVendor);
        Line("time_source", report.ByTimeSource);
        Line("parse_status", report.ByParseStatus);

        if (report.SignaturesInLastWindow == 0)
        {
            Console.Error.WriteLine(
                "uyarı  Son 45 dakikada hiç imza yok — baseline ölçümü \"pencerede hiç imzalı olay yok\" der ve durur.");
        }
        else if (report.SignaturesInLastWindow < 10)
        {
            Console.Error.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 uyarı  Son 45 dakikadaki ayrı imza {report.SignaturesInLastWindow} — oran %{100.0 / report.SignaturesInLastWindow:0.#} adımlarla
                        değişir ve eğrinin dirseği okunamaz. --events'i büyütün ya da --zipf'i küçültün.
                 """));
        }
    }

    private static void Line(string title, IReadOnlyDictionary<string, long> counters)
    {
        var body = string.Join(
            "  ",
            counters.OrderByDescending(static kv => kv.Value)
                .Select(static kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value}")));

        Console.WriteLine($"{title,-14} : {body}");
    }
}
