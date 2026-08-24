using System.Security.Cryptography;
using System.Text;

namespace Bizigo.UnitTests;

/// <summary>
/// <b><c>docs/wiki/</c> sayfalarının kaynaklarından türeyen damga.</b>
///
/// <para>
/// Vault sayfaları <c>docs/epic/</c>, <c>CLAUDE.md</c>, <c>README.md</c> gibi
/// belgelerden <b>damıtıldı</b>. Damıtılmış bir cümlenin kaynağı değiştiğinde
/// hiçbir şey olmuyor: derleme geçiyor, test yeşil, sayfa yerinde duruyor —
/// yalnızca artık doğru değil. Hata yok, sayaç yok, belirti yok; bu deponun
/// §7'de adını koyduğu <b>sessiz yanlış davranış</b> sınıfının ta kendisi.
/// </para>
///
/// <para>
/// <b>Mekanizma:</b> her sayfa frontmatter'ında <c>sources:</c> taşıyor. Buradaki
/// kod o kaynakların <b>içeriğinden</b> deterministik bir dizge üretiyor ve
/// sayfaya <c>source_digest:</c> olarak yazılıyor. Bekçi
/// (<see cref="WikiSourceDigestTests"/>) dizgeyi yeniden hesaplayıp kayıtlıyla
/// karşılaştırıyor; ayrışma <b>hangi sayfanın hangi kaynağı</b> yüzünden
/// bayatladığını söylüyor.
/// </para>
///
/// <para>
/// <b>Biçim:</b> <c>sha256-12/v1 &lt;yol&gt;=&lt;12 hane&gt; &lt;yol&gt;=&lt;12 hane&gt; …</c>
/// — yola göre sıralı, boşlukla ayrılmış, tek satır. Tek bir toplam hash
/// <b>bilerek</b> kullanılmadı: toplam hash "bir şey değişti" der, hangi kaynağın
/// değiştiğini söyleyemez, ve bekçinin işe yarar olması tam olarak o cümleye
/// bağlı. Baştaki algoritma etiketi biçim değişirse eski damgaların yeni
/// damgalarla sessizce karşılaştırılmasını engelliyor.
/// </para>
///
/// <para>
/// <b>Hash girdisi normalize ediliyor</b> (UTF-8 BOM atılıyor, CRLF → LF):
/// aksi hâlde damga satır sonuna bağlı olurdu ve macOS'ta yeşil, Linux CI'da
/// kırmızı yanan bir bekçi — yani bekçinin kendisi bir sessiz kusur — çıkardı.
/// Duvar saati denkleme <b>hiç</b> girmiyor (§6): damga içerikten türüyor,
/// tarihten değil.
/// </para>
/// </summary>
public static class WikiSourceDigest
{
    /// <summary>Damga biçiminin sürümü. Biçim değişirse bu da değişir.</summary>
    public const string AlgorithmTag = "sha256-12/v1";

    /// <summary>SHA-256'nın kaç onaltılık hanesi yazılıyor.</summary>
    private const int HexLength = 12;

    /// <summary>
    /// Kaynak dosya yerinde değilse hane yerine bu yazılır. Onaltılık bir
    /// dizgeyle asla eşleşemez, dolayısıyla eksik bir kaynak damgayla
    /// <b>eşit çıkamaz</b>.
    /// </summary>
    public const string MissingMarker = "KAYIP";

    /// <summary>Damgalayıcıyı açan değişken. Bkz. <see cref="WikiSourceDigestStamper"/>.</summary>
    public const string StampEnvironmentVariable = "BIZIGO_WIKI_STAMP";

    public static bool StampRequested =>
        Environment.GetEnvironmentVariable(StampEnvironmentVariable) == "1";

    /// <summary>Vault kökü, depo köküne göre — mesajlarda da bu ön ek kullanılıyor.</summary>
    public const string VaultPrefix = "docs/wiki";

    public static string VaultRoot => Path.Combine(RepositoryLayout.Root, "docs", "wiki");

