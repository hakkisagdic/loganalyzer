using LogAnalyzer.Parsing.Engine;
using Microsoft.Extensions.Options;

namespace LogAnalyzer.Parsing.Dispatch;

/// <summary>
/// Kataloğun varsayılan kaynağı: yalnızca repodaki dosyalar.
///
/// <para>
/// F1'in davranışı bu. Taslak deposu devreye girdiğinde
/// (<c>AddLogAnalyzerAuthoring</c>) yerini veritabanını da okuyan sürüme bırakıyor;
/// kayıt bilinçli olarak değiştirilebilir bırakıldı ki parser motoru tek başına
/// (CLI, testler) taslaklardan habersiz çalışabilsin.
/// </para>
/// </summary>
public sealed class DirectoryParserCatalogSource(
    ParserCatalog catalog,
    ParserCompiler compiler,
    IOptions<ParsingOptions> options) : IParserCatalogSource
{
    public Task<CatalogRefreshOutcome> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var report = catalog.LoadFromDirectory(options.Value.ParserDirectory, compiler);

        return Task.FromResult(new CatalogRefreshOutcome(report.Loaded, report.Errors, []));
    }
}
