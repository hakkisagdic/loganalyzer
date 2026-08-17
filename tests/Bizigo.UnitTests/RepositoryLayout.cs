using Bizigo.Parsing.Grok;

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

    /// <summary>Lookaround'suz kaplama — tam set değil, <c>legacy</c> üstüne biner.</summary>
    public static string BizigoV1PatternDirectory => Path.Combine(PatternDirectory, "bizigo-v1");

    public static string CatalogParserDirectory => Path.Combine(Root, "catalog", "parsers");

    /// <summary>Maskeleme sözlüğü — sidecar ile paylaşılan tek kaynak (K14).</summary>
    public static string MaskFile => Path.Combine(Root, "catalog", "masks", "bizigo-masks.yaml");

    /// <summary>
    /// <b>Üretimin</b> pattern kütüphanesi: <c>legacy</c> üstüne <c>bizigo-v1</c>
    /// kaplaması — <c>ParserToolbox.Create</c> ile aynı kurulum.
    ///
    /// <para>
    /// Kataloğu sınayan testler bunu kullanmak zorunda. <c>legacy</c>'yi tek
    /// başına yüklemek, sevk edilmeyen bir yapılandırmayı sınamak demek: kaplama
    /// olmadan katalog pattern'lerinin çoğu geri izlemeli motorda derleniyor ve
    /// oradaki <c>MatchTimeout</c> duvar saatini ölçtüğü için yüklü makinede
    /// sağlıklı bir pattern zaman aşımına uğrayıp örneği <c>failed</c> yapıyor.
    /// Kararsızlığın kaynağı buydu.
    /// </para>
    ///
    /// <para>
    /// Kaplamayı <b>kâhinle karşılaştıran</b> testler (<c>BizigoV1PatternTests</c>,
    /// <c>CiscoAsaAddressPatternTests</c>, <c>NginxNumberPatternTests</c>) bilerek
    /// <c>legacy</c>'yi ayrıca yüklüyor — upstream davranışı onların ölçüm tabanı.
    /// </para>
    /// </summary>
    public static GrokPatternLibrary DefaultLibrary { get; } =
        GrokPatternLibrary.LoadWithOverlay(LegacyPatternDirectory, BizigoV1PatternDirectory);

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
