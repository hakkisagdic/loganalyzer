using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;

namespace Bizigo.Cli;

/// <summary>
/// CLI'nin paylaşılan derleyici kurulumu. Pattern ve eşleme tablosu dizinleri
/// dışarıdan verilebilir — CI, geliştirici makinesi ve F2'deki UI editörü aynı
/// motoru farklı kataloglarla koşturacak.
/// </summary>
internal sealed class ParserToolbox
{
    private ParserToolbox(ParserCompiler compiler, string patternDirectory, string mappingDirectory)
    {
        Compiler = compiler;
        PatternDirectory = patternDirectory;
        MappingDirectory = mappingDirectory;
    }

    public ParserCompiler Compiler { get; }

    public string PatternDirectory { get; }

    public string MappingDirectory { get; }

    public static ParserToolbox Create(string? patternDirectory, string? mappingDirectory)
    {
        var patterns = patternDirectory ?? DefaultPatternDirectory();
        var mappings = mappingDirectory ?? DefaultMappingDirectory();

        // Kaplama varsayılan olarak devrede: `bizigo-v1` olmadan kataloğun
        // pattern'lerinin çoğu doğrusal motorda derlenemiyor.
        var library = GrokPatternLibrary.LoadWithOverlay(patterns, DefaultOverlayDirectory());
        var compiler = new ParserCompiler(
            new GrokCompiler(library),
            MappingTableCatalog.LoadFromDirectory(mappings));

        return new ParserToolbox(compiler, patterns, mappings);
    }

    private static string DefaultPatternDirectory() =>
        Environment.GetEnvironmentVariable("BIZIGO_PATTERNS")
        ?? Locate(Path.Combine("catalog", "patterns", "legacy"));

    private static string? DefaultOverlayDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BIZIGO_PATTERN_OVERLAY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        try
        {
            return Locate(Path.Combine("catalog", "patterns", "bizigo-v1"));
        }
        catch (DirectoryNotFoundException)
        {
            // Kaplamanın yokluğu hata değil; taban setle devam edilir.
            return null;
        }
    }

    private static string DefaultMappingDirectory() =>
        Environment.GetEnvironmentVariable("BIZIGO_MAPPINGS")
        ?? Locate(Path.Combine("catalog", "mappings"));

    /// <summary>
    /// Çalışma dizininden yukarı doğru arar. CLI repo içinde herhangi bir alt
    /// dizinden çağrılabilsin diye; aksi halde "hangi dizinden koşturdun" sorusu
    /// her hata mesajının arkasına gizlenir.
    /// </summary>
    private static string Locate(string relative)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return relative;
    }
}
