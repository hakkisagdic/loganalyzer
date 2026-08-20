using System.CommandLine;
using Bizigo.Cli;
using Bizigo.Cli.Seeding;
using Bizigo.Storage.ClickHouse;

var patternsOption = new Option<DirectoryInfo?>("--patterns")
{
    Description = "Grok pattern dizini (varsayılan: catalog/patterns/legacy, ya da BIZIGO_PATTERNS).",
};

var mappingsOption = new Option<DirectoryInfo?>("--mappings")
{
    Description = "Eşleme tablosu dizini (varsayılan: catalog/mappings, ya da BIZIGO_MAPPINGS).",
};

var filesArgument = new Argument<FileInfo[]>("dosyalar")
{
    Description = "Parser YAML dosyaları veya dizinleri.",
    Arity = ArgumentArity.OneOrMore,
};

var lintCommand = new Command("lint", "Şema doğrulaması + ReDoS taraması.");
lintCommand.Arguments.Add(filesArgument);
lintCommand.Options.Add(patternsOption);
lintCommand.Options.Add(mappingsOption);
lintCommand.SetAction(parse => ParserCommandHandlers.Lint(
    parse.GetValue(filesArgument) ?? [],
    Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption))));

var testCommand = new Command("test", "YAML'ın gömülü `tests` bloğunu koşturur.");
testCommand.Arguments.Add(filesArgument);
testCommand.Options.Add(patternsOption);
testCommand.Options.Add(mappingsOption);
testCommand.SetAction(parse => ParserCommandHandlers.Test(
    parse.GetValue(filesArgument) ?? [],
    Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption))));

var catalogArgument = new Argument<DirectoryInfo>("dizin")
{
    Description = "Parser kataloğu dizini.",
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine("catalog", "parsers")),
};

var allowedFailedOption = new Option<double>("--allow-failed")
{
    Description = "İzin verilen failed satır yüzdesi (varsayılan 0).",
    DefaultValueFactory = _ => 0,
};

var coverageCommand = new Command(
    "coverage",
    "Altın örnek dosyalarını dispatcher'dan geçirir; ok/partial/failed oranını raporlar.");
coverageCommand.Arguments.Add(catalogArgument);
coverageCommand.Options.Add(allowedFailedOption);
coverageCommand.Options.Add(patternsOption);
coverageCommand.Options.Add(mappingsOption);
coverageCommand.SetAction(parse => ParserCommandHandlers.Coverage(
    parse.GetValue(catalogArgument)!,
    parse.GetValue(allowedFailedOption),
    Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption))));

var fileArgument = new Argument<FileInfo>("dosya") { Description = "Parser YAML dosyası." };
var inputOption = new Option<string?>("--input", "-i") { Description = "Denenecek tek satır." };
var inputFileOption = new Option<FileInfo?>("--input-file") { Description = "Satır satır denenecek dosya." };
var jsonOption = new Option<bool>("--json") { Description = "Çıktıyı JSON olarak ver." };

var tryCommand = new Command("try", "Tek satırı dener ve çözülen alanları gösterir.");
tryCommand.Arguments.Add(fileArgument);
tryCommand.Options.Add(inputOption);
tryCommand.Options.Add(inputFileOption);
tryCommand.Options.Add(jsonOption);
tryCommand.Options.Add(patternsOption);
tryCommand.Options.Add(mappingsOption);
tryCommand.SetAction(parse => ParserCommandHandlers.Try(
    parse.GetValue(fileArgument)!,
    parse.GetValue(inputOption),
    parse.GetValue(inputFileOption),
    parse.GetValue(jsonOption),
    Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption))));

var parserCommand = new Command("parser", "Parser plugin'leriyle çalışır.");
parserCommand.Subcommands.Add(lintCommand);
parserCommand.Subcommands.Add(testCommand);
parserCommand.Subcommands.Add(tryCommand);
parserCommand.Subcommands.Add(coverageCommand);

var directoryArgument = new Argument<string>("dizin")
{
    Description = "Göç dosyalarının dizini.",
    DefaultValueFactory = _ => Path.Combine("db", "clickhouse"),
};

var migrateCommand = new Command("migrate", "ClickHouse göçlerini uygular.");
migrateCommand.Arguments.Add(directoryArgument);
migrateCommand.SetAction(async (parse, cancellationToken) =>
{
    var connectionString = Environment.GetEnvironmentVariable("BIZIGO_CLICKHOUSE")
        ?? "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo";

    using var context = new ClickHouseContext(new ClickHouseOptions { ConnectionString = connectionString });
    var result = await new ClickHouseMigrator(context)
        .MigrateAsync(parse.GetValue(directoryArgument)!, cancellationToken)
        .ConfigureAwait(false);

    foreach (var version in result.Applied)
    {
        Console.WriteLine($"uygulandı  {version}");
    }

    foreach (var version in result.AlreadyApplied)
    {
        Console.WriteLine($"zaten var  {version}");
    }

    Console.WriteLine($"toplam: {result.Applied.Count} yeni, {result.AlreadyApplied.Count} mevcut");
    return 0;
});

