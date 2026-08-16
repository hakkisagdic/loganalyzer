using System.CommandLine;
using Bizigo.Cli;
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

var root = new RootCommand("bizigo — log analyzer CLI");
root.Subcommands.Add(parserCommand);
root.Subcommands.Add(schemaCommand);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static ParserToolbox Toolbox(DirectoryInfo? patterns, DirectoryInfo? mappings) =>
    ParserToolbox.Create(patterns?.FullName, mappings?.FullName);
