using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Schema;

namespace Bizigo.Parsing.Dispatch;

/// <param name="Parsers">Derlenmiş parser'lar; dizideki konum otomat sahibi kimliğidir.</param>
/// <param name="Automaton">Literal ön filtre.</param>
/// <param name="ByParserId">Envanter bağının çözdüğü ad → parser.</param>
/// <param name="LiteralFree">Hiç literali olmayan parser'lar — ön filtre bunları eleyemez.</param>
public sealed record CatalogSnapshot(
    IReadOnlyList<CompiledParser> Parsers,
    AhoCorasick Automaton,
    IReadOnlyDictionary<string, CompiledParser> ByParserId,
    IReadOnlyList<int> LiteralFree)
{
    public static CatalogSnapshot Empty { get; } = new(
        [],
        AhoCorasick.Build([]),
        new Dictionary<string, CompiledParser>(StringComparer.Ordinal),
        []);
}

public sealed record CatalogLoadReport(
    int Loaded,
    int Failed,
    IReadOnlyList<string> Errors);

/// <summary>
/// Derlenmiş parser kümesinin sahibi.
///
/// <para>
/// <b>Sıcak yeniden yükleme atomik:</b> yeni katalog tamamen kurulup derlenene
/// kadar eskisi yerinde kalıyor, sonra tek referans değişimiyle yer değiştiriyor.
/// Koşan boru hattı ya tamamen eski ya tamamen yeni kataloğu görür — yarı yüklü
/// bir ara durum yok. Yükleme sırasında bir dosya bozuksa <b>hiçbir şey
/// değişmez</b>: bozuk bir katkı, çalışan sistemi bozamaz.
/// </para>
/// </summary>
public sealed class ParserCatalog
{
    private volatile CatalogSnapshot _snapshot = CatalogSnapshot.Empty;

    public CatalogSnapshot Current => _snapshot;

    public int Count => _snapshot.Parsers.Count;

    /// <summary>
    /// Dizindeki tüm YAML parser'larını yükler ve kataloğu değiştirir.
    /// Hiç parser derlenemezse katalog <b>değiştirilmez</b>.
    /// </summary>
    public CatalogLoadReport LoadFromDirectory(string directory, ParserCompiler compiler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(compiler);

        if (!Directory.Exists(directory))
        {
            return new CatalogLoadReport(0, 0, [$"Parser dizini yok: {directory}"]);
        }

        var files = Directory
            .EnumerateFiles(directory, "*.y*ml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        var compiled = new List<CompiledParser>();
        var errors = new List<string>();

        foreach (var file in files)
        {
            var loaded = ParserYamlLoader.LoadFile(file);
            if (!loaded.Ok)
            {
                errors.AddRange(loaded.Errors.Select(e => e.ToString()));
                continue;
            }

            var result = compiler.Compile(loaded.Value);
            if (!result.Ok)
            {
                errors.AddRange(result.Errors.Select(e => e.ToString()));
                continue;
            }

            compiled.Add(result.Value);
        }

        if (compiled.Count == 0 && errors.Count > 0)
        {
            // Tamamı bozuksa mevcut katalog korunuyor: yanlış bir dağıtım,
            // ayakta duran boru hattını parser'sız bırakmamalı.
            return new CatalogLoadReport(0, errors.Count, errors);
        }

        Replace(Resolve(compiled, errors));
        return new CatalogLoadReport(_snapshot.Parsers.Count, errors.Count, errors);
    }

    public void Replace(IReadOnlyList<CompiledParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _snapshot = BuildSnapshot(parsers);
    }

    /// <summary>
    /// Aynı <c>id</c>'nin birden çok sürümü varsa en yükseği kazanır; aynı
    /// <c>id@version</c> iki kez tanımlıysa bu bir hatadır ve ikincisi yok sayılır.
    /// </summary>
    private static List<CompiledParser> Resolve(List<CompiledParser> compiled, List<string> errors)
    {
        var byId = new Dictionary<string, CompiledParser>(StringComparer.Ordinal);

        foreach (var parser in compiled)
        {
            if (!byId.TryGetValue(parser.Id, out var existing))
            {
                byId[parser.Id] = parser;
                continue;
            }

            var comparison = CompareVersions(parser.Version, existing.Version);

            if (comparison == 0)
            {
                errors.Add(
                    $"'{parser.Id}@{parser.Version}' birden çok dosyada tanımlı; " +
                    $"'{parser.Definition.SourcePath}' yok sayıldı.");
                continue;
            }

            if (comparison > 0)
            {
                byId[parser.Id] = parser;
            }
        }

        return [.. byId.Values];
    }

    private static int CompareVersions(string left, string right)
    {
        // Basit tutuluyor: sayısal bileşenler soldan karşılaştırılır. Semver'in
        // tamamı (ön sürüm etiketleri) katalog için gereğinden karmaşık.
        var a = left.Split('.', '-');
        var b = right.Split('.', '-');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var parsedA) ? parsedA : 0;
            var y = i < b.Length && int.TryParse(b[i], out var parsedB) ? parsedB : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return string.CompareOrdinal(left, right);
    }

    private static CatalogSnapshot BuildSnapshot(IReadOnlyList<CompiledParser> parsers)
    {
        // Sıralama burada bir kez yapılıyor: dispatcher her satırda sıralama yapmaz.
        var ordered = parsers
            .OrderByDescending(p => p.Definition.Metadata.Specificity)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();

        var literals = new List<(string, int)>();
        var literalFree = new List<int>();
        var byId = new Dictionary<string, CompiledParser>(StringComparer.Ordinal);

        for (var i = 0; i < ordered.Length; i++)
        {
            var parser = ordered[i];
            byId[parser.Id] = parser;

            var contains = parser.Definition.Match.Contains;
            if (contains.Count == 0)
            {
                // Literali olmayan parser ön filtreyle elenemez; her satırda aday
                // olmak zorunda. Aksi halde sessizce hiç denenmezdi.
                literalFree.Add(i);
                continue;
            }

            foreach (var literal in contains)
            {
                literals.Add((literal, i));
            }
        }

        return new CatalogSnapshot(ordered, AhoCorasick.Build(literals), byId, literalFree);
    }
}
