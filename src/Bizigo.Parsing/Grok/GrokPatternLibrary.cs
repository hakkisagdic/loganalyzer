using System.Collections.Frozen;

namespace Bizigo.Parsing.Grok;

/// <summary>
/// <c>NAME pattern</c> satırlarından oluşan grok pattern kütüphanesi.
///
/// Kütüphane <b>veridir</b> (F1 §4.1): Logstash/Elastic setleri
/// <c>catalog/patterns/</c> altında sürümlenir, koda gömülmez. Bu sınıf yalnızca
/// o dosyaları okur.
/// </summary>
public sealed class GrokPatternLibrary
{
    private readonly FrozenDictionary<string, string> _patterns;

    private GrokPatternLibrary(IDictionary<string, string> patterns)
    {
        _patterns = patterns.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static GrokPatternLibrary Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    public int Count => _patterns.Count;

    public IEnumerable<string> Names => _patterns.Keys;

    public bool TryGet(string name, out string pattern) => _patterns.TryGetValue(name, out pattern!);

    /// <summary>
    /// Dizindeki (ve alt dizinlerdeki) tüm pattern dosyalarını yükler.
    /// Aynı ad iki kez tanımlanırsa <b>hata verir</b> — sessizce üzerine yazmak,
    /// bir pattern'in nereden geldiğini takip edilemez hale getirir.
    /// </summary>
    public static GrokPatternLibrary LoadFromDirectory(string directory, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Grok pattern dizini bulunamadı: {directory}");
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(directory, "*", option)
            .Where(static path => !IsIgnored(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        var patterns = new Dictionary<string, string>(StringComparer.Ordinal);
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            foreach (var (name, pattern, lineNumber) in ReadFile(file))
            {
                if (origins.TryGetValue(name, out var firstOrigin))
                {
                    // Birebir aynı tanımın tekrarı belirsizlik yaratmaz — upstream
                    // set bunu yapıyor (MCOLLECTIVEAUDIT hem `mcollective` hem
                    // `mcollective-patterns` dosyasında, aynı gövdeyle). Yalnızca
                    // *çelişen* tanım hatadır.
                    if (string.Equals(patterns[name], pattern, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    throw new GrokCompilationException(
                        $"Grok pattern '{name}' iki farklı gövdeyle tanımlı: {firstOrigin} ve {file}:{lineNumber}. " +
                        "Hangi pattern'in koşacağı belirsiz kalır.");
                }

                patterns[name] = pattern;
                origins[name] = $"{file}:{lineNumber}";
            }
        }

        return new GrokPatternLibrary(patterns);
    }

    /// <summary>Tek dosya veya sözlükten yükleme — testler ve parser içi <c>pattern_definitions</c> için.</summary>
    public static GrokPatternLibrary FromDictionary(IEnumerable<KeyValuePair<string, string>> patterns)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, pattern) in patterns)
        {
            map[name] = pattern;
        }

        return new GrokPatternLibrary(map);
    }

    /// <summary>
    /// Taban seti yükler, üstüne kaplamayı bindirir (<c>bizigo-v1</c>).
    ///
    /// <para>
    /// Kaplama tam bir set değil: yalnızca lookaround taşıyan pattern'lerin
    /// lookaround'suz karşılıklarını içeriyor. Taban seti kopyalamak yerine
    /// bindirmek bilinçli — <c>legacy</c> ve <c>ecs-v1</c> upstream'in birebir
    /// kopyası kalıyor ve <c>cp -R</c> ile yükseltilebiliyor.
    /// </para>
    ///
    /// <para>
    /// Kaplama dizini yoksa taban set aynen dönüyor: kaplamanın <b>yokluğu</b>
    /// bir hata değil, yalnızca doğrusal motorun daha az devreye girmesi demek.
    /// </para>
    /// </summary>
    public static GrokPatternLibrary LoadWithOverlay(string baseDirectory, string? overlayDirectory)
    {
        var library = LoadFromDirectory(baseDirectory);

        if (string.IsNullOrWhiteSpace(overlayDirectory) || !Directory.Exists(overlayDirectory))
        {
            return library;
        }

        var overlay = LoadFromDirectory(overlayDirectory);
        return library.With(overlay._patterns);
    }

    /// <summary>Bu kütüphanenin üstüne ek tanımlar bindirir (parser'ın kendi <c>pattern_definitions</c>'ı).</summary>
    public GrokPatternLibrary With(IEnumerable<KeyValuePair<string, string>>? overrides)
    {
        if (overrides is null)
        {
            return this;
        }

        var map = new Dictionary<string, string>(_patterns, StringComparer.Ordinal);
        var changed = false;
        foreach (var (name, pattern) in overrides)
        {
            map[name] = pattern;
            changed = true;
        }

        return changed ? new GrokPatternLibrary(map) : this;
    }

    private static bool IsIgnored(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.'))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return extension is ".md" or ".txt" or ".yaml" or ".yml" or ".json"
            || string.Equals(name, "LICENSE", StringComparison.Ordinal);
    }

    private static IEnumerable<(string Name, string Pattern, int LineNumber)> ReadFile(string path)
    {
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new GrokCompilationException(
                    $"{path}:{lineNumber}: geçersiz pattern satırı. Beklenen biçim: 'AD pattern'. Görülen: {rawLine}");
            }

            var name = line[..separator];
            var pattern = line[(separator + 1)..].TrimStart();
            if (pattern.Length == 0)
            {
                throw new GrokCompilationException($"{path}:{lineNumber}: '{name}' için pattern boş.");
            }

            yield return (name, pattern, lineNumber);
        }
    }
}
