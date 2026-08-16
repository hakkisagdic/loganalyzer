namespace Bizigo.UnitTests;

/// <summary>
/// Testler repo köküne göre dosya okuyor (grok kütüphanesi, örnek parser'lar).
/// Çıktı dizininden yukarı yürüyerek kökü bulur — sabit göreli yol yazmak
/// `dotnet test` ile IDE arasında sessizce ayrışır.
/// </summary>
public static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static string PatternDirectory => Path.Combine(Root, "catalog", "patterns");

    public static string LegacyPatternDirectory => Path.Combine(PatternDirectory, "legacy");

    public static string EcsPatternDirectory => Path.Combine(PatternDirectory, "ecs-v1");

    public static string CatalogParserDirectory => Path.Combine(Root, "catalog", "parsers");

    /// <summary>Maskeleme sözlüğü — sidecar ile paylaşılan tek kaynak (K14).</summary>
    public static string MaskFile => Path.Combine(Root, "catalog", "masks", "bizigo-masks.yaml");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bizigo.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Repo kökü bulunamadı ({AppContext.BaseDirectory} altından yukarı Bizigo.sln aranırken).");
    }
}
