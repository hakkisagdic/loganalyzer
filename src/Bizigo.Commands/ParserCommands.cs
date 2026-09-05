using Bizigo.Contracts;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Testing;

namespace Bizigo.Commands;

/// <param name="Files">Dosya başına lint raporu.</param>
public sealed record ParserLintOutcome(IReadOnlyList<ParserLintReport> Files)
{
    public bool HasErrors => Files.Any(static f => f.HasErrors);

    public bool HasWarnings => Files.Any(static f => f.HasWarnings);
}

/// <param name="Path">Dosya yolu.</param>
/// <param name="CompileErrors">Derlenemediyse sebepleri; derlendiyse boş.</param>
/// <param name="Report">Derlendiyse test raporu, aksi hâlde <see langword="null"/>.</param>
public sealed record ParserTestFileOutcome(
    string Path,
    IReadOnlyList<string> CompileErrors,
    ParserTestReport? Report);

public sealed record ParserTestOutcome(IReadOnlyList<ParserTestFileOutcome> Files)
{
    public int PassCount => Files.Sum(static f => f.Report?.PassCount ?? 0);

    public int FailCount => Files.Sum(static f => f.Report?.FailCount ?? 0);

    /// <summary>
    /// <b>Derlenemeyen bir dosya da başarısızlık.</b> Yalnızca
    /// <see cref="FailCount"/>'a bakan bir çağıran, hiç koşmamış bir dosyayı
    /// "sıfır hata" diye okurdu — ölçülmemiş olanı sorunsuz saymak.
    /// </summary>
    public bool HasFailures => FailCount > 0 || Files.Any(static f => f.CompileErrors.Count > 0);
}

/// <param name="Report">Kapsam raporu.</param>
/// <param name="AllowedFailedPercent">İzin verilen üst sınır.</param>
public sealed record ParserCoverageOutcome(SampleCoverageReport Report, double AllowedFailedPercent)
{
    public bool WithinBudget => Report.FailedPercent <= AllowedFailedPercent;
}

/// <param name="Line">Denenen satır.</param>
/// <param name="Result">Ayrıştırma sonucu.</param>
public sealed record ParserTryLine(string Line, ParseResult Result);

public sealed record ParserTryOutcome(string ParserId, IReadOnlyList<ParserTryLine> Lines)
{
    public bool AllResolved => Lines.All(static l => l.Result.Status == ParseStatus.Ok);
}

/// <summary>
/// Parser komutlarının <b>çekirdeği</b> — dört komut, dördü de yan etkisiz.
///
/// <para>
/// <b>Bu sınıf hesap yapmıyor.</b> Hesap <c>Bizigo.Parsing.Testing</c>'de ve
/// zaten değer döndürüyor (<c>ParserLintReport</c>, <c>ParserTestReport</c>,
/// <c>SampleCoverageReport</c>). Buradaki iş <b>toplama</b> ve <b>sınıflandırma</b>:
/// dosyaları genişletmek, dosya başına raporu birleştirmek, ve yürümeyen bir
/// çağrının <b>sebebini</b> taşımak.
/// </para>
///
/// <para>
/// <b>Sonuç tipleri hesap tiplerini SARMIYOR, taşıyor.</b> İkinci bir gösterim
/// yazmak (§9) sürüklenme üretirdi ve sürüklenmeyi hiçbir şey göremezdi. Telde
/// hangi alanın görüneceği <b>aracın</b> kararı: MCP şemaları elle yazılıyor
/// ve araç kendi yükünü şekillendiriyor — domain tipini olduğu gibi tele
/// koymak, §8'in tam olarak yasakladığı şey.
/// </para>
/// </summary>
public static class ParserCommands
{
    public static CommandOutcome<ParserLintOutcome> Lint(
        IReadOnlyList<string> files,
        ParserToolbox toolbox)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(toolbox);

        var expanded = Expand(files);

        if (expanded.Count == 0)
        {
            return CommandOutcome<ParserLintOutcome>.Failed(
                CommandFailureKind.NotFound, "Denetlenecek parser dosyası bulunamadı.");
        }

