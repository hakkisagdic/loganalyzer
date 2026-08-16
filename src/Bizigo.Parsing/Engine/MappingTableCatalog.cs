using YamlDotNet.RepresentationModel;

namespace Bizigo.Parsing.Engine;

/// <summary>
/// <c>{ from: action, table: ocsf_network_activity }</c> biçimindeki eşleme
/// tabloları. Tablolar <c>catalog/mappings/</c> altında <b>veri</b> olarak durur —
/// F1 §5'in "türetme kuralları kodda değil" kararı.
/// </summary>
public sealed class MappingTableCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> _tables;

    private MappingTableCatalog(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> tables) =>
        _tables = tables;

    public static MappingTableCatalog Empty { get; } =
        new(new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal));

    public IEnumerable<string> TableNames => _tables.Keys;

    public bool Contains(string table) => _tables.ContainsKey(table);

    /// <summary>
    /// Arama <b>ordinal</b>dır. <c>ToLower()</c> ile normalize etme cazibesine
    /// direnmek gerekiyor: <c>tr-TR</c> kültüründe <c>ACCEPT → aççept</c> değil ama
    /// <c>I → ı</c> dönüşümü tabloyu sessizce ıskalatır (F1 §2.4).
    /// </summary>
    public bool TryLookup(string table, string key, out object? value)
    {
        value = null;
        return _tables.TryGetValue(table, out var entries) && entries.TryGetValue(key, out value);
    }

    public static MappingTableCatalog LoadFromDirectory(string directory)
    {
        var tables = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
        {
            return new MappingTableCatalog(tables);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            tables[name] = LoadTable(file);
        }

        return new MappingTableCatalog(tables);
    }

    public static MappingTableCatalog FromTables(
        IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, object?>>> tables) =>
        new(tables.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, object?> LoadTable(string file)
    {
        var entries = new Dictionary<string, object?>(StringComparer.Ordinal);

        var stream = new YamlStream();
        using var reader = new StreamReader(file);
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return entries;
        }

        foreach (var (keyNode, valueNode) in root.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } || valueNode is not YamlScalarNode { Value: { } raw })
            {
                continue;
            }

            entries[key] = long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number
                : raw;
        }

        return entries;
    }
}