    /// <summary>
    /// Taranan bir vault sayfası.
    /// </summary>
    /// <param name="Path">Vault köküne göre yol, eğik çizgiyle (<c>concepts/x.md</c>).</param>
    /// <param name="AbsolutePath">Diskteki tam yol.</param>
    /// <param name="HasSourcesKey">Frontmatter'da <c>sources:</c> anahtarı var mı.</param>
    /// <param name="Sources">Bildirilen kaynak yolları, yazıldıkları sırada.</param>
    /// <param name="RecordedDigest">Sayfada yazılı damga; yoksa <c>null</c>.</param>
    /// <param name="SourcesEndLine">
    /// <c>sources</c> değerinin son satırının indeksi — damgalayıcı yeni satırı
    /// buranın hemen altına koyuyor. Anahtar yoksa <c>-1</c>.
    /// </param>
    /// <param name="DigestLine">Var olan <c>source_digest</c> satırının indeksi; yoksa <c>null</c>.</param>
    public sealed record Page(
        string Path,
        string AbsolutePath,
        bool HasSourcesKey,
        IReadOnlyList<string> Sources,
        string? RecordedDigest,
        int SourcesEndLine,
        int? DigestLine)
    {
        /// <summary>Depo köküne göre yol — bekçi mesajları bunu yazıyor.</summary>
        public string RepoPath => $"{VaultPrefix}/{Path}";
    }

    /// <summary>
    /// Vault'taki bütün <c>.md</c> sayfaları — <b>diski tarayarak</b>, elle
    /// tutulan bir listeden değil (§7: elle liste bu depoda beş kez kör kaldı).
    ///
    /// <para>
    /// <c>CiCoverageTests</c> aynı işi <c>git ls-files</c> ile yapıyor ama oradaki
    /// soru farklı — <i>CI neyi checkout ediyor</i>. Buradaki soru
    /// <i>vault'ta damıtılmış olduğunu iddia eden hangi sayfalar var</i>, ve
    /// bayatlama commit'le değil <b>yazmakla</b> başlıyor: henüz commitlenmemiş
    /// bir sayfa da denetlenmeli. O yüzden dosya sistemi taranıyor.
    /// </para>
    ///
    /// <para>
    /// Noktayla başlayan dizinler (<c>.obsidian/</c>) dışarıda: Obsidian'ın
    /// çalışma zamanı durumu, damıtılmış bilgi değil.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Page> Pages()
    {
        var root = VaultRoot;

        if (!Directory.Exists(root))
        {
            return [];
        }

        var pages = new List<Page>();

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file)
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');

            if (relative.Split('/').Any(segment => segment.StartsWith('.')))
            {
                continue;
            }