        return CommandOutcome<ParserLintOutcome>.Success(
            new ParserLintOutcome([.. expanded.Select(f => ParserLinter.LintFile(f, toolbox.Compiler))]));
    }

    public static CommandOutcome<ParserTestOutcome> Test(
        IReadOnlyList<string> files,
        ParserToolbox toolbox)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(toolbox);

        var expanded = Expand(files);

        if (expanded.Count == 0)
        {
            return CommandOutcome<ParserTestOutcome>.Failed(
                CommandFailureKind.NotFound, "Koşturulacak parser dosyası bulunamadı.");
        }

        var results = new List<ParserTestFileOutcome>(expanded.Count);

        foreach (var file in expanded)
        {
            var compiled = toolbox.Compiler.CompileFile(file);

            results.Add(compiled.Ok
                ? new ParserTestFileOutcome(file, [], ParserTestRunner.Run(compiled.Value))

                // Derlenemeyen dosya ATLANMIYOR: listeden düşseydi "hiç test
                // yok" ile "test koşamadı" aynı çıktıyı verirdi.
                : new ParserTestFileOutcome(file, [.. compiled.Errors.Select(e => e.ToString()!)], null));
        }

        return CommandOutcome<ParserTestOutcome>.Success(new ParserTestOutcome(results));
    }

    public static CommandOutcome<ParserCoverageOutcome> Coverage(
        string directory,
        double allowedFailedPercent,
        ParserToolbox toolbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(toolbox);

        var report = SampleCoverage.Run(directory, toolbox.Compiler);

        if (report.Files.Count == 0)
        {
            return CommandOutcome<ParserCoverageOutcome>.Failed(
                CommandFailureKind.NotFound,
                $"'{directory}' altında örnek dosyası yok " +
                $"({SampleCoverage.SamplesDirectoryName}/*.log bekleniyor).");
        }

        return CommandOutcome<ParserCoverageOutcome>.Success(
            new ParserCoverageOutcome(report, allowedFailedPercent));
    }

    /// <param name="file">Parser YAML'ı.</param>
    /// <param name="lines">Denenecek satırlar — <b>çağıran topluyor</b>.</param>
    /// <param name="toolbox">Derleyici kurulumu.</param>
    /// <remarks>
    /// <b>stdin burada okunmuyor</b> ve bu bilinçli: stdin bir <i>sunum</i>
    /// ayrıntısı. MCP tarafında stdin diye bir şey yok — orada satırlar araç
    /// argümanından geliyor. Çekirdek stdin okusaydı, MCP çağrısı çekirdeğin
    /// hiç kullanmadığı bir yola bağımlı olurdu.
    /// </remarks>
    public static CommandOutcome<ParserTryOutcome> Try(
        string file,
        IReadOnlyList<string> lines,
        ParserToolbox toolbox)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(toolbox);

        var compiled = toolbox.Compiler.CompileFile(file);

        if (!compiled.Ok)
        {
            return CommandOutcome<ParserTryOutcome>.Failed(
                CommandFailureKind.InvalidArgument,
                "Parser derlenemedi: " + string.Join("; ", compiled.Errors));
        }

        if (lines.Count == 0)
        {
            return CommandOutcome<ParserTryOutcome>.Failed(
                CommandFailureKind.InvalidArgument, "Denenecek satır verilmedi.");
        }

        var parser = compiled.Value;

        return CommandOutcome<ParserTryOutcome>.Success(new ParserTryOutcome(
            parser.Id,
            [.. lines.Select(line => new ParserTryLine(line, parser.Parse(line)))]));
    }

    /// <summary>
    /// Dizin verildiyse altındaki <c>*.yaml</c>'ları açar. Sıra <b>sabit</b>:
    /// koşumdan koşuma değişen bir sıra raporu karşılaştırılamaz yapardı.
    /// </summary>
    /// <remarks>
    /// <b><c>*.yml</c> BİLEREK kapsanmıyor</b> — taşınan CLI davranışı bu ve
    /// genişletmek sessiz bir davranış değişikliği olurdu. Bugüne kadar
    /// <c>parser lint catalog/</c> bir <c>.yml</c> dosyasını görmüyordu; çekirdek
    /// onu görmeye başlasaydı, aynı komut aynı dizinde farklı sonuç verirdi ve
    /// sebebi bu taşımada aranmazdı. Uzantı kümesini genişletmek ayrı ve
    /// bilinçli bir hareket.
    /// </remarks>
    private static IReadOnlyList<string> Expand(IReadOnlyList<string> paths)
    {
        var files = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                files.AddRange(Directory.EnumerateFiles(path, "*.yaml", SearchOption.AllDirectories));
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        return [.. files.Order(StringComparer.Ordinal)];
    }
}