var schemaCommand = new Command("schema", "Depolama şeması işlemleri.");
schemaCommand.Subcommands.Add(migrateCommand);

// ── seed golden (T39) ───────────────────────────────────────────────────────
// İki F3 ölçümü de gerçek veri istiyor: Sigma kapsamı (T30) vendor başına
// sayıyor, baseline penceresi (T35) tabanı 1 saatten 30 güne süpürüyor. İkisinin
// de ön kontrolü sentetik kıyaslama verisini reddediyor ve doğru yapıyor.
var seedCatalogArgument = new Argument<DirectoryInfo>("dizin")
{
    Description = "Parser kataloğu dizini (altın örnekler `<id>/samples/*.log` altında).",
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine("catalog", "parsers")),
};

var masksOption = new Option<FileInfo?>("--masks")
{
    Description = "Maskeleme sözlüğü (varsayılan: catalog/masks/bizigo-masks.yaml).",
};

var clickHouseOption = new Option<string?>("--clickhouse")
{
    Description = "ClickHouse bağlantı dizesi (varsayılan: BIZIGO_CLICKHOUSE ortam değişkeni).",
};

var ownerGroupOption = new Option<string>("--owner-group")
{
    Description = "Yükleyicinin yazdığı TEK kapsam grubu.",
    DefaultValueFactory = _ => "golden",
};

var spanOption = new Option<int>("--span-days")
{
    Description = "Zaman yayılımının uzunluğu (gün). Baseline ölçümü 30 güne kadar süpürüyor.",
    DefaultValueFactory = _ => 30,
};

var eventsOption = new Option<int>("--events")
{
    Description = "Hedeflenen toplam olay sayısı.",
    DefaultValueFactory = _ => 120_000,
};

var zipfOption = new Option<double>("--zipf")
{
    Description = "Sıklık yasasının üssü; büyüdükçe kuyruk seyrekleşir ve eğrinin dirseği uzaklaşır.",
    DefaultValueFactory = _ => 2.0,
};

var seedOption = new Option<int>("--seed")
{
    Description = "Deterministik üretim tohumu.",
    DefaultValueFactory = _ => 39,
};

var anchorOption = new Option<DateTimeOffset?>("--anchor")
{
    Description = "Yayılımın sağ ucu (varsayılan: şimdi, UTC).",
};

var batchRowsOption = new Option<int>("--batch-rows")
{
    Description = "Tek INSERT'e giden satır sayısı.",
    DefaultValueFactory = _ => 20_000,
};

var replaceOption = new Option<bool>("--replace")
{
    Description = "Kapsam grubunun mevcut satırlarını sil ve yeniden yaz. YALNIZCA o grubu etkiler.",
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "ClickHouse'a hiç dokunma: üret, zaman damgası bekçisini koştur, raporla.",
};

var seedGoldenCommand = new Command(
    "golden",
    "Altın örnekleri gerçek boru hattından geçirip ClickHouse'a yazar (T39).");
seedGoldenCommand.Arguments.Add(seedCatalogArgument);
seedGoldenCommand.Options.Add(masksOption);
seedGoldenCommand.Options.Add(clickHouseOption);
seedGoldenCommand.Options.Add(ownerGroupOption);
seedGoldenCommand.Options.Add(spanOption);
seedGoldenCommand.Options.Add(eventsOption);
seedGoldenCommand.Options.Add(zipfOption);
seedGoldenCommand.Options.Add(seedOption);
seedGoldenCommand.Options.Add(anchorOption);
seedGoldenCommand.Options.Add(batchRowsOption);
seedGoldenCommand.Options.Add(replaceOption);
seedGoldenCommand.Options.Add(dryRunOption);
seedGoldenCommand.Options.Add(patternsOption);
seedGoldenCommand.Options.Add(mappingsOption);
seedGoldenCommand.SetAction((parse, cancellationToken) =>
{
    var catalog = parse.GetValue(seedCatalogArgument)!;

    // Saniyeye indiriliyor: örnek biçimlerin çoğu saniyenin altını taşımıyor ve
    // ekilen an ile yeniden yazılan satır birbirini tutmak zorunda.
    var anchor = parse.GetValue(anchorOption) ?? DateTimeOffset.UtcNow;
    anchor = new DateTimeOffset(anchor.Ticks - (anchor.Ticks % TimeSpan.TicksPerSecond), anchor.Offset)
        .ToUniversalTime();

    var request = new SeedGoldenRequest(
        Catalog: catalog.FullName,
        MaskFile: parse.GetValue(masksOption)?.FullName
            ?? Path.Combine(catalog.Parent?.FullName ?? ".", "masks", "bizigo-masks.yaml"),
        ConnectionString: parse.GetValue(clickHouseOption)
            ?? Environment.GetEnvironmentVariable("BIZIGO_CLICKHOUSE")
            ?? "Host=localhost;Port=8123;Database=bizigo;Username=bizigo;Password=bizigo",
        OwnerGroup: parse.GetValue(ownerGroupOption)!,
        Plan: new SeedPlanOptions(
            Anchor: anchor,
            Span: TimeSpan.FromDays(parse.GetValue(spanOption)),
            TotalEvents: parse.GetValue(eventsOption),
            ZipfExponent: parse.GetValue(zipfOption),
            Seed: parse.GetValue(seedOption)),
        BatchRows: parse.GetValue(batchRowsOption),
        Replace: parse.GetValue(replaceOption),
        DryRun: parse.GetValue(dryRunOption));

    return SeedCommandHandlers.Golden(
        request,
        Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption)),
        cancellationToken);
});

