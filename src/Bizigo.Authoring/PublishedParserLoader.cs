using Bizigo.ControlPlane;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Bizigo.Parsing;

namespace Bizigo.Authoring;

/// <param name="Loaded">Katalogda etkin parser sayısı.</param>
/// <param name="FromRepository">Repodaki dosyalardan gelen.</param>
/// <param name="FromDatabase">Yayınlanmış taslaklardan gelen.</param>
/// <param name="Shadowed">
/// Repodaki dosyası olup yayınlanmış taslak tarafından <b>gölgelenen</b>
/// parser kimlikleri. Sessiz kalmaması gereken tek durum bu.
/// </param>
/// <param name="Errors">Yüklenemeyenler.</param>
public sealed record CatalogSourceReport(
    int Loaded,
    int FromRepository,
    int FromDatabase,
    IReadOnlyList<string> Shadowed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Kataloğu <b>iki kaynaktan</b> besler: repodaki dosyalar ve yayınlanmış
/// taslaklar (T18).
///
/// <para>
/// <b>Çakışmada veritabanı kazanıyor.</b> Editörün amacı sevk edilmiş bir
/// parser'ı düzeltebilmek; repo kazansaydı yayın düğmesi hiçbir işe yaramazdı.
/// </para>
///
/// <para>
/// Ama bunun sinsi bir bedeli var ve <b>görünür kılınması şart</b>: bir
/// <c>git pull</c> repodaki parser'ı güncellerse, üstünde yayınlanmış bir taslak
/// varken o güncelleme <b>sessizce yok sayılır</b>. Katkı yapan kişi dosyayı
/// değiştirdiğini görür, üretimde hiçbir şey değişmez ve sebebi hiçbir yerde
/// yazmaz. <see cref="CatalogSourceReport.Shadowed"/> tam bunun için var —
/// gölgelenen her kimlik loglanıyor ve API'den görünüyor.
/// </para>
/// </summary>
public sealed class PublishedParserLoader(
    IDbContextFactory<ControlPlaneDbContext> factory,
    ParserCatalog catalog,
    ParserCompiler compiler,
    IOptions<ParsingOptions> parsing,
    ILogger<PublishedParserLoader> logger) : IParserCatalogSource
{
    public async Task<CatalogRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var report = await LoadAsync(parsing.Value.ParserDirectory, cancellationToken);

        var notes = report.Shadowed
            .Select(id => $"'{id}' repodaki dosyayı gölgeliyor: yayınlanmış taslak kullanılıyor.")
            .ToArray();

        return new CatalogRefreshOutcome(report.Loaded, report.Errors, notes);
    }

    public async Task<CatalogSourceReport> LoadAsync(
        string repositoryDirectory,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var byId = new Dictionary<string, CompiledParser>(StringComparer.Ordinal);

        foreach (var parser in LoadRepository(repositoryDirectory, errors))
        {
            byId[parser.Id] = parser;
        }

        var fromRepository = byId.Count;
        var shadowed = new List<string>();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var published = await db.Parsers
            .AsNoTracking()
            .Where(p => p.State == ParserState.Published && !p.Quarantined)
            .OrderBy(p => p.ParserId)
            .ToListAsync(cancellationToken);

        var fromDatabase = 0;

        foreach (var entity in published)
        {
            var loaded = ParserYamlLoader.Load(entity.Yaml, $"db:{entity.ParserId}@{entity.Version}");
            if (!loaded.Ok)
            {
                errors.AddRange(loaded.Errors.Select(e => e.ToString()));
                continue;
            }

            var compiled = compiler.Compile(loaded.Value);
            if (!compiled.Ok)
            {
                errors.AddRange(compiled.Errors.Select(e => e.ToString()));
                continue;
            }

            if (byId.ContainsKey(compiled.Value.Id))
            {
                shadowed.Add(compiled.Value.Id);
            }

            byId[compiled.Value.Id] = compiled.Value;
            fromDatabase++;
        }

        if (byId.Count == 0 && errors.Count > 0)
        {
            // Tamamı bozuksa mevcut katalog korunuyor — `LoadFromDirectory` ile
            // aynı ilke: yanlış bir dağıtım ayakta duran boru hattını
            // parser'sız bırakmamalı.
            logger.LogError("Katalog tamamen yüklenemedi; önceki katalog korunuyor.");
            return new CatalogSourceReport(0, 0, 0, shadowed, errors);
        }

        catalog.Replace([.. byId.Values]);

        foreach (var id in shadowed)
        {
            logger.LogWarning(
                "Parser '{ParserId}' repodaki dosyayı GÖLGELİYOR: yayınlanmış taslak kullanılıyor, "
                + "dosyadaki değişiklikler etkisiz.",
                id);
        }

        return new CatalogSourceReport(byId.Count, fromRepository, fromDatabase, shadowed, errors);
    }

    private IEnumerable<CompiledParser> LoadRepository(string directory, List<string> errors)
    {
        if (!Directory.Exists(directory))
        {
            errors.Add($"Parser dizini yok: {directory}");
            yield break;
        }

        var files = Directory
            .EnumerateFiles(directory, "*.y*ml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var loaded = ParserYamlLoader.LoadFile(file);
            if (!loaded.Ok)
            {
                errors.AddRange(loaded.Errors.Select(e => e.ToString()));
                continue;
            }

            var compiled = compiler.Compile(loaded.Value);
            if (!compiled.Ok)
            {
                errors.AddRange(compiled.Errors.Select(e => e.ToString()));
                continue;
            }

            yield return compiled.Value;
        }
    }
}
