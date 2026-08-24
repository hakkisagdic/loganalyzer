namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>docs/wiki/</c> sayfaları kaynaklarına göre bayat mı.</b>
///
/// <para>
/// Vault sayfaları <c>docs/epic/</c>, <c>CLAUDE.md</c>, <c>README.md</c>
/// belgelerinden damıtıldı. O belgeler değiştiğinde damıtılmış cümle sessizce
/// yanlış olur — hata yok, sayaç yok, belirti yok. §7'nin adını koyduğu sınıf
/// bu, ve vault'un kullanılabilir kalmasının tek şartı da bu sorunun sorulması.
/// </para>
///
/// <para>
/// <b>Neden bekçi, neden "sayfayı güncellemeyi unutma" kuralı değil:</b> aynı
/// gerekçe <c>CiCoverageTests</c>'te ölçüldü — hatırlamaya dayanan mekanizma bu
/// depoda kaybetti. Bir kaynağın değiştiğini fark etmek, kaynağı değiştirenin
/// vault'un varlığından haberdar olmasını gerektirir; olmayacak.
/// </para>
///
/// <para>
/// <b>Denetlenen küme taranıyor</b> (<see cref="WikiSourceDigest.Pages"/>), elle
/// tutulmuyor. Elle kalan tek şey <see cref="Exempt"/>: kaynağı olmayan
/// sayfalar, ve büyümesi <see cref="ExpectedExemptCount"/> yüzünden görünür.
/// </para>
///
/// <para>
/// <b>Kırmızı yandığında ne yapılır:</b> önce sayfa gözden geçirilir — kaynak
/// gerçekten sayfayı yanlışladı mı — sonra yeniden damgalanır:
/// <c>BIZIGO_WIKI_STAMP=1 dotnet test tests/Bizigo.UnitTests --filter
/// FullyQualifiedName~WikiSourceDigestStamper</c>. Sıra bu: önce oku, sonra
/// damgala. Ters sıra damgayı bir kayıt olmaktan çıkarıp bir gürültü bastırıcıya
/// çevirir.
/// </para>
/// </summary>
public sealed class WikiSourceDigestTests
{
    /// <summary>
    /// <b>Kaynağı olmayan sayfalar.</b> Bunlar damıtılmış bilgi taşımıyor:
    /// gezinme, yapılandırma ve otomatik üretilen görünümler. Bir kaynağı
    /// olmadığı için bayatlamaları da kaynak değişiminden gelmiyor.
    ///
    /// <para>
    /// Muafiyet <b>bedava değil</b> (§8 deseni): buraya satır eklemek
    /// <see cref="ExpectedExemptCount"/>'u da değiştirmeyi gerektiriyor, yani
    /// kaçış kapısı sessizce genişleyemiyor. Ayrıca muafiyetin <b>bayatlaması</b>
    /// da yakalanıyor: listedeki bir sayfa silinirse ya da sonradan
    /// <c>sources</c> kazanırsa <see cref="Muafiyet_listesi_sessizce_buyuyemez"/>
    /// kırmızı yanıyor — aksi hâlde muafiyet, denetlenmesi gereken bir sayfayı
    /// sessizce dışarıda tutan bir delik olurdu.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["index.md"] = "Dizin; sayfa listesinden otomatik üretiliyor, damıtılmış cümle taşımıyor.",
        ["hot.md"] = "Sıcak sayfalar görünümü; vault'un kendi durumundan türüyor.",
        ["log.md"] = "İşlem günlüğü; kaynaktan damıtılmıyor, olan biteni yazıyor.",
        ["README.md"] = "Vault'u açma/profil talimatı; depo belgelerinden damıtılmadı.",
        ["_meta/taxonomy.md"] = "Etiket sözlüğü; vault'un kendi konvansiyonu, dış kaynağı yok.",
    };

    /// <summary>
    /// <see cref="Exempt"/> bu sayıda kalmalı. Sabitin tek işlevi listeyi
    /// büyütmeyi <b>görünür</b> kılmak.
    /// </summary>
    private const int ExpectedExemptCount = 5;

    /// <summary>
    /// Damgalayıcı ile bekçi <b>aynı koşumda buluşamaz</b>.
    ///
    /// <para>
    /// <c>BIZIGO_WIKI_STAMP=1 dotnet test</c> damgaları tazeler ve ardından
    /// aynı süreçte doğrular; her sayfa geçer, hiçbir şey kanıtlanmaz. Kendi
    /// kendini tatmin eden bir kapı, olmayan bir kapıdan tehlikeli (§7) —
    /// çünkü üstüne "bu soru sorulmuş" yanılsaması bırakıyor.
    /// </para>
    /// </summary>
    private static void RefuseSelfSatisfyingRun()
    {
        Assert.False(
            WikiSourceDigest.StampRequested,
            $"{WikiSourceDigest.StampEnvironmentVariable}=1 ile koşuluyor: bu koşumda damgalar " +
            "yeniden yazılıyor, dolayısıyla bekçinin doğruladığı şey kendi çıktısı olurdu. " +
            "Damgaladıktan sonra değişkeni kaldırıp yeniden koşturun.");
    }

    private static IReadOnlyList<WikiSourceDigest.Page> Vault()
    {
        RefuseSelfSatisfyingRun();

        var pages = WikiSourceDigest.Pages();

        // Vault silinirse ya da tarama bir gün boş dönerse bütün olgular
        // boşlukta yeşil yanardı — "hiçbir sayfa bayat değil" cümlesi teknik
        // olarak doğru, pratik olarak anlamsız. Ölçütün tabanı yok: sayfa
        // sayısı değil, sıfır olmadığı sınanıyor.
        Assert.True(
            pages.Count > 0,
            $"{WikiSourceDigest.VaultPrefix}/ altında hiç `.md` bulunamadı. Vault kaldırıldıysa " +
            "bu bekçi de kaldırılmalı; kaldırılmadıysa tarama bozulmuş demektir.");

        Assert.True(
            pages.Any(page => page.HasSourcesKey),
            $"{WikiSourceDigest.VaultPrefix}/ altında `sources` bildiren tek sayfa yok — " +
            "frontmatter ayrıştırıcısı sessizce bozulmuş olabilir.");

        return pages;
    }

    /// <summary>
    /// Her sayfa ya kaynağını bildiriyor ya açıkça muaf. Üçüncü hâl —
    /// <i>kaynağı yok, sessizce atlanıyor</i> — bekçiyi tam da yeni eklenen
    /// sayfalarda kör bırakırdı.
    /// </summary>
    [Fact]
    public void Her_sayfa_ya_kaynak_bildiriyor_ya_muaf()
    {
        var unaccounted = Vault()
            .Where(page => !page.HasSourcesKey && !Exempt.ContainsKey(page.Path))
            .Select(page => page.RepoPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unaccounted.Length == 0,
            "Frontmatter'ında `sources:` olmayan ve muaf da olmayan sayfa(lar):\n  " +
            string.Join("\n  ", unaccounted) +
            "\n\nSayfa bir belgeden damıtıldıysa `sources:` yazın; damıtılmadıysa " +
            $"`{nameof(WikiSourceDigestTests)}.{nameof(Exempt)}`'e gerekçesiyle ekleyip " +
            $"`{nameof(ExpectedExemptCount)}`'u güncelleyin.");
    }

    /// <summary>
    /// Muafiyet listesi ne sessizce büyüyebilir ne de sessizce bayatlayabilir.
    /// </summary>
    [Fact]
    public void Muafiyet_listesi_sessizce_buyuyemez()
    {
        var pages = Vault().ToDictionary(page => page.Path, StringComparer.Ordinal);

        Assert.True(
            Exempt.Count == ExpectedExemptCount,
            $"Muafiyet listesi {ExpectedExemptCount} yerine {Exempt.Count} satır taşıyor. " +
            $"Değişiklik bilinçliyse `{nameof(ExpectedExemptCount)}`'u da güncelleyin.");

        foreach (var (path, reason) in Exempt)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(reason),
                $"{WikiSourceDigest.VaultPrefix}/{path} muafiyeti gerekçesiz.");

            Assert.True(
                pages.TryGetValue(path, out var page),
                $"{WikiSourceDigest.VaultPrefix}/{path} muafiyet listesinde ama vault'ta yok. " +
                "Bayat bir muafiyet, ileride aynı adla gelen bir sayfayı sessizce denetim dışı bırakır.");

            Assert.False(
                page!.HasSourcesKey,
                $"{page.RepoPath} artık `sources` bildiriyor ama hâlâ muafiyet listesinde — " +
                "yani damgası hiç denetlenmiyor. Muafiyet satırını silin ve " +
                $"`{nameof(ExpectedExemptCount)}`'u düşürün.");
        }
    }

    /// <summary>
    /// Bildirilen her kaynak yolu depoda <b>gerçekten</b> var, depo köküne göre
    /// yazılmış, ve sayfa içinde tekrarlanmamış.
    ///
    /// <para>
    /// Var olmayan bir yol iki şey demek olabilir: kaynak taşındı (sayfa artık
    /// neye dayandığını söyleyemiyor) ya da yol baştan uydurma. İkisi de damgayı
    /// anlamsızlaştırıyor, o yüzden ayrı ve önce sorulan bir soru.
    /// </para>
    /// </summary>
    [Fact]
    public void Bildirilen_her_kaynak_yolu_depoda_var()
    {
        var problems = new List<string>();

        foreach (var page in Vault().Where(page => page.HasSourcesKey))
        {
            if (page.Sources.Count == 0)
            {
                problems.Add($"{page.RepoPath}: `sources` boş — sayfa neye dayandığını söylemiyor.");
                continue;
            }

            foreach (var duplicate in page.Sources
                .GroupBy(source => source, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(source => source, StringComparer.Ordinal))
            {
                problems.Add($"{page.RepoPath}: `{duplicate}` iki kez yazılmış.");
            }

            foreach (var source in page.Sources)
            {
                if (source.StartsWith('/') || source.Contains("..", StringComparison.Ordinal)
                    || source.Contains('\\', StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{page.RepoPath}: `{source}` depo köküne göre düz bir yol değil.");
                    continue;
                }

                if (WikiSourceDigest.FileDigest(source) == WikiSourceDigest.MissingMarker)
                {
                    problems.Add($"{page.RepoPath}: `{source}` depoda yok.");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "Vault sayfalarının `sources` listelerinde sorun var:\n  " +
            string.Join("\n  ", problems));
    }

    /// <summary>
    /// Kaynak bildiren her sayfa damgalı. Damgasız bir sayfa bayatlığını
    /// <b>hiç</b> gösteremez — sessizce denetim dışı kalırdı, ki bu bekçinin
    /// engellemek için var olduğu şeyin ta kendisi.
    /// </summary>
    [Fact]
    public void Kaynak_bildiren_her_sayfa_damgali()
    {
        var unstamped = Vault()
            .Where(page => page.HasSourcesKey && string.IsNullOrWhiteSpace(page.RecordedDigest))
            .Select(page => page.RepoPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unstamped.Length == 0,
            "`source_digest` taşımayan sayfa(lar):\n  " +
            string.Join("\n  ", unstamped) +
            "\n\nDamgalayıcıyı koşturun:\n  " +
            $"{WikiSourceDigest.StampEnvironmentVariable}=1 dotnet test tests/Bizigo.UnitTests " +
            $"--filter FullyQualifiedName~{nameof(WikiSourceDigestStamper)}");
    }

    /// <summary>
    /// <b>Asıl soru.</b> Damga, kaynakların damgalandığı andaki hâlini taşıyor;
    /// bugünkü hâlleriyle karşılaştırılıyor. Ayrışma, sayfanın damıtıldığı
    /// zemin altından kaydı demek.
    ///
    /// <para>
    /// Mesaj <b>hangi kaynağın</b> değiştiğini yazıyor: "bir şey bayat" cümlesi
    /// eyleme dönüşmüyor, "şu sayfa, şu kaynağı yüzünden" dönüşüyor.
    /// </para>
    /// </summary>
    [Fact]
    public void Damga_kaynaklarin_bugunku_haliyle_ayni()
    {
        var stale = new List<string>();

        foreach (var page in Vault().Where(page =>
            page.HasSourcesKey && !string.IsNullOrWhiteSpace(page.RecordedDigest)))
        {
            var divergences = WikiSourceDigest.Divergences(
                page.RepoPath,
                page.RecordedDigest,
                WikiSourceDigest.Compute(page.Sources));

            if (divergences.Count > 0)
            {
                stale.Add($"{page.RepoPath}\n      - " + string.Join("\n      - ", divergences));
            }
        }

        Assert.True(
            stale.Count == 0,
            $"{stale.Count} vault sayfası damgalandığından beri kaynağı değişmiş:\n\n  " +
            string.Join("\n\n  ", stale) +
            "\n\nSıra: önce sayfayı gözden geçirin (kaynak sayfayı yanlışladı mı), " +
            "sonra yeniden damgalayın:\n  " +
            $"{WikiSourceDigest.StampEnvironmentVariable}=1 dotnet test tests/Bizigo.UnitTests " +
            $"--filter FullyQualifiedName~{nameof(WikiSourceDigestStamper)}");
    }
}