            pages.Add(ReadPage(file, relative));
        }

        return [.. pages.OrderBy(page => page.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Kaynak listesinden damga dizgesini üretir. Sıralama <b>yola göre</b>:
    /// <c>sources</c> listesinin sırası değişince damga değişmesin — sıra
    /// değişikliği bilgi değil biçim.
    /// </summary>
    public static string Compute(IReadOnlyList<string> sources)
    {
        var entries = sources
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => $"{path}={FileDigest(path)}");

        return AlgorithmTag + " " + string.Join(' ', entries);
    }

    /// <summary>Tek bir kaynağın normalize edilmiş içeriğinden 12 haneli özet.</summary>
    public static string FileDigest(string relativePath)
    {
        var absolute = System.IO.Path.Combine(
            RepositoryLayout.Root,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        if (!File.Exists(absolute))
        {
            return MissingMarker;
        }

        var normalized = Normalize(File.ReadAllBytes(absolute));
        return Convert.ToHexStringLower(SHA256.HashData(normalized))[..HexLength];
    }

    /// <summary>
    /// Kayıtlı damga ile bugünkü damganın <b>farkları</b> — insan cümlesiyle.
    /// Boş liste "sayfa taze" demek.
    /// </summary>
    public static IReadOnlyList<string> Divergences(string pageLabel, string? recorded, string current)
    {
        var report = new List<string>();

        var (recordedTag, recordedMap) = Parse(recorded, report, pageLabel);
        var (currentTag, currentMap) = Parse(current, report, pageLabel);

        if (!string.Equals(recordedTag, currentTag, StringComparison.Ordinal))
        {
            report.Add(
                $"damga `{(recordedTag.Length == 0 ? "(etiketsiz)" : recordedTag)}` biçimiyle yazılmış, " +
                $"bugünkü biçim `{currentTag}`.");
        }

        foreach (var (path, hash) in currentMap.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!recordedMap.TryGetValue(path, out var stamped))
            {
                report.Add($"`{path}` damgada yok — `sources` listesine damgadan sonra eklenmiş.");
            }
            else if (!string.Equals(stamped, hash, StringComparison.Ordinal))
            {
                report.Add(hash == MissingMarker
                    ? $"`{path}` artık depoda yok (damgalandığında {stamped} idi)."
                    : $"`{path}` **değişti** (damga {stamped}, bugün {hash}).");
            }
        }

        foreach (var path in recordedMap.Keys
            .Where(key => !currentMap.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal))
        {
            report.Add($"`{path}` damgada var ama `sources` listesinde yok — listeden çıkarılmış.");
        }

        return report;
    }

    /// <summary>
    /// Sayfanın damgasını bugünkü kaynaklara göre <b>yazar</b>. Yalnızca
    /// <see cref="WikiSourceDigestStamper"/> çağırıyor; bekçi bu metoda hiç
    /// dokunmuyor — damgayı yazan ile doğrulayan aynı koşumda buluşursa
    /// doğrulama hiçbir şey kanıtlamaz.
    /// </summary>
    /// <returns>Dosya gerçekten değiştiyse <c>true</c>.</returns>
    public static bool Stamp(Page page)
    {
        if (!page.HasSourcesKey || page.Sources.Count == 0)
        {
            throw new InvalidOperationException(
                $"{page.RepoPath}: `sources` yok ya da boş; damgalanacak bir şey yok.");
        }

        var missing = page.Sources
            .Where(source => FileDigest(source) == MissingMarker)
            .OrderBy(source => source, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"{page.RepoPath}: var olmayan kaynak(lar) damgalanamaz — {string.Join(", ", missing)}. " +
                "Damga, kaynağın bugünkü hâlini kaydetmek demek; olmayan bir dosyanın hâli yok.");
        }

        var digest = Compute(page.Sources);

        if (string.Equals(digest, page.RecordedDigest, StringComparison.Ordinal))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(page.AbsolutePath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(hasBom ? bytes.AsSpan(3) : bytes);

        // Satır sonu korunuyor: '\n' ile bölünen satırlar CRLF dosyada sonda
        // '\r' taşımaya devam ediyor, '\n' ile birleştirmek onları aynen geri
        // veriyor. Eklenen satır da aynı sonu alıyor.
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var lines = new List<string>(text.Split('\n'));
        var stamped = $"source_digest: \"{digest}\"" + (crlf ? "\r" : string.Empty);

        if (page.DigestLine is int existing)
        {
            lines[existing] = stamped;
        }
        else
        {
            lines.Insert(page.SourcesEndLine + 1, stamped);
        }

        File.WriteAllText(page.AbsolutePath, string.Join('\n', lines), new UTF8Encoding(hasBom));
        return true;
    }

    private static (string Tag, Dictionary<string, string> Map) Parse(
        string? digest,
        List<string> report,
        string pageLabel)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(digest))
        {
            return (string.Empty, map);
        }

        var tokens = digest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens.Skip(1))
        {
            var separator = token.LastIndexOf('=');

            if (separator <= 0 || separator == token.Length - 1)
            {
                // Sessizce atlamak, bozuk bir damgayı taze göstermek olurdu.
                report.Add($"{pageLabel}: damga dizgesinde bozuk parça: `{token}`.");
                continue;
            }

            map[token[..separator]] = token[(separator + 1)..];
        }

        return (tokens[0], map);
    }

    /// <summary>BOM'suz, LF'li gövde — damga platformdan bağımsız olsun diye.</summary>
    private static byte[] Normalize(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        var output = new List<byte>(bytes.Length - start);

        for (var index = start; index < bytes.Length; index++)
        {
            if (bytes[index] == 0x0D && index + 1 < bytes.Length && bytes[index + 1] == 0x0A)
            {
                continue;
            }

            output.Add(bytes[index]);
        }

        return [.. output];
    }

    private static string Chomp(string line) => line.EndsWith('\r') ? line[..^1] : line;

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length >= 2
            && ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1].Trim();
        }

        return trimmed;
    }

    /// <summary>
    /// Frontmatter'dan <c>sources</c> ve <c>source_digest</c> okuyan <b>dar</b>
    /// ayrıştırıcı. Genel bir YAML okuyucusu değil — ve anlamadığı bir şeyle
    /// karşılaşınca <b>boş dönmüyor, atıyor</b>: sessizce boş dönen bir
    /// ayrıştırıcı, sayfayı "kaynaksız" göstererek bekçiyi kör bırakırdı.
    /// </summary>
    private static Page ReadPage(string absolutePath, string relative)
    {
        var label = $"{VaultPrefix}/{relative}";
        var lines = File.ReadAllText(absolutePath).Split('\n');

        if (lines.Length == 0 || Chomp(lines[0]) != "---")
        {
            return new Page(relative, absolutePath, false, [], null, -1, null);
        }

        var close = -1;

        for (var index = 1; index < lines.Length; index++)
        {
            if (Chomp(lines[index]) == "---")
            {
                close = index;
                break;
            }
        }

        if (close < 0)
        {
            throw new InvalidOperationException($"{label}: frontmatter kapanış `---` satırı yok.");
        }

        List<string>? sources = null;
        var sourcesEnd = -1;
        string? recorded = null;
        int? digestLine = null;

        for (var index = 1; index < close; index++)
        {
            var line = Chomp(lines[index]);

            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();

            if (key == "sources")
            {
                if (sources is not null)
                {
                    throw new InvalidOperationException($"{label}: `sources` iki kez yazılmış.");
                }

                (sources, sourcesEnd) = ReadList(lines, index, close, value, label);
                index = sourcesEnd;
            }
            else if (key == "source_digest")
            {
                if (recorded is not null)
                {
                    throw new InvalidOperationException($"{label}: `source_digest` iki kez yazılmış.");
                }

                recorded = Unquote(value);
                digestLine = index;
            }
        }

        return new Page(relative, absolutePath, sources is not null, sources ?? [], recorded, sourcesEnd, digestLine);
    }

    /// <summary>
    /// <c>sources:</c> değerini okur. Vault'ta <b>iki</b> biçim birden kullanımda:
    /// akış (<c>[a, b]</c>) ve blok (<c>- a</c> satırları). İkisini de tanımak
    /// zorunlu — birini tanımayan ayrıştırıcı o sayfaları kaynaksız sanardı.
    /// </summary>
    private static (List<string> Items, int EndLine) ReadList(
        string[] lines,
        int keyLine,
        int close,
        string inline,
        string label)
    {
        if (inline.StartsWith('['))
        {
            var end = inline.LastIndexOf(']');

            if (end < 0)
            {
                throw new InvalidOperationException($"{label}: `sources` akış listesi `]` ile kapanmamış.");
            }

            var items = inline[1..end]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(Unquote)
                .Where(item => item.Length > 0)
                .ToList();

            return (items, keyLine);
        }

        if (inline.Length > 0)
        {
            return ([Unquote(inline)], keyLine);
        }

        var block = new List<string>();
        var last = keyLine;

        for (var index = keyLine + 1; index < close; index++)
        {
            var line = Chomp(lines[index]);

            if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
            {
                break;
            }

            var trimmed = line.TrimStart();

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label}: `sources` altında liste ögesi olmayan satır: `{line}`.");
            }

            block.Add(Unquote(trimmed[2..]));
            last = index;
        }

        return (block, last);
    }
}
