namespace Bizigo.UnitTests;

/// <summary>
/// Redaksiyon ölçümlerinin <b>ortak</b> korpus okuyucusu — T41'in fixture
/// konvansiyonu ve altın korpus tek yerden okunuyor.
///
/// <para>
/// <b>Neden ayrı bir dosya.</b> T60 aynı iki korpusu okumak zorunda ve
/// <c>RedactionGateTests</c>'in özel yardımcılarını kopyalamak §9'un
/// yasakladığı şey. Kopya olsaydı ilk ayrışma sessiz olurdu: fixture başlığının
/// biçimi değiştiği gün bir okuyucu <c>BEKLENEN:</c> satırlarını bulur, diğeri
/// <b>boş liste</b> döndürür — ve boş liste üzerinde dönen bir ölçüm yeşil
/// yanar.
/// </para>
///
/// <para>
/// O yüzden okuyucular <b>boş kümede durmuyor</b>: bildirilen sır bulunamazsa
/// ya da altın korpus beklenenden küçükse iddia düşüyor. Bu, bu deponun
/// "bekçinin boş küme üzerinde dönmesi" dediği sınıfa karşı tek savunma.
/// </para>
/// </summary>
internal static class RedactionFixtures
{
    private const string BeklenenOnek = "# BEKLENEN:";

    /// <summary>Sahte olduğunu değerin kendisinden okuyan işaret (kriter 10).</summary>
    internal const string SahteIsaret = "SAHTE";

    /// <summary>Log kolunda sır taşıyan fixture'ların profilleri.</summary>
    internal static readonly string[] Profiles =
        ["asa-dc-01", "fw-ankara-01", "rb-sube-07", "lb-web-01"];

    internal static string ProfileDirectory =>
        Path.Combine(RepositoryLayout.Root, "catalog", "simulators");

    internal static string FixturePath(string profil) =>
        Path.Combine(ProfileDirectory, "profiller", profil, "sir-tasiyan.log");

    /// <summary>
    /// Fixture'ın iki yarısı: <c>#</c> ile başlayan başlık (beklenti bildirimi)
    /// ve gerçek log satırları. Kapıya yalnızca ikincisi giriyor — başlık log
    /// değil, fixture'ın kendi hakkındaki beyanı.
    ///
    /// <para>
    /// <b>Beklenen değerler teste yazılmıyor, dosyadan okunuyor</b>: yazılsaydı
    /// fixture değiştiği gün test eski değeri arar ve yeni sızıntıyı göremezdi.
    /// </para>
    /// </summary>
    internal static (string Payload, IReadOnlyList<string> Beklenen) Fixture(string profil)
    {
        var lines = File.ReadAllLines(FixturePath(profil));

        var beklenen = lines
            .Where(l => l.StartsWith(BeklenenOnek, StringComparison.Ordinal))
            .Select(l => l[BeklenenOnek.Length..].Trim())
            .ToArray();

        var payload = string.Join('\n', lines.Where(l => !l.StartsWith('#')));

        return (payload, beklenen);
    }

    /// <summary>
    /// Dört profilin bildirdiği bütün sahte sırlar — kaçırmanın ölçülebildiği
    /// <b>tek</b> küme. Altın korpus sırsız (T41 §3), yani orada kaçırma
    /// ölçülemez.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredSecrets() =>
        [.. Profiles.SelectMany(p => Fixture(p).Beklenen)];

    /// <summary>
    /// Altın korpus: <c>catalog/parsers/**/*.log</c>, boş ve <c>#</c> satırları
    /// dışlanmış. <b>Sırsız</b> — dolayısıyla buradaki her gölge adayı tanımı
    /// gereği bir yanlış pozitif.
    /// </summary>
    internal static IReadOnlyList<string> GoldenCorpus() =>
    [
        .. Directory
            .EnumerateFiles(RepositoryLayout.CatalogParserDirectory, "*.log", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#')),
    ];
}
