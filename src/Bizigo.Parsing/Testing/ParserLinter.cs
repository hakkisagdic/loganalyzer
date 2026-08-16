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

        var loaded = ParserYamlLoader.LoadFile(path);
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
