using Bizigo.Commands;
using System.CommandLine;
using Bizigo.Cli;
using Bizigo.Commands.Seeding;
using Bizigo.Mcp;
using Bizigo.Parsing.Samples;
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

// ── fleet apply (S05) ───────────────────────────────────────────────────────
// Uçtan uca harness kapsam eşlemesini ve kaynak envanterini ELLE SQL ile
// kuruyordu; yani ekran görüntüsü koşumunun gördüğü envanter ürünün ürettiği
// envanter değildi. Bu komut o iki adımın yerine geçiyor ve kaynağı tek bir
// dosya: `catalog/simulators/filo.yaml`.
var fleetCatalogArgument = new Argument<DirectoryInfo>("dizin")
{
    Description = "Simülatör profilleri ve filo dosyasının dizini.",
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine("catalog", "simulators")),
};

var fleetApplyCommand = new Command("apply", "Filo tanımını kontrol düzlemine yazar.");
fleetApplyCommand.Arguments.Add(fleetCatalogArgument);
fleetApplyCommand.SetAction(async (parse, cancellationToken) =>
{
    var catalog = parse.GetValue(fleetCatalogArgument)!;

    // Depo kökü, katalog dizininin İKİ ÜSTÜ değil — açıkça çözülüyor. Profil
    // örnekleri `catalog/parsers/...` altında ve yanlış kök beş profili birden
    // "örnek dosya yok" yapıyor; ölçüldü.
    var repositoryRoot = catalog.Parent?.Parent?.FullName ?? Directory.GetCurrentDirectory();

    var connectionString = Environment.GetEnvironmentVariable("BIZIGO_CONTROLPLANE")
        ?? "Host=localhost;Port=5432;Database=bizigo;Username=bizigo;Password=bizigo";

    return await FleetCommandHandlers
        .ApplyAsync(catalog.FullName, repositoryRoot, connectionString, cancellationToken)
        .ConfigureAwait(false);
});

var fleetCommand = new Command("fleet", "Simüle cihaz filosu işlemleri.");
fleetCommand.Subcommands.Add(fleetApplyCommand);

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

var migrationsOption = new Option<DirectoryInfo?>("--migrations")
{
    Description = "ClickHouse göç dizini — saklama süresi (TTL) ve görünüm kolonları oradan okunuyor.",
};

var seedGoldenCommand = new Command(
    "golden",
    "Altın örnekleri gerçek boru hattından geçirip ClickHouse'a yazar (T39).");
