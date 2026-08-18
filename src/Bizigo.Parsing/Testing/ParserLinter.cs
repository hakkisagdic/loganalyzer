using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Testing;

public sealed record ParserLintReport(
    string Path,
    string? ParserId,
    IReadOnlyList<ParserSchemaError> SchemaErrors,
    IReadOnlyList<RedosFinding> RedosFindings)
{
    public bool HasErrors =>
        SchemaErrors.Count > 0 || RedosFindings.Any(static f => f.Severity == RedosSeverity.Error);

    public bool HasWarnings => RedosFindings.Any(static f => f.Severity == RedosSeverity.Warning);
}

/// <summary>
/// <c>bizigo parser lint</c> arkasındaki iş: şema doğrulaması + ReDoS taraması.
/// Test koşumu ayrı komut (<c>parser test</c>) — lint'in ağ/dosya erişimi olmadan
/// ve saniyenin altında bitmesi isteniyor, çünkü editör bunu her kayıtta çağıracak (F2).
/// </summary>
public static class ParserLinter
{
    public static ParserLintReport LintFile(string path, ParserCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);

        return Inspect(ParserYamlLoader.LoadFile(path), path, compiler);
    }

    /// <summary>
    /// Dosyaya yazılmamış YAML'ı denetler — parser editöründen gelen taslaklar
    /// için (T18). Dosya yolundan geçen sürümle <b>aynı</b> kuralları koşuyor:
    /// iki ayrı denetleyici, taslakta geçip yayında kalan bir parser demek olurdu.
    /// </summary>
    public static ParserLintReport Lint(string yaml, string label, ParserCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);

        return Inspect(ParserYamlLoader.Load(yaml, label), label, compiler);
    }

    private static ParserLintReport Inspect(ParserLoadResult loaded, string path, ParserCompiler compiler)
    {
        if (!loaded.Ok)
        {
            return new ParserLintReport(path, null, loaded.Errors, []);
        }

        var definition = loaded.Value;
        var compiled = compiler.Compile(definition);

        if (!compiled.Ok)
        {
            return new ParserLintReport(path, definition.Metadata.Id, compiled.Errors, []);
        }

        var findings = new List<RedosFinding>();
        foreach (var grok in compiled.Value.Groks)
        {
            findings.AddRange(RedosLinter.Inspect(grok));
        }

        return new ParserLintReport(path, definition.Metadata.Id, [], findings);
    }
}
