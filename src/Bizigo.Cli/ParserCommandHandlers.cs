using System.Globalization;
using System.Text.Json;

using Bizigo.Commands;
using Bizigo.Contracts;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Testing;

namespace Bizigo.Cli;

/// <summary>
/// Parser komutlarının <b>CLI sunumu</b> (M02).
///
/// <para>
/// <b>Bu dosya artık iş yapmıyor — çiziyor.</b> Karar
/// <c>Bizigo.Commands.ParserCommands</c>'ta ve bir <b>değer</b> döndürüyor;
/// burada o değer konsola çiziliyor ve bir çıkış koduna çevriliyor. Aynı değeri
/// MCP tarafı kendi yüküne çeviriyor.
/// </para>
///
/// <para>
/// Ayrım M02'nin ölçtüğü sayıdan çıktı: hesap katmanında <c>Console</c> çağrısı
/// <b>sıfır</b>, sunum katmanında <b>117</b>. Ortaklaştırılacak şey iş mantığı
/// değildi — o zaten ortaktı; ortaklaşamayan şey <b>sonucun kendisiydi</b>.
/// </para>
/// </summary>
internal static class ParserCommandHandlers
{
    public static int Lint(IReadOnlyList<FileInfo> files, ParserToolbox toolbox)
    {
        var outcome = ParserCommands.Lint([.. files.Select(static f => f.FullName)], toolbox);

        if (!outcome.Ok)
        {
            return Fail(outcome.Failure);
        }

        foreach (var report in outcome.Payload.Files)
        {
            var name = Path.GetFileName(report.Path);

            foreach (var error in report.SchemaErrors)
            {
                Console.Error.WriteLine($"hata   {error}");
            }

            foreach (var finding in report.RedosFindings)
            {
                var label = finding.Severity switch
                {
                    RedosSeverity.Error => "hata  ",
                    RedosSeverity.Warning => "uyarı ",
                    _ => "bilgi ",
                };

                var writer = finding.Severity == RedosSeverity.Error ? Console.Error : Console.Out;
                writer.WriteLine($"{label} {name} [{finding.Code}] {finding.Message}");

                if (finding.Fragment.Length > 0)
                {
                    writer.WriteLine($"        → {finding.Fragment}");
                }
            }

            if (!report.HasErrors && !report.HasWarnings)
            {
                Console.WriteLine($"tamam  {name} ({report.ParserId})");
            }
        }

        return outcome.Payload.HasErrors ? 1 : 0;
    }

    public static int Test(IReadOnlyList<FileInfo> files, ParserToolbox toolbox)
    {
        var outcome = ParserCommands.Test([.. files.Select(static f => f.FullName)], toolbox);

        if (!outcome.Ok)
        {
            return Fail(outcome.Failure);
        }

        foreach (var file in outcome.Payload.Files)
        {
            if (file.Report is null)
            {
                foreach (var error in file.CompileErrors)
                {
                    Console.Error.WriteLine($"hata   {error}");
                }

                continue;
            }

            var report = file.Report;
            Console.WriteLine($"{report.ParserId}  ({Path.GetFileName(file.Path)})");

            foreach (var test in report.Tests)
            {
                if (test.Passed)
                {
                    Console.WriteLine($"  ✓ {test.Name}");
                    continue;
                }

                Console.Error.WriteLine($"  ✗ {test.Name}  (satır {test.Line})");

                foreach (var failure in test.Failures)
                {
                    Console.Error.WriteLine(failure.Describe());
                }

                if (test.Parse is { } parse && parse.Issues.Count > 0)
                {
                    foreach (var issue in parse.Issues)
                    {
                        Console.Error.WriteLine($"      adım '{issue.Step}': {issue.Message}");
                    }
                }
            }
        }

        Console.WriteLine($"toplam: {outcome.Payload.PassCount} geçti, {outcome.Payload.FailCount} kaldı");

        return outcome.Payload.HasFailures ? 1 : 0;
    }

