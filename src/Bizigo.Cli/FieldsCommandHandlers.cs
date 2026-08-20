using System.Globalization;
using Bizigo.Cli.Fields;
using Bizigo.Cli.Seeding;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Cli;

/// <param name="Catalog">Parser kataloğu (altın örnekler burada).</param>
/// <param name="MaskFile">Maskeleme sözlüğü.</param>
/// <param name="Migrations">ClickHouse göç dizini — görünümün kolon listesi oradan okunuyor.</param>
/// <param name="ConnectionString">Boşsa yalnızca katalog yarısı koşuyor.</param>
/// <param name="OwnerGroup">ClickHouse yarısında sayılan kapsam grubu.</param>
/// <param name="Anchor">Örnekleri hangi ana taşıyarak ölçeceği.</param>
internal sealed record FieldCoverageRequest(
    string Catalog,
    string MaskFile,
    string Migrations,
    string? ConnectionString,
    string OwnerGroup,
    DateTimeOffset Anchor);

/// <summary>
/// <c>bizigo fields coverage</c> — altın örneklerin taşıdığı bilginin ne
/// kadarının <c>events_ocsf</c>'e alan olarak indiğini ölçer (T39).
///
/// <para>
/// İki yarısı var ve ayrı sorular soruyorlar. <b>Katalog yarısı</b> ClickHouse
/// gerektirmiyor: örnek satırları gerçek boru hattından geçirip
/// <c>LogEvent</c>'e bakıyor, yani "katalog ne üretebiliyor". <b>ClickHouse
/// yarısı</b> yazma ve görünüm yolundan sonra ne kaldığını sayıyor. İkisinin
/// farkı tek başına görünmeyen bir hata sınıfını yakalıyor — alan doluyor ama
/// kolon boş görünüyor — ve o kayıp hata vermez, yalnızca o alana vuran her
/// Sigma kuralını sessizce sonuçsuz bırakır.
/// </para>
/// </summary>
internal static class FieldsCommandHandlers
{
    public static async Task<int> Coverage(
        FieldCoverageRequest request,
        ParserToolbox toolbox,
        CancellationToken cancellationToken)
    {
        // BEKÇİ: yazıcının yazdığı her kolonu tanımıyorsak ölçüm eksik bir
        // tabloyu tam gösterir.
        var unknown = EventFieldKinds.Unknown();

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine(
                "hata   `events` tablosuna eklenmiş ama EventFieldKinds'ta tanımlanmamış kolon(lar): " +
                string.Join(", ", unknown) +
                ". Alan kapsamı ölçümü onları hiç sormaz; önce oraya ekleyin.");
            return 1;
        }

        var samples = GoldenSampleSeeder.ReadSamples(request.Catalog);

        if (samples.Count == 0)
        {
            Console.Error.WriteLine($"hata   {request.Catalog} altında hiç `samples/*.log` yok.");
            return 1;
        }

        var columns = OcsfViewSchema.Read(request.Migrations);

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

        var seeder = new GoldenSampleSeeder(
            new Dispatcher(catalog, new DispatchStats()),
            MaskCatalog.LoadFromFile(request.MaskFile));

        // Her örnek satır BİR kez: soru "katalog ne taşıyor", "hangi satır kaç
        // kez yazıldı" değil. Zipf ağırlıkları buraya karışsaydı nadir bir
        // satırın doldurduğu alan oranda kaybolurdu.
        var events = new List<LogEvent>(samples.Count);

        foreach (var sample in samples)
        {
            events.Add(seeder.Compose(sample, request.Anchor, request.OwnerGroup, Guid.NewGuid()));
        }

