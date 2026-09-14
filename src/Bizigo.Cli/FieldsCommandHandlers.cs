using Bizigo.Commands;
using System.Globalization;
using Bizigo.Commands.Fields;
using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.Cli;

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
        var outcome = await FieldsCommands.CoverageAsync(request, toolbox, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Ok)
        {
            Console.Error.WriteLine($"hata   {outcome.Failure.Message}");
            return outcome.Failure.Kind == CommandFailureKind.Unavailable ? 1 : 1;
        }

        var result = outcome.Payload;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             === alan kapsamı — altın örnekler → events_ocsf (T39) ===
             katalog   : {request.Catalog}
             görünüm   : {request.Migrations} içinden okundu, {result.ColumnCount} kolon
             örnek satır: {result.SampleCount}, {result.Report.Vendors.Count} vendor
             """));

        Print(result.Report);

        if (result.Stored is null)
        {
            // `null` "boş" DEĞİL: soru hiç sorulmadı. Bu cümle o farkı taşıyor.
            Console.WriteLine();
            Console.WriteLine(
                "ClickHouse yarısı atlandı (--clickhouse verilmedi). Katalog yarısı " +
                "\"ne üretilebiliyor\" diyor; \"ne yazılmış\" sorusu cevapsız kaldı.");
            return 0;
        }

        return Compare(result.Report, result.Stored, request.OwnerGroup);
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

            var permanent = vendor.NeverTogether.Where(static pair => pair.Structural).ToList();
            var incidental = vendor.NeverTogether.Where(static pair => !pair.Structural).ToList();

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  KESİŞİM — ikisi de dolu ama AYNI SATIRDA hiç birlikte değil " +
                $"({permanent.Count} kalıcı, {incidental.Count} örneklem tesadüfü):"));

            Console.WriteLine("    KALICI (parser kümeleri ayrık — örneklem büyüse de değişmez):");
            Console.WriteLine("      " + (permanent.Count == 0
                ? "(yok)"
                : string.Join(", ", permanent.Select(static pair => $"{pair.First}+{pair.Second}"))));

            Console.WriteLine("    ÖRNEKLEM TESADÜFÜ (bir parser ikisini de doldurabiliyor):");
            Console.WriteLine("      " + (incidental.Count == 0
                ? "(yok)"
                : string.Join(", ", incidental.Select(static pair => $"{pair.First}+{pair.Second}"))));

            var substituted = vendor.Substituted
                .Where(pair => pair.Value > 0)
                .OrderByDescending(static pair => pair.Value)
                .Select(pair => string.Create(
                    CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}/{vendor.Lines}"))
                .ToList();

            Console.WriteLine("  DOLU AMA DEĞERİ SATIRDAN GELMİYOR (sabit ya da geri düşüş):");
            Console.WriteLine("    " + (substituted.Count == 0 ? "(yok)" : string.Join("  ", substituted)));

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

    /// <summary>
    /// <c>bizigo fields values</c> — kolonların taşıyabildiği değerleri çıkarır,
    /// istenirse Sigma kurallarıyla birleştirir.
    ///
    /// <para>
    /// Bu ölçüm <b>veriye hiç bakmıyor</b> ve bakmaması asıl özelliği: örneklemde
    /// bir değerin bulunmaması "bugün yok", şemanın onu üretememesi "hiçbir zaman
    /// olmayacak" demek. İkisi Kapı 3'ün tablosunda aynı görünüyor ve verdikleri
    /// iş emri zıt.
    /// </para>
    /// </summary>
    public static int Values(
        string catalogDirectory,
        string mappingsDirectory,
        string migrationsDirectory,
        string? rulesJson,
        string pipelinePath)
    {
        var outcome = FieldsCommands.Values(catalogDirectory, mappingsDirectory, migrationsDirectory);

        if (!outcome.Ok)
        {
            Console.Error.WriteLine($"hata   {outcome.Failure.Message}");
            return 1;
        }

        var spaces = outcome.Payload.Spaces;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             === kolon değer uzayları (T39) ===
             katalog : {catalogDirectory}
             tablolar: {mappingsDirectory}
             görünüm : {outcome.Payload.ColumnCount} kolon
             """));

        foreach (var space in spaces)
        {
            Console.WriteLine();
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"=== {space.Vendor} ({string.Join(", ", space.Products)}) ==="));

            foreach (var column in space.Columns.Values.OrderBy(
                         static column => column.Alias, StringComparer.Ordinal))
            {
                var body = column.Kind switch
                {
                    ValueSpaceKind.Open => "AÇIK — cihaz ne yazarsa",
                    ValueSpaceKind.Absent => "YOK",
                    _ => "KAPALI: " + string.Join(" · ", column.Values),
                };

                Console.WriteLine($"  {column.Alias,-32} {Clip(body)}");

                if (column.Kind == ValueSpaceKind.Closed)
                {
                    Console.WriteLine($"  {string.Empty,-32} ← {string.Join(", ", column.Sources)}");
                }
            }

            if (space.UnreachableCoreFields.Count > 0)
            {
                // Parser dolduruyor, normalizasyon hiçbir kolona taşımıyor.
                Console.Error.WriteLine(
                    "  ⚠ hiçbir kolona ulaşmayan `core` alanı: " +
                    string.Join(", ", space.UnreachableCoreFields));
            }
        }

        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            Console.WriteLine();
            Console.WriteLine(
                "Kural birleştirmesi atlandı (--rules verilmedi). Üretmek için:\n" +
                "  python3 prototypes/t30-sigma/explain_misses.py --json /tmp/misses.json");
            return 0;
        }

        // `--rules` birleştirmesi CLI'DA KALIYOR ve MCP aracına girmiyor:
        // girdisi depo dışından gelen bir JSON dosyası ve bir modelin onu
        // üretmesinin yolu yok. Asimetri BİLEREK ve M02 raporunda yazılı.
        return JoinRules(rulesJson, spaces, outcome.Payload.Columns, pipelinePath);
    }

    private static int JoinRules(
        string rulesJson,
        IReadOnlyList<VendorValueSpace> spaces,
        IReadOnlyList<OcsfViewColumn> columns,
        string pipelinePath)
    {
        var rules = RuleReachability.ReadRules(rulesJson);
        var fieldMap = SigmaFieldMap.Read(pipelinePath);

        // Ham gövdenin takma adı görünüm dosyasından türetiliyor: kaynağı
        // `body` olan kolon. Elle "raw_data" yazmak, görünüm yeniden
        // adlandırıldığı gün sessizce yanlış sınıflandırmaya yol açardı.
        var rawTextAlias = columns.Single(static column => column.Source == "body").Alias;
        var reaches = RuleReachability.Join(rules, spaces, fieldMap, rawTextAlias);

        var unreachable = reaches.Where(static item => item.Verdict == ReachVerdict.Unreachable).ToList();
        var gaps = reaches.Where(static item => item.Verdict == ReachVerdict.ParserGap).ToList();
        var rawText = reaches.Where(static item => item.Verdict == ReachVerdict.RawText).ToList();
        var unmapped = reaches.Where(static item => item.Verdict == ReachVerdict.UnmappedAccess).ToList();
        var unknown = reaches.Where(static item => item.Verdict == ReachVerdict.Unknown).ToList();
        var mistaken = reaches.Where(static item => item.TextAxisWrong).ToList();

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             === kural × değer uzayı ({rules.Count} kural, {reaches.Count} dizge) ===
             alan çevirisi: {pipelinePath} ({fieldMap.Count} giriş)
             ERİŞİLEMEZ     : {unreachable.Count} dizge — şema söylüyor, örneklem değil
             PARSER BOŞLUĞU : {gaps.Count} dizge — vendor'da açık ama bazı parser'lar hiç doldurmuyor
             ham metin      : {rawText.Count} dizge — `{rawTextAlias} ILIKE …`, indeks kullanılmıyor
             unmapped erişimi: {unmapped.Count} dizge — `unmapped['…']`, Map araması, yine indekssiz
             uzay açık      : {unknown.Count} dizge — şema bir şey demiyor
             erişilebilir   : {reaches.Count - unreachable.Count - gaps.Count - rawText.Count - unmapped.Count - unknown.Count} dizge
             """));

        var rawTextRules = rawText.Select(static item => item.Rule).Distinct(StringComparer.Ordinal).Count();

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             ### KAPSAM GÖSTERGESİ — ham metne vuran kural: {rawTextRules}/{rules.Count}
             Bir detection kuralı ham gövdeye vuruyorsa yapısal alan aramıyor demektir:
             tam metin taraması, indeksten yararlanmıyor ve maliyeti kural sayısıyla
             çarpılıyor. Bu bir tasarım tercihi olabilir; sayının büyümesi bir kalem.
             """));

        PrintAbsentBoxCorrection(rules, reaches);

        Section("ERİŞİLEMEZ — örneklem düzelse de eşleşmez", unreachable);
        Section("PARSER BOŞLUĞU — kural o parser'ın satırlarına vuruyorsa eşleşemez", gaps);
        Section("METİN EKSENİ YANILIYOR — ham satırda yok ama kolonda VAR", mistaken);

        Console.WriteLine();
        Console.WriteLine(
            "Okuma notu: bu üç liste `explain_misses.py`'nin metin ekseninden BAĞIMSIZ.\n" +
            "Orada 'desen YOK' çıkan bir kural örneklem büyüyünce eşleşebilir; burada\n" +
            "ERİŞİLEMEZ çıkan bir kural, örneklem ne olursa olsun eşleşmez. Üçüncü liste\n" +
            "ters yönü gösteriyor: metin ekseninin 'yok' dediği bir değer, eşleme tablosu\n" +
            "onu üretiyorsa kolonda vardır ve kural doğrudur.");

        return 0;
    }

    /// <summary>
    /// <b>Metin ekseninin `absent` kutusunun düzeltmesi.</b>
    ///
    /// <para>
    /// Bir eşleme tablosu cihazın sözcüğünü normalleştiriyorsa
    /// (<c>failed → failure</c>), kuralın aradığı normalleştirilmiş değer ham
    /// satırda <b>hiç geçmez</b> ve metin ekseninde <c>absent</c> görünür. O
    /// kutu bu yüzden bir <b>üst sınır</b>: her elemanı örneklem boşluğu değil.
    /// </para>
    ///
    /// <para>
    /// Kapsam oranının paydası <c>absent</c> kutusu düşülerek kuruluyor, yani
    /// bu düzeltme <b>doğrudan paydayı oynatıyor</b>. Kural düzeyinde
    /// hesaplanıyor çünkü metin ekseninin kutusu da kural düzeyinde: bir
    /// kuralın BÜTÜN <c>absent</c> dizgeleri normalleştirmeyle açıklanıyorsa o
    /// kural kutudan tamamen çıkar.
    /// </para>
    /// </summary>
    private static void PrintAbsentBoxCorrection(
        IReadOnlyList<RuleEntry> rules,
        IReadOnlyList<LiteralReach> reaches)
    {
        var absentRules = rules
            .Where(static rule => string.Equals(rule.Verdict, "absent", StringComparison.Ordinal))
            .ToList();

        if (absentRules.Count == 0)
        {
            return;
        }

        var byRule = reaches
            .GroupBy(static item => item.Rule, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);

        var leaves = new List<string>();
        var partial = new List<string>();

        foreach (var rule in absentRules)
        {
            var absentLiterals = byRule.GetValueOrDefault(rule.Name, [])
                .Where(static item => string.Equals(item.Literal.Verdict, "absent", StringComparison.Ordinal))
                .ToList();

            if (absentLiterals.Count == 0)
            {
                continue;
            }

            var explained = absentLiterals.Count(static item => item.TextAxisWrong);

            if (explained == absentLiterals.Count)
            {
                leaves.Add(rule.Name);
            }
            else if (explained > 0)
            {
                partial.Add(rule.Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             ### `absent` KUTUSUNUN DÜZELTMESİ — {absentRules.Count} kuraldan {leaves.Count}'i çıkıyor
             Kapsam oranının paydası `absent` düşülerek kuruluyor; bu satır paydayı oynatıyor.
             Tamamen çıkan: {(leaves.Count == 0 ? "(yok)" : string.Join(", ", leaves))}
             Kısmen açıklanan: {(partial.Count == 0 ? "(yok)" : string.Join(", ", partial))}
             Gerekçe: eşleme tablosu cihazın sözcüğünü çeviriyor, kuralın aradığı değer
             kolonda GERÇEKTEN var; ham satırda geçmemesi örneklem boşluğu değil.
             """));
    }

    private static void Section(string title, IReadOnlyList<LiteralReach> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"### {title}");

        foreach (var item in items.OrderBy(static item => item.Rule, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {item.Rule}  [{item.Vendor}]  {item.Literal.Field}|{item.Literal.Operator} = '{item.Literal.Value}'"));
            Console.WriteLine($"      {item.Reason}");
        }
    }
}
