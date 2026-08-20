using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Bizigo.Parsing.Schema;
using Bizigo.Parsing.Testing;

namespace Bizigo.Authoring;

/// <summary>
/// Kapının hangi aşamada durduğu.
///
/// <para>
/// Sıra <b>anlamlı</b>: şemadan geçmeyen YAML derlenemiyor, derlenemeyen
/// pattern taranamıyor, taranmamış parser test koşturamıyor. Editör "neden
/// yayınlanamıyor" sorusuna tek bir cümleyle cevap verebilsin diye aşama
/// açıkça taşınıyor — hata listesinin içeriğinden geri çıkarmak, listeyi
/// biçimlendiren her değişiklikte sessizce yanlışlaşırdı.
/// </para>
/// </summary>
public enum PublishGateStage
{
    /// <summary>Hiçbir kapı düşmedi.</summary>
    Passed = 0,

    /// <summary>YAML şeması ya da derleme — satır/sütun taşıyan hatalar.</summary>
    Schema,

    /// <summary>ReDoS taraması; <c>GROK003</c> burada yayını durduruyor.</summary>
    Redos,

    /// <summary>Gömülü <c>tests</c> bloğu: yok ya da geçmiyor.</summary>
    Tests,
}

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
    IReadOnlyList<string> Warnings)
{
    /// <summary>Kapının durduğu aşama; geçtiyse <see cref="PublishGateStage.Passed"/>.</summary>
    public PublishGateStage Stage { get; init; } = PublishGateStage.Passed;

    /// <summary>
    /// Şema/derleme hataları <b>yapılandırılmış</b> hâlde.
    ///
    /// <para>
    /// <see cref="Errors"/> aynı bilgiyi düz metin olarak taşıyor ve CLI için
    /// yeterli; editör için değil. "Bir yerde bir şey yanlış" ile "37. satır"
    /// arasındaki fark <see cref="ParserSchemaError.Line"/> alanının
    /// kaybedilmemesi — biçimlendirilmiş metinden geri ayrıştırmak, mesaj
    /// metnini değiştiren ilk katkıda sessizce kırılırdı (T19).
    /// </para>
    /// </summary>
    public IReadOnlyList<ParserSchemaError> SchemaErrors { get; init; } = [];

    /// <summary>ReDoS bulguları; şiddet ayrımı korunuyor.</summary>
    public IReadOnlyList<RedosFinding> RedosFindings { get; init; } = [];

    /// <summary>
    /// Gömülü testlerin sonucu — geçenler de dahil.
    ///
    /// <para>Yalnızca düşenleri taşımak, "kaç test var" sorusunu cevapsız
    /// bırakırdı; editörde bir taslağın testsiz olması ile testlerinin
    /// geçmesi arasındaki fark tam olarak bu.</para>
    /// </summary>
    public IReadOnlyList<ParserTestResult> TestResults { get; init; } = [];

    /// <summary>
    /// Kapının onayladığı derlenmiş parser; şema aşamasında düştüyse
    /// <see langword="null"/>.
    ///
    /// <para>
    /// Editörün örnek satırı <b>bununla</b> deneniyor. İkinci kez derlemek
    /// mümkündü ama "kapının gördüğü parser" ile "önizlemenin koşturduğu
    /// parser"ı iki ayrı derleme sonucuna bırakırdı; ikisi bir gün ayrışırsa
    /// önizleme yalan söyler ve bunu kimse fark etmez.
    /// </para>
    /// </summary>
    public CompiledParser? Compiled { get; init; }
}

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

        var blockingRedos = false;

        foreach (var finding in lint.RedosFindings)
        {
            var text = $"[{finding.Code}] {finding.Message}"
                + (finding.Fragment.Length > 0 ? $" → {finding.Fragment}" : string.Empty);

            // GROK003 uyarı seviyesinde ama yayında HATA sayılıyor: kataloğun
            // tamamı doğrusal motorda derleniyor ve bu değişmez korunmalı.
            if (finding.Severity == RedosSeverity.Error || finding.Code == "GROK003")
            {
                errors.Add(text);
                blockingRedos = true;
            }
            else
            {
                warnings.Add(text);
            }
        }

        if (lint.SchemaErrors.Count > 0)
        {
            // Şema hatası en derin sebep: YAML yüklenemediyse ya da derlenemediyse
            // pattern taraması ve test koşumu hiç çalışmadı. Bunu "test düştü"
            // gibi göstermek kullanıcıyı yanlış yere bakmaya yollardı.
            return new PublishVerdict(false, lint.ParserId ?? string.Empty, string.Empty, 0, errors, warnings)
            {
                Stage = PublishGateStage.Schema,
                SchemaErrors = lint.SchemaErrors,
            };
        }

        // Derleme başarılı — linter zaten derledi, ama testleri koşturmak ve
        // önizlemeyi beslemek için derlenmiş nesneye ihtiyacımız var.
        var loaded = ParserYamlLoader.Load(yaml, label);
        var compiled = _compiler.Compile(loaded.Value);

        // Testler ReDoS bulgusu olsa da koşuyor. Yayın yine durur, ama
        // "yayınlanamaz" ile "denenemez" aynı şey değil: geri izlemeye düşen bir
        // pattern'i düzelten kişi, düzeltmeden ÖNCE parser'ın doğru ayrıştırdığını
        // görebilmeli — yoksa iki sorunu aynı anda kör olarak çözmek zorunda kalır.
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
            warnings)
        {
            // ReDoS bulgusu testlerden ÖNCE gelen kapı: test düşmesi de olsa
            // kullanıcıya önce doğrusal motora dönmesi söylenmeli, çünkü geri
            // izleyen bir pattern testleri de kararsız hâle getiriyor
            // (`matchTimeout` duvar saatini ölçüyor — T08 raporu #10).
            Stage = errors.Count == 0 ? PublishGateStage.Passed
                : blockingRedos ? PublishGateStage.Redos
                : PublishGateStage.Tests,
            RedosFindings = lint.RedosFindings,
            TestResults = report.Tests,
            Compiled = compiled.Ok ? compiled.Value : null,
        };
    }
}
