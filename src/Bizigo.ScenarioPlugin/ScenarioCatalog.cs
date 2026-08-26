namespace Bizigo.ScenarioPlugin;

/// <param name="Scenarios">Zarfı doğrulanmış senaryolar, id'ye göre sıralı.</param>
/// <param name="Errors">Yüklenemeyenlerin kusurları — dosya yolu ve satırla.</param>
public sealed record ScenarioCatalogReport(
    IReadOnlyList<ScenarioDefinition> Scenarios,
    IReadOnlyList<ScenarioSchemaError> Errors)
{
    public bool Ok => Errors.Count == 0;

    public string Describe() =>
        Errors.Count == 0 ? "hata yok" : string.Join(Environment.NewLine, Errors.Select(e => e.ToString()));
}

/// <summary>
/// Senaryo dosyalarının bulunduğu dizini yükler.
///
/// <para>
/// <b>Bir dosyanın kusuru diğerlerini düşürmüyor</b> ama kusurlu dosya
/// yüklenmiyor: kısmi katalog + görünür hata listesi. Kusuru yutup dizini
/// "yüklendi" saymak, eksik bir senaryo kümesini tam gibi gösterirdi.
/// </para>
/// </summary>
public static class ScenarioCatalog
{
    public static ScenarioCatalogReport Load(
        string directory,
        IScenarioProviderRegistry registry,
        IReadOnlySet<string>? triggers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(registry);

        var errors = new List<ScenarioSchemaError>();

        if (!Directory.Exists(directory))
        {
            errors.Add(new ScenarioSchemaError(directory, 1, 1, "Senaryo dizini yok."));
            return new ScenarioCatalogReport([], errors);
        }

        var files = Directory
            .EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var loaded = new List<ScenarioDefinition>(files.Count);
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var result = ScenarioYamlLoader.LoadFile(file, registry, triggers);

            if (!result.Ok)
            {
                errors.AddRange(result.Errors);
                continue;
            }

            var definition = result.Value;

            // Aynı kimliğin iki dosyada olması: biri sessizce kazanır ve hangisinin
            // koştuğu dosya adına bağlı kalır.
            if (byId.TryGetValue(definition.Metadata.Id, out var first))
            {
                errors.Add(new ScenarioSchemaError(file, 1, 1,
                    $"Senaryo id '{definition.Metadata.Id}' zaten '{first}' dosyasında tanımlı."));
                continue;
            }

            byId[definition.Metadata.Id] = file;
            loaded.Add(definition);
        }

        return new ScenarioCatalogReport(
            [.. loaded.OrderBy(s => s.Metadata.Id, StringComparer.Ordinal)],
            errors);
    }
}