seedGoldenCommand.Arguments.Add(seedCatalogArgument);
seedGoldenCommand.Options.Add(masksOption);
seedGoldenCommand.Options.Add(migrationsOption);
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
    // Kural tek yerde: SampleClock. Dört ayrı yerde yazılıydı ve beşincisi
    // simülatör olacaktı; ayrışmaları sessiz (biri TTL'e takılır, diğeri takılmaz).
    var anchor = parse.GetValue(anchorOption) is { } given
        ? SampleClock.Truncate(given)
        : SampleClock.Anchor();

    var request = new SeedGoldenRequest(
        Catalog: catalog.FullName,
        MaskFile: parse.GetValue(masksOption)?.FullName
            ?? Path.Combine(catalog.Parent?.FullName ?? ".", "masks", "bizigo-masks.yaml"),
        Migrations: parse.GetValue(migrationsOption)?.FullName
            ?? Path.Combine("db", "clickhouse"),
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
    var anchor = parse.GetValue(anchorOption) is { } given
        ? SampleClock.Truncate(given)
        : SampleClock.Anchor();

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

var rulesOption = new Option<FileInfo?>("--rules")
{
    Description = "explain_misses.py --json çıktısı; verilirse kurallarla birleştirilir.",
};

var pipelineOption = new Option<FileInfo?>("--pipeline")
{
    Description = "Sigma pipeline dosyası — alan adı çevirisi (FIELD_MAP) oradan okunuyor.",
};

var fieldsValuesCommand = new Command(
    "values",
    "Kolonların taşıyabildiği değerleri çıkarır — VERİYE BAKMADAN, eşleme tablolarından.");
fieldsValuesCommand.Arguments.Add(seedCatalogArgument);
fieldsValuesCommand.Options.Add(migrationsOption);
fieldsValuesCommand.Options.Add(mappingsOption);
fieldsValuesCommand.Options.Add(rulesOption);
fieldsValuesCommand.Options.Add(pipelineOption);
fieldsValuesCommand.SetAction(parse => FieldsCommandHandlers.Values(
    parse.GetValue(seedCatalogArgument)!.FullName,
    parse.GetValue(mappingsOption)?.FullName ?? Path.Combine("catalog", "mappings"),
    parse.GetValue(migrationsOption)?.FullName ?? Path.Combine("db", "clickhouse"),
    parse.GetValue(rulesOption)?.FullName,
    parse.GetValue(pipelineOption)?.FullName
        // ÜRÜNÜN pipeline'ı, prototipin değil.
        //
        // Varsayılan `prototypes/t30-sigma/bizigo_pipeline.py` idi ve T31
        // kalıcı modülü yazdıktan sonra o dosya **bayat** kaldı: prototipte 28
        // alan, üründe 32 (`http_method`, `user_name`, `cs_method`,
        // `TargetUserName` eksikti). Yani erişilebilirlik ölçümü, ürünün
        // eşlediği dört alanı "eşlenmemiş" sayıyordu — `SigmaFieldMap`'in
        // kendi yorumunun uyardığı hatanın bir kademe yukarısı.
        ?? Path.Combine("sidecar", "app", "sigma_pipeline.py")));

var fieldsCommand = new Command("fields", "Alan kapsamı ölçümleri.");
fieldsCommand.Subcommands.Add(fieldsCoverageCommand);
fieldsCommand.Subcommands.Add(fieldsValuesCommand);

// --- sigma sync ---------------------------------------------------------
//
// Senkron bir DAĞITIM hareketi, kullanıcı hareketi değil: kural seti çivili
// bir commit'ten geliyor ve manifest üretilmiş bir dosya. Kapsam beyanı da
// burada doğal duruyor — bir uçta olsaydı "kim hangi kapsamla tetikliyor"
// sorusu her çağrıda yeniden sorulurdu.
var manifestOption = new Option<FileInfo?>("--manifest")
{
    Description = $"Derleme hattının manifesti (varsayılan {SigmaCommands.DefaultManifest}).",
};

var ownerSubjectOption = new Option<string>("--owner-subject")
{
    Description = "Kuralları kaydeden kimlik — denetim kaydına giriyor.",
    Required = true,
};

// Kapsam ZORUNLU ve türetilmiyor: `logsource`'tan çıkarmak akla yatkın ama
// yanlış — vendor ile grup aynı şey değil. Beyanı zorunlu kılmak, "sınırsız
// kapsamlı kural yok" değişmezinin tek satırda delinmesini engelliyor (§8).
var sigmaOwnerGroupOption = new Option<string[]>("--owner-group")
{
    Description = "Kuralların koşacağı owner_group kümesi. Birden çok kez verilebilir.",
    Required = true,
    AllowMultipleArgumentsPerToken = true,
};

var connectionOption = new Option<string?>("--connection")
{
    Description = "ControlPlane bağlantı dizgesi (yoksa BIZIGO_CONTROLPLANE).",
};

var sigmaSyncCommand = new Command(
    "sync", "Derleme hattının manifestini alarm kurallarına yazar.");
sigmaSyncCommand.Options.Add(manifestOption);
sigmaSyncCommand.Options.Add(ownerSubjectOption);
sigmaSyncCommand.Options.Add(sigmaOwnerGroupOption);
sigmaSyncCommand.Options.Add(connectionOption);
sigmaSyncCommand.SetAction((parse, token) => SigmaSyncCommandHandler.RunAsync(
    parse.GetValue(manifestOption)?.FullName ?? SigmaCommands.DefaultManifest,
    parse.GetValue(ownerSubjectOption)!,
    parse.GetValue(sigmaOwnerGroupOption) ?? Array.Empty<string>(),
    parse.GetValue(connectionOption),
    token));

// M02: `--dry-run` bayrağı KENDİ KOMUTU oldu. Bayrak olarak kalsaydı MCP
// tarafında ilan edilebilecek tek şey YAZAN komut olurdu; okuma yarısı
// (`SigmaCommands.Plan`) bu depoda zaten saftı ve ilan edilmemesi için sebep
// yoktu. Yani bu bir komut BÖLMEK değil, zaten ayrı olan yarıyı ilan etmek.
var sigmaPlanCommand = new Command(
    "plan", "Hiçbir şey yazmaz; manifestin ne getireceğini gösterir.");
sigmaPlanCommand.Options.Add(manifestOption);
sigmaPlanCommand.SetAction((parse, token) => SigmaSyncCommandHandler.PlanAsync(
    parse.GetValue(manifestOption)?.FullName ?? SigmaCommands.DefaultManifest,
    token));

var sigmaCommand = new Command("sigma", "Sigma kural seti işlemleri.");
sigmaCommand.Subcommands.Add(sigmaSyncCommand);
sigmaCommand.Subcommands.Add(sigmaPlanCommand);

// MCP'nin stdio taşıması (M01). İkinci bir host projesi AÇILMADI: aynı
// komutların iki yerde kurulması M02'nin kaçınmak için var olduğu kopya olurdu.
var mcpSurfaceOption = new Option<string>("--surface")
{
    Description = $"Sunulacak MCP yüzeyi: '{McpSurfaces.ProductName}' ya da '{McpSurfaces.SimulatorName}'.",
    DefaultValueFactory = _ => McpSurfaces.ProductName,
};

// K6 beyanı (M06). `--surface`'in DefaultValueFactory'si var, bunun YOK ve
// olmamalı: yüzeyin makul bir varsayılanı var, ağ sınırının yok. Beyansız
// koşum reddediliyor — `McpCommandHandlers.ServeAsync` çıkış kodu 2 veriyor.
var mcpBoundaryOption = new Option<string?>("--data-boundary")
{
    Description =
        "ZORUNLU. Bu sunucuya bağlanacak istemcinin hangi tarafta olduğu: 'internal' ya da "
        + "'external'. Varsayılanı YOK — beyansız bir yüzey 'iç ağ' sayılmıyor (K6). "
        + "Dikkat: istemci aynı makinede olmak, iç ağda olmak DEĞİLDİR — masaüstü MCP "
        + "istemcilerinin çoğu aldığı metni buluta gönderiyor.",
};

var mcpVerboseOption = new Option<bool>("--verbose")
{
    Description = "Günlükleri stderr'e yaz. stdout PROTOKOLÜN kendisi; oraya hiçbir şey yazılmıyor.",
};

var mcpServeCommand = new Command("serve", "MCP sunucusunu stdio üzerinden koşturur.");
mcpServeCommand.Options.Add(mcpSurfaceOption);
mcpServeCommand.Options.Add(mcpBoundaryOption);
mcpServeCommand.Options.Add(mcpVerboseOption);
mcpServeCommand.SetAction((parse, token) => McpCommandHandlers.ServeAsync(
    parse.GetValue(mcpSurfaceOption)!,
    parse.GetValue(mcpBoundaryOption),
    parse.GetValue(mcpVerboseOption),
    token));

var mcpCommand = new Command("mcp", "Model Context Protocol sunucusu.");
mcpCommand.Subcommands.Add(mcpServeCommand);

var root = new RootCommand("bizigo — log analyzer CLI");
root.Subcommands.Add(parserCommand);
root.Subcommands.Add(schemaCommand);
root.Subcommands.Add(fleetCommand);
root.Subcommands.Add(seedCommand);
root.Subcommands.Add(fieldsCommand);
root.Subcommands.Add(sigmaCommand);
root.Subcommands.Add(mcpCommand);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static ParserToolbox Toolbox(DirectoryInfo? patterns, DirectoryInfo? mappings) =>
    ParserToolbox.Create(patterns?.FullName, mappings?.FullName);