        var report = FieldCoverage.Measure(events, columns);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             === alan kapsamı — altın örnekler → events_ocsf (T39) ===
             katalog   : {request.Catalog}
             görünüm   : {request.Migrations} içinden okundu, {columns.Count} kolon
             örnek satır: {samples.Count}, {report.Vendors.Count} vendor
             """));

        Print(report);

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            Console.WriteLine();
            Console.WriteLine(
                "ClickHouse yarısı atlandı (--clickhouse verilmedi). Katalog yarısı " +
                "\"ne üretilebiliyor\" diyor; \"ne yazılmış\" sorusu cevapsız kaldı.");
            return 0;
        }

        using var context = new ClickHouseContext(new ClickHouseOptions
        {
            ConnectionString = request.ConnectionString,
        });

        var stored = await new FieldCoverageReader(context).ReadAsync(
            request.OwnerGroup,
            [.. columns.Select(column => (column.Source, column.Alias))],
            cancellationToken);

        return Compare(report, stored, request.OwnerGroup);
    }

    private static void Print(FieldCoverageReport report)
    {
        var everywhere = report.EmptyEverywhere();

        Console.WriteLine();
        Console.WriteLine("### KUTU 3a — hiçbir vendor'da dolmayan OCSF alanları");
        Console.WriteLine(everywhere.Count == 0
            ? "  (yok)"
            : "  " + string.Join(", ", everywhere));
        Console.WriteLine(
            "  Bunlar için soru: eşleme hiç yazılmadı mı, yoksa örneklem bu bilgiyi hiç mi taşımıyor?");

        foreach (var vendor in report.Vendors)
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"=== {vendor.Vendor} · {vendor.Lines} örnek satır ==="));

            var filled = report.Aliases
                .Where(alias => vendor.Populated.GetValueOrDefault(alias) > 0)
                .Select(alias => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{alias}={100.0 * vendor.Populated[alias] / vendor.Lines:0}%"))
                .ToList();

            Console.WriteLine("  dolu OCSF alanları:");
            Console.WriteLine("    " + (filled.Count == 0 ? "(yok)" : string.Join("  ", filled)));

            var emptyHere = report.EmptyFor(vendor);

            Console.WriteLine("  KUTU 3b — burada boş, başka vendor'da dolu:");
            Console.WriteLine("    " + (emptyHere.Count == 0 ? "(yok)" : string.Join(", ", emptyHere)));

            var fromLine = vendor.Relocated.Where(static entry => entry.FromLine).ToList();

            Console.WriteLine("  KUTU 2 — satırdan gelmiş ama OCSF kolonuna değil `unmapped`'e inmiş:");

            if (fromLine.Count == 0)
            {
                Console.WriteLine("    (yok)");
            }
            else
            {
                foreach (var entry in fromLine)
                {
                    var note = entry.Note.Length == 0 ? string.Empty : $"   [{entry.Note}]";
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    {entry.Key,-28} {entry.Lines,3} satır  ör. {Clip(entry.Sample)}{note}"));
                }
            }

            Console.WriteLine("  KUTU 1 — hiçbir alana inmemiş metin (ayraç ve söz dizimi de burada):");

            if (vendor.Uncaptured.Count == 0)
            {
                Console.WriteLine("    (yok)");
            }
            else
            {
                foreach (var fragment in vendor.Uncaptured)
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"    {fragment.Lines,3} satır  {Clip(fragment.Text)}"));
                }
            }

            if (vendor.UncapturedDropped > 0)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    … {vendor.UncapturedDropped} parça daha basılmadı (rapor sınırı)."));
            }
        }
    }

    /// <summary>
    /// Katalog yarısı ile ClickHouse yarısını <b>varlık</b> düzeyinde
    /// karşılaştırır.
    ///
    /// <para>
    /// Oranlar karşılaştırılmıyor ve bu bilinçli: tohumlama Zipf ağırlıklı, yani
    /// aynı alan veritabanında bambaşka bir oranla dolu olabilir ve bu bir arıza
    /// değil. Arıza olan tek şey, katalogda dolan bir alanın veritabanında
    /// <b>hiç</b> dolmaması — yükleyici her satırı en az bir kez yazdığı için o
    /// fark ancak yazma ya da görünüm yolunda bir kayıptan gelebilir.
    /// </para>
    /// </summary>
    private static int Compare(
        FieldCoverageReport catalog,
        IReadOnlyList<VendorFieldCoverage> stored,
        string ownerGroup)
    {
        Console.WriteLine();
        Console.WriteLine($"### ÇAPRAZ KONTROL — katalog ↔ events_ocsf (owner_group={ownerGroup})");

        if (stored.Count == 0)
        {
            Console.Error.WriteLine(
                $"  `{ownerGroup}` grubunda hiç satır yok. Önce `bizigo seed golden` koşturun; " +
                "ClickHouse yarısı bu hâlde her alanı boş görür ve fark 'kayıp' diye okunurdu.");
            return 1;
        }

        var mismatches = 0;

        foreach (var vendor in catalog.Vendors)
        {
            var match = stored.FirstOrDefault(entry =>
                string.Equals(entry.Vendor, vendor.Vendor, StringComparison.Ordinal));

            if (match is null)
            {
                Console.Error.WriteLine(
                    $"  {vendor.Vendor}: katalogda var, events_ocsf'te YOK. Yükleme eksik ya da " +
                    "`vendor` değeri yazımda değişmiş.");
                mismatches++;
                continue;
            }

            var lost = catalog.Aliases
                .Where(alias => vendor.Populated.GetValueOrDefault(alias) > 0
                    && match.Populated.GetValueOrDefault(alias) == 0)
                .ToList();

            var extra = catalog.Aliases
                .Where(alias => vendor.Populated.GetValueOrDefault(alias) == 0
                    && match.Populated.GetValueOrDefault(alias) > 0)
                .ToList();

            var missingKeys = vendor.AttributeKeys.Keys
                .Where(key => !match.AttributeKeys.ContainsKey(key))
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToList();

            if (lost.Count == 0 && extra.Count == 0 && missingKeys.Count == 0)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {vendor.Vendor,-10} tuttu ({match.Rows} satır, {match.AttributeKeys.Count} unmapped anahtarı)"));
                continue;
            }

            mismatches++;

            if (lost.Count > 0)
            {
                Console.Error.WriteLine(
                    $"  {vendor.Vendor}: katalogda DOLAN ama events_ocsf'te BOŞ: " +
                    string.Join(", ", lost) +
                    "  ← yazma ya da görünüm yolunda kayıp");
            }

            if (extra.Count > 0)
            {
                Console.Error.WriteLine(
                    $"  {vendor.Vendor}: katalogda boş ama events_ocsf'te dolu: " +
                    string.Join(", ", extra) +
                    "  ← veritabanında başka bir turdan kalma satır olabilir");
            }

            if (missingKeys.Count > 0)
            {
                Console.Error.WriteLine(
                    $"  {vendor.Vendor}: katalogda üretilen ama events_ocsf'te bulunmayan unmapped anahtarı: " +
                    string.Join(", ", missingKeys));
            }
        }

        Console.WriteLine();
        Console.WriteLine(mismatches == 0
            ? "Çapraz kontrol temiz: kataloğun doldurabildiği her alan events_ocsf'te de dolu."
            : $"{mismatches} vendor'da fark var — yukarıdaki satırlar sebebi söylüyor.");

        return mismatches == 0 ? 0 : 1;
    }

    private static string Clip(string text) =>
        text.Length <= 70 ? text : text[..67] + "…";
}