var seedCommand = new Command("seed", "Ölçüm ve geliştirme verisi yükler.");
seedCommand.Subcommands.Add(seedGoldenCommand);

// ── fields coverage (T39) ───────────────────────────────────────────────────
// Kapı 3'ün boş kuralları iki bambaşka sebepten boş olabiliyor: eşleme
// eksikliği ya da örneklemde desen olmaması. Tabloda ikisi de "boş kolon"
// görünüyor.
var migrationsOption = new Option<DirectoryInfo?>("--migrations")
{
    Description = "ClickHouse göç dizini — `events_ocsf` kolon listesi oradan okunuyor.",
};

var fieldsCoverageCommand = new Command(
    "coverage",
    "Altın örneklerin taşıdığı bilginin ne kadarının events_ocsf'e ALAN olarak indiğini ölçer.");
fieldsCoverageCommand.Arguments.Add(seedCatalogArgument);
fieldsCoverageCommand.Options.Add(masksOption);
fieldsCoverageCommand.Options.Add(migrationsOption);
fieldsCoverageCommand.Options.Add(clickHouseOption);
fieldsCoverageCommand.Options.Add(ownerGroupOption);
fieldsCoverageCommand.Options.Add(anchorOption);
fieldsCoverageCommand.Options.Add(patternsOption);
fieldsCoverageCommand.Options.Add(mappingsOption);
fieldsCoverageCommand.SetAction((parse, cancellationToken) =>
{
    var catalog = parse.GetValue(seedCatalogArgument)!;
    var anchor = (parse.GetValue(anchorOption) ?? DateTimeOffset.UtcNow).ToUniversalTime();
    anchor = new DateTimeOffset(anchor.Ticks - (anchor.Ticks % TimeSpan.TicksPerSecond), anchor.Offset);

    var request = new FieldCoverageRequest(
        Catalog: catalog.FullName,
        MaskFile: parse.GetValue(masksOption)?.FullName
            ?? Path.Combine(catalog.Parent?.FullName ?? ".", "masks", "bizigo-masks.yaml"),
        Migrations: parse.GetValue(migrationsOption)?.FullName
            ?? Path.Combine("db", "clickhouse"),
        // Bağlantı verilmezse ClickHouse yarısı atlanıyor: katalog yarısı
        // Docker'sız koşabilmeli, yoksa alan eksiği ancak konteyner turuyla
        // görülebilirdi.
        ConnectionString: parse.GetValue(clickHouseOption)
            ?? Environment.GetEnvironmentVariable("BIZIGO_CLICKHOUSE"),
        OwnerGroup: parse.GetValue(ownerGroupOption)!,
        Anchor: anchor);

    return FieldsCommandHandlers.Coverage(
        request,
        Toolbox(parse.GetValue(patternsOption), parse.GetValue(mappingsOption)),
        cancellationToken);
});

var fieldsCommand = new Command("fields", "Alan kapsamı ölçümleri.");
fieldsCommand.Subcommands.Add(fieldsCoverageCommand);

var root = new RootCommand("bizigo — log analyzer CLI");
root.Subcommands.Add(parserCommand);
root.Subcommands.Add(schemaCommand);
root.Subcommands.Add(seedCommand);
root.Subcommands.Add(fieldsCommand);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static ParserToolbox Toolbox(DirectoryInfo? patterns, DirectoryInfo? mappings) =>
    ParserToolbox.Create(patterns?.FullName, mappings?.FullName);