    /// <summary>
    /// Altın örnek dosyalarının kapsam raporu (T08). <c>test</c> komutundan
    /// farkı, satırların dispatcher'dan geçmesi: ön filtre ve "ilk `ok` kazanır"
    /// kuralı da ölçülüyor, dolayısıyla bir vendor'ın satırının başka bir
    /// parser'a düşmesi burada görünür.
    /// </summary>
    public static int Coverage(DirectoryInfo directory, double allowedFailedPercent, ParserToolbox toolbox)
    {
        var outcome = ParserCommands.Coverage(directory.FullName, allowedFailedPercent, toolbox);

        if (!outcome.Ok)
        {
            return Fail(outcome.Failure);
        }

        var report = outcome.Payload.Report;
        var root = directory.FullName;

        foreach (var file in report.Files)
        {
            var name = Path.GetRelativePath(root, file.Path);

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name,-52} {file.Total,5} satır   ok {file.OkPercent,6:F1}%   " +
                $"partial {file.PartialPercent,6:F1}%   failed {file.FailedPercent,6:F1}%"));

            foreach (var (parser, count) in file.ByParser.OrderByDescending(static pair => pair.Value)
                         .ThenBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"      → {parser,-40} {count,5}");
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"toplam: {report.Total} satır — ok {report.Ok}, partial {report.Partial}, " +
            $"failed {report.Failed} ({report.FailedPercent:F1}%)"));

        if (!outcome.Payload.WithinBudget)
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"hata   failed oranı {report.FailedPercent:F1}%, " +
                $"izin verilen üst sınır {allowedFailedPercent:F1}%."));

            return 1;
        }

        return 0;
    }

    public static int Try(FileInfo file, string? input, FileInfo? inputFile, bool asJson, ParserToolbox toolbox)
    {
        // stdin BURADA okunuyor, çekirdekte değil: stdin bir sunum ayrıntısı ve
        // MCP tarafında karşılığı yok. Çekirdek onu okusaydı, MCP çağrısı
        // hiç kullanmadığı bir yola bağımlı olurdu.
        var outcome = ParserCommands.Try(file.FullName, ReadInput(input, inputFile), toolbox);

        if (!outcome.Ok)
        {
            return Fail(outcome.Failure);
        }

        foreach (var line in outcome.Payload.Lines)
        {
            var result = line.Result;

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    parser_id = result.ParserId,
                    parser_version = result.ParserVersion,
                    parse_status = result.Status.ToString().ToLowerInvariant(),
                    timestamp = result.Timestamp,
                    core = result.Core,
                    ocsf = result.Ocsf,
                    otel = result.Otel,
                    fields = result.Fields,
                    tags = result.Tags,
                    issues = result.Issues,
                }, JsonOptions));

                continue;
            }

            Console.WriteLine($"parse_status: {result.Status.ToString().ToLowerInvariant()}   " +
                $"parser: {result.ParserId}@{result.ParserVersion}");

            if (result.Timestamp is { } timestamp)
            {
                Console.WriteLine($"@timestamp:   {timestamp.ToString("O", CultureInfo.InvariantCulture)}");
            }

            WriteSection("core", result.Core);
            WriteSection("ocsf", result.Ocsf);
            WriteSection("otel", result.Otel);
            WriteSection("fields", result.Fields);

            if (result.Tags.Count > 0)
            {
                Console.WriteLine($"tags:         {string.Join(", ", result.Tags)}");
            }

            foreach (var issue in result.Issues)
            {
                Console.Error.WriteLine($"  ! adım '{issue.Step}': {issue.Message}");
            }

            Console.WriteLine();
        }

        return outcome.Payload.Lines.Any(static l => l.Result.Status == ParseStatus.Failed) ? 1 : 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Sebebi bir çıkış koduna çeviren <b>tek</b> yer.
    ///
    /// <para>
    /// Kodlar taşınan davranışı koruyor: girdi hatası <c>2</c>, bulunamayan
    /// <c>2</c>, gerisi <c>1</c>. İkinci bir eşleme yazılsaydı iki komut aynı
    /// sebebe farklı kod verirdi ve betikler sessizce ayrışırdı.
    /// </para>
    /// </summary>
    private static int Fail(CommandFailure failure)
    {
        Console.Error.WriteLine($"hata   {failure.Message}");

        return failure.Kind switch
        {
            CommandFailureKind.InvalidArgument => 2,
            CommandFailureKind.NotFound => 2,
            _ => 1,
        };
    }

    private static void WriteSection(string name, IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        Console.WriteLine($"{name}:");

        foreach (var (key, value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {key,-24} {ValueFormatter.Format(value)}");
        }
    }

    private static IReadOnlyList<string> ReadInput(string? input, FileInfo? inputFile)
    {
        if (input is not null)
        {
            return [input];
        }

        if (inputFile is not null)
        {
            return [.. File.ReadAllLines(inputFile.FullName).Where(static line => line.Length > 0)];
        }

        if (Console.IsInputRedirected)
        {
            var lines = new List<string>();

            while (Console.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        return [];
    }
}
