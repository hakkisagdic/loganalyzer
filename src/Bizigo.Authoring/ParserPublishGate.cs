using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;
using Bizigo.Parsing.Testing;

namespace Bizigo.Authoring;

/// <param name="Ok">Yayınlanabilir mi.</param>
/// <param name="ParserId">YAML'dan çözülen kimlik; çözülemediyse boş.</param>
/// <param name="Version">YAML'dan çözülen sürüm.</param>
/// <param name="PassingTests">Geçen gömülü test sayısı.</param>
/// <param name="Errors">Yayını engelleyen sebepler — kullanıcıya olduğu gibi gösteriliyor.</param>
/// <param name="Warnings">Engellemiyor ama görünmesi gereken bulgular.</param>
public sealed record PublishVerdict(
    bool Ok,
    string ParserId,
    string Version,
    int PassingTests,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Yayın öncesi zorunlu kapılar (T18).
///
/// <para>
/// <b>Neden kapı, öneri değil:</b> katalog F1'de sıfır <c>GROK003</c>'e indirildi
/// ve bu dört ayrı daraltma gerektirdi. Kapı olmadan ilk katkı kataloğu geri
/// izlemeli motora düşürür ve kimse fark etmez — geri izlemeye düşen pattern
/// <c>matchTimeout</c> ödüyor, o da <b>duvar saatini</b> ölçüyor, yani yüklü
/// makinede sağlıklı bir satır <c>failed</c> oluyor.
/// </para>
///
/// <para>
/// Aynı gerekçeyle testler de zorunlu: gömülü <c>tests</c> bloğu şema düzeyinde
/// zaten boş bırakılamıyor, ama <b>geçtiklerini</b> burada doğruluyoruz.
/// </para>
/// </summary>
public sealed class ParserPublishGate(ParserCompiler compiler)
{
    private readonly ParserCompiler _compiler = compiler
        ?? throw new ArgumentNullException(nameof(compiler));

    public PublishVerdict Inspect(string yaml, string label = "<taslak>")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var errors = new List<string>();
        var warnings = new List<string>();

        var lint = ParserLinter.Lint(yaml, label, _compiler);

        foreach (var schemaError in lint.SchemaErrors)
        {
            errors.Add(schemaError.ToString());
        }

        foreach (var finding in lint.RedosFindings)
        {
            var text = $"[{finding.Code}] {finding.Message}"
                + (finding.Fragment.Length > 0 ? $" → {finding.Fragment}" : string.Empty);

            // GROK003 uyarı seviyesinde ama yayında HATA sayılıyor: kataloğun
            // tamamı doğrusal motorda derleniyor ve bu değişmez korunmalı.
            if (finding.Severity == RedosSeverity.Error || finding.Code == "GROK003")
            {
                errors.Add(text);
            }
            else
            {
                warnings.Add(text);
            }
        }

        if (errors.Count > 0)
        {
            return new PublishVerdict(false, lint.ParserId ?? string.Empty, string.Empty, 0, errors, warnings);
        }

        // Buraya gelindiyse derleme başarılı — linter zaten derledi, ama
        // testleri koşturmak için derlenmiş nesneye ihtiyacımız var.
        var loaded = ParserYamlLoader.Load(yaml, label);
        var compiled = _compiler.Compile(loaded.Value);

        var report = ParserTestRunner.Run(compiled.Value);
        var passing = report.Tests.Count(static t => t.Passed);

        if (report.Tests.Count == 0)
        {
            errors.Add("Gömülü test yok. Testsiz parser yayınlanamaz.");
        }
        else if (!report.Passed)
        {
            foreach (var failed in report.Tests.Where(static t => !t.Passed))
            {
                errors.Add($"Test düştü: {failed.Name} (satır {failed.Line}) — "
                    + string.Join("; ", failed.Failures.Select(f => f.Describe())));
            }
        }

        var metadata = compiled.Value.Definition.Metadata;

        return new PublishVerdict(
            errors.Count == 0,
            metadata.Id,
            metadata.Version,
            passing,
            errors,
            warnings);
    }
}
