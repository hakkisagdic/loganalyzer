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

    /// <summary>
    /// Tablonun üretebileceği <b>bütün</b> değerler — tekrarsız, sıralı.
    ///
    /// <para>
    /// <b>Neden gerekiyor:</b> bir eşleme tablosu, beslediği kolonun değer
    /// uzayını <b>daraltıyor</b>. <c>outcome</c> kolonu
    /// <c>http_status_outcome</c>'dan besleniyorsa orada hiçbir zaman bir HTTP
    /// kodu durmuyor, yalnızca <c>success</c>/<c>failure</c> duruyor — yani
    /// <c>status|startswith: '5'</c> arayan bir Sigma kuralı, veri ne olursa
    /// olsun <b>asla</b> eşleşemez. Bu, verinin değil şemanın söylediği bir
    /// şey ve sorulmadan bilinemez (T39).
    /// </para>
    ///
    /// <para>
    /// Bilinmeyen tablo için boş dizi dönmüyor, <b>istisna</b> atıyor: boş dizi
    /// "değer uzayı yok" ile "tablo yok"u aynı şeye indirir ve ikincisi
    /// sessizce "hiçbir değer üretilemiyor" diye okunurdu.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Outputs(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        if (!_tables.TryGetValue(table, out var entries))
        {
            throw new KeyNotFoundException($"Eşleme tablosu bulunamadı: {table}");
        }

        return
        [
            .. entries.Values
                .Select(static value => Convert.ToString(
                    value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal),
        ];
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
