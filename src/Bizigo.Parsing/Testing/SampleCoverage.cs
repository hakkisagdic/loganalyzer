using Bizigo.Contracts;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;

namespace Bizigo.Parsing.Testing;

/// <param name="Path">Örnek dosyanın yolu.</param>
/// <param name="Ok">Tam ayrıştırılan satır sayısı.</param>
/// <param name="Partial">Kısmi ayrıştırılan satır sayısı.</param>
/// <param name="Failed">Hiçbir parser'ın tutmadığı ya da düşen satır sayısı.</param>
/// <param name="ByParser">Hangi parser'ın kaç satırı kazandığı — vendor sızıntısı burada görünür.</param>
public sealed record SampleFileCoverage(
    string Path,
    int Ok,
    int Partial,
    int Failed,
    IReadOnlyDictionary<string, int> ByParser)
{
    public int Total => Ok + Partial + Failed;

    public double OkPercent => Percent(Ok);

    public double PartialPercent => Percent(Partial);

    public double FailedPercent => Percent(Failed);

    private double Percent(int count) => Total == 0 ? 0.0 : count * 100.0 / Total;
}

public sealed record SampleCoverageReport(IReadOnlyList<SampleFileCoverage> Files)
{
    public int Total => Files.Sum(static f => f.Total);

    public int Ok => Files.Sum(static f => f.Ok);

    public int Partial => Files.Sum(static f => f.Partial);

    public int Failed => Files.Sum(static f => f.Failed);

    public double FailedPercent => Total == 0 ? 0.0 : Failed * 100.0 / Total;
}

/// <summary>
/// Altın örnek dosyalarının kapsam raporu (T08 kabul kriteri).
///
/// <para>
/// Gömülü <c>tests</c> bloğu parser'ı <b>doğrudan</b> çağırıyor; buradaki ölçüm
/// ise satırı <see cref="Dispatcher"/>'dan geçiriyor. Fark önemli: dispatcher
/// yolu <c>match.contains</c> ön filtresini ve "ilk <c>ok</c> kazanır" kuralını
/// da sınıyor. Bir vendor'ın satırı başka vendor'ın parser'ına düşüyorsa bu
/// rapor onu <c>ByParser</c> dağılımında gösterir — gömülü testler gösteremez,
/// çünkü orada kataloğun geri kalanı yoktur.
/// </para>
///
/// <para>
/// Envanter bağı (kademe 1) bilinçli olarak kullanılmıyor: örnek dosyalar
/// kaynak kimliği taşımıyor ve buradaki soru zaten "kaynağı tanımasak da
/// katalog bu satırı tanır mı?".
/// </para>
/// </summary>
public static class SampleCoverage
{
    /// <summary>Örneklerin arandığı alt dizin adı: <c>catalog/parsers/&lt;id&gt;/samples/</c>.</summary>
    public const string SamplesDirectoryName = "samples";

    public static SampleCoverageReport Run(string catalogDirectory, ParserCompiler compiler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(compiler);

        var catalog = new ParserCatalog();
        catalog.LoadFromDirectory(catalogDirectory, compiler);

        return Run(catalogDirectory, new Dispatcher(catalog, new DispatchStats()));
    }

    public static SampleCoverageReport Run(string catalogDirectory, Dispatcher dispatcher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(dispatcher);

        var files = new List<SampleFileCoverage>();

        if (!Directory.Exists(catalogDirectory))
        {
            return new SampleCoverageReport(files);
        }

        var samples = Directory
            .EnumerateDirectories(catalogDirectory, SamplesDirectoryName, SearchOption.AllDirectories)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories))
            .OrderBy(static path => path, StringComparer.Ordinal);

        foreach (var path in samples)
        {
            files.Add(Measure(path, dispatcher));
        }

        return new SampleCoverageReport(files);
    }

    private static SampleFileCoverage Measure(string path, Dispatcher dispatcher)
    {
        var ok = 0;
        var partial = 0;
        var failed = 0;
        var byParser = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            // Boş satır ve `#` yorumu ölçüme girmiyor: örnek dosyaya açıklama
            // yazabilmek, dosyayı bozmadan bağlam bırakmanın tek yolu.
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var dispatch = dispatcher.Dispatch(line, boundParserId: null);

            switch (dispatch.Result.Status)
            {
                case ParseStatus.Ok:
                    ok++;
                    break;
                case ParseStatus.Partial:
                    partial++;
                    break;
                default:
                    failed++;
                    break;
            }

            var owner = dispatch.Result.ParserId.Length == 0 ? "<eşleşmedi>" : dispatch.Result.ParserId;
            byParser[owner] = byParser.GetValueOrDefault(owner) + 1;
        }

        return new SampleFileCoverage(path, ok, partial, failed, byParser);
    }
}
