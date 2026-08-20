using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Testing;

namespace Bizigo.Authoring;

/// <param name="Ok">Tam ayrıştırılan satır sayısı.</param>
/// <param name="MeasuredAt">Ölçümün alındığı an; ekran "ne kadar bayat" diyebilsin.</param>
public sealed record CatalogCoverage(
    int Ok,
    int Partial,
    int Failed,
    IReadOnlyList<ParserCoverage> ByParser,
    DateTimeOffset MeasuredAt)
{
    public int Total => Ok + Partial + Failed;

    public static CatalogCoverage Empty { get; } = new(0, 0, 0, [], DateTimeOffset.UnixEpoch);
}

/// <param name="Wins">Bu parser'ın kazandığı satır sayısı.</param>
public sealed record ParserCoverage(string ParserId, int Wins);

/// <summary>
/// Altın örnek kapsamının <b>anlık görüntüye bağlı</b> önbelleği (T20).
///
/// <para>
/// <b>Neden önbellek şart:</b> ölçüm <see cref="SampleCoverage.Run(string, Dispatcher)"/>
/// ile altın örneklerin <b>tamamını</b> dispatcher'dan geçiriyor. Katalog
/// ekranı her açıldığında bunu koşturmak, K16'nın uyardığı desenin katalog
/// tarafındaki hâli olurdu: bir ekranın açılışı motoru doyurur.
/// </para>
///
/// <para>
/// <b>Anahtar bir sürüm sayacı değil, anlık görüntünün kendisi.</b>
/// <see cref="ParserCatalog"/> yeniden yüklemede referansı tek atomik adımda
/// değiştiriyor; dolayısıyla "katalog değişti mi" sorusunun tam cevabı
/// <c>ReferenceEquals</c>. Ayrı bir sayaç tutmak, sayacı artırmayı unutmanın
/// mümkün olduğu ikinci bir gerçek kaynak yaratırdı — ve o unutma, ekranda
/// bayat bir kapsam oranı olarak görünürdü.
/// </para>
/// </summary>
public sealed class CatalogCoverageCache(ParserCatalog catalog, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private CatalogSnapshot? _measuredFor;
    private CatalogCoverage _coverage = CatalogCoverage.Empty;

    /// <summary>Ölçüm hiç alınmadıysa ya da katalog değiştiyse <see langword="true"/>.</summary>
    public bool IsStale
    {
        get
        {
            lock (_gate)
            {
                return !ReferenceEquals(_measuredFor, catalog.Current);
            }
        }
    }

    /// <summary>
    /// Kapsamı döndürür; katalog değiştiyse yeniden ölçer.
    ///
    /// <para>
    /// <paramref name="force"/> ekrandaki "yeniden ölç" düğmesi için: altın
    /// örnek <b>dosyaları</b> katalog değişmeden de düzenlenebiliyor ve o
    /// durumda anlık görüntü aynı kalıyor. Elle tetikleme olmasaydı kullanıcı
    /// dosyayı düzeltip sonucu göremezdi.
    /// </para>
    /// </summary>
    public CatalogCoverage Measure(string catalogDirectory, Dispatcher dispatcher, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        lock (_gate)
        {
            var snapshot = catalog.Current;

            if (!force && ReferenceEquals(_measuredFor, snapshot))
            {
                return _coverage;
            }

            var report = SampleCoverage.Run(catalogDirectory, dispatcher);

            _coverage = new CatalogCoverage(
                report.Ok,
                report.Partial,
                report.Failed,
                [.. report.Files
                    .SelectMany(static f => f.ByParser)
                    .GroupBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static g => new ParserCoverage(g.Key, g.Sum(static pair => pair.Value)))
                    .OrderByDescending(static p => p.Wins)],
                _time.GetUtcNow());

            _measuredFor = snapshot;
            return _coverage;
        }
    }
}
