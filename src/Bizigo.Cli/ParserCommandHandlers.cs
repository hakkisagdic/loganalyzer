using System.Globalization;
using System.Text.Json;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Testing;

namespace Bizigo.Cli;

internal static class ParserCommandHandlers
{
    public static int Lint(IReadOnlyList<FileInfo> files, ParserToolbox toolbox)
    {
        var exitCode = 0;

        foreach (var file in Expand(files))
        {
            var report = ParserLinter.LintFile(file.FullName, toolbox.Compiler);

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
                writer.WriteLine($"{label} {file.Name} [{finding.Code}] {finding.Message}");
                if (finding.Fragment.Length > 0)
                {
                    writer.WriteLine($"        → {finding.Fragment}");
                }
            }

            if (report.HasErrors)
            {
                exitCode = 1;
            }
            else if (!report.HasWarnings)
            {
                Console.WriteLine($"tamam  {file.Name} ({report.ParserId})");
            }
        }

        return exitCode;
    }

    public static int Test(IReadOnlyList<FileInfo> files, ParserToolbox toolbox)
    {
        var exitCode = 0;
        var totalPassed = 0;
        var totalFailed = 0;

        foreach (var file in Expand(files))
        {
            var compiled = toolbox.Compiler.CompileFile(file.FullName);
            if (!compiled.Ok)
            {
                foreach (var error in compiled.Errors)
                {
                    Console.Error.WriteLine($"hata   {error}");
                }

                exitCode = 1;
                continue;
            }

            var report = ParserTestRunner.Run(compiled.Value);
            totalPassed += report.PassCount;
            totalFailed += report.FailCount;

            Console.WriteLine($"{report.ParserId}  ({file.Name})");

            foreach (var test in report.Tests)
            {
                if (test.Passed)
                {
                    Console.WriteLine($"  ✓ {test.Name}");
                    continue;
                }

                exitCode = 1;
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

        Console.WriteLine($"toplam: {totalPassed} geçti, {totalFailed} kaldı");
        return exitCode;
    }

    public static int Try(FileInfo file, string? input, FileInfo? inputFile, bool asJson, ParserToolbox toolbox)
    {
        var compiled = toolbox.Compiler.CompileFile(file.FullName);
        if (!compiled.Ok)
        {
            foreach (var error in compiled.Errors)
            {
                Console.Error.WriteLine($"hata   {error}");
            }

            return 1;
        }

        var lines = ReadInput(input, inputFile);
        if (lines.Count == 0)
        {
            Console.Error.WriteLine("hata   girdi yok: --input, --input-file veya stdin kullanın.");
            return 2;
        }

        var parser = compiled.Value;
        var failed = false;

        foreach (var line in lines)
        {
            var result = parser.Parse(line);
            failed |= result.Status == Contracts.ParseStatus.Failed;

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

        return failed ? 1 : 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
            return File.ReadAllLines(inputFile.FullName)
                .Where(static line => line.Length > 0)
                .ToArray();
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

    /// <summary>Dizin verilirse içindeki tüm YAML'lar alınır — katalog tümüne tek komut.</summary>
    private static IEnumerable<FileInfo> Expand(IReadOnlyList<FileInfo> files)
    {
        foreach (var file in files)
        {
            if (Directory.Exists(file.FullName))
            {
                foreach (var path in Directory
                             .EnumerateFiles(file.FullName, "*.yaml", SearchOption.AllDirectories)
                             .OrderBy(static path => path, StringComparer.Ordinal))
                {
                    yield return new FileInfo(path);
                }

                continue;
            }

            yield return file;
        }
    }
}
