using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bizigo.Contracts.Security;

/// <summary>
/// Prompt'a giden metnin geçtiği <b>tek</b> kapı (T41 — F4'ün önkoşulu).
///
/// <para>
/// <b>Değişmez:</b> sır içeren bir satır hiçbir içerik düzeyinde prompt'a
/// girmez. <c>summary</c> / <c>masked</c> / <c>raw</c> üçlüsü ayarlanabilir bir
/// parametre — "ne kadar bağlam" sorusunu cevaplıyor. Bu kapı ayarlanabilir
/// değil; "hangi bağlam hiç gitmez" sorusunu cevaplıyor ve cevabı düzeyden
/// bağımsız.
/// </para>
///
/// <para>
/// <b>Kapının tek olduğunu derleyici tutuyor, bir yorum değil.</b> Kapı ile
/// çıktısı <b>aynı tip</b>: yapıcı <c>private</c>, dolayısıyla bir
/// <see cref="RedactedPrompt"/> örneği yalnızca <see cref="Redact"/>'ten
/// çıkabilir. Prompt yolunu kuran taraf (F4) girdisini bu tipten aldığı sürece
/// "kapıyı atlamayı unuttum" diye bir hâl <b>derlenmez</b> — ikinci giriş
/// açmak için bu dosyayı değiştirmek gerekir, ve o hareket görünür.
/// </para>
///
/// <para>
/// Alternatifi bir <c>string</c> döndürmekti; o zaman kapı yalnızca bir
/// çağrı alışkanlığı olurdu ve unutulduğu gün hiçbir şey kırılmazdı — bu
/// deponun sessiz-yanlış sınıfı (§7).
/// </para>
///
/// <h3>Üç katman, sırasıyla</h3>
/// <list type="number">
/// <item><b>C — üretici söz dizimi.</b> <b>Maskeliyor.</b>
/// <see cref="SecretPatterns.LogAssignment"/>; anahtar kelime listesi config
/// kolu ile ortak, çapası ayrı.</item>
/// <item><b>B — dar bilinen biçimler.</b> <b>Maskeliyor.</b> Yalnızca kendini
/// tarif eden ve eskimeyen biçimler: PEM gövdesi, JWT'nin üç parçası,
/// <c>Authorization: Bearer/Basic …</c>.</item>
/// <item><b>A — entropi.</b> <b>Maskelemiyor, SAYIYOR.</b> Gölge mod: "C+B'nin
/// üstüne kaç belirteç daha maskelerdim" sorusunun cevabını yayıyor, metne
/// dokunmuyor.</item>
/// </list>
///
/// <para>
/// A'nın gölgede olmasının sebebi §4'ün asimetrisi: ölçülebilen tek hata yönü
/// <b>fazla maskeleme</b>. Sırrı kaçırmanın belirtisi yok. A tam olarak
/// maliyeti fazla maskeleme olan katman — hash, UUID, session id, base64 gövde
/// ağ logunda bol — o yüzden ürüne girmeden önce kendini ölçüyor. Terfi ayrı
/// bir ticket.
/// </para>
///
/// <h3>Bu kapının TANIYAMADIKLARI</h3>
///
/// <para>
/// Hiçbir redaksiyon kapısı tam değildir ve <b>tam olduğu iddiası, olmadığı
/// iddiasından tehlikelidir</b>: tam sanılan bir kapı arkasındaki <c>raw</c>
/// düzeyini gerekçesiz açtırır. Bilinen boşluklar:
/// </para>
/// <list type="bullet">
/// <item><b>Sağlayıcı anahtar katalogları</b> (<c>AKIA…</c>, <c>ghp_…</c>,
/// <c>xoxb-…</c>) — <b>bilinçli kapsam dışı</b>. K2'nin alanı ağ cihazı; o
/// listeler bulut için yazıldı, dışarıdan besleniyor ve eskiyor.</item>
/// <item><b>Anahtar kelimesiz taşınan sırlar.</b> Bir parolanın yanında
/// <c>password</c> yazmıyorsa C onu görmez.</item>
/// <item><b>Yüksek entropili bilinmeyen biçimler</b> — A gölgede olduğu sürece
/// yalnızca sayılıyor, maskelenmiyor. Sayı
/// <c>redaction_shadow_candidates</c>.</item>
/// <item><b>Bilinmeyen üretici söz dizimi.</b> Liste bu ürünün baktığı üç
/// vendor için yazıldı; dördüncüsü bir bakım kalemi.</item>
/// <item><b>Düşük entropili gerçek parolalar</b> (<c>admin123</c>) — C
/// bulursa maskelenir, bulamazsa A da bulamaz.</item>
/// <item><b>Ayırıcısı köşeli parantez olan biçimler</b>:
/// <c>cfgattr="psksecret[…]"</c>. Anahtar kelimeden sonra <c>[</c> geliyor,
/// desenin istediği ayırıcı gelmiyor. Ölçüldü (T41 uygulama turu). Gerçek
/// FortiGate bu alanda değeri kendi maskeliyor (<c>psksecret[*]</c>), o
/// yüzden bugün bir sızıntı değil — ama biçim ailesi kör noktada.</item>
/// </list>
///
/// <para>
/// Ters yöndeki bilinen kusur da burada: boşluk ayırıcı düz İngilizce anlatımı
/// tutuyor (<c>Failed password for admin from …</c> → satır sonuna kadar
/// maskelenir). Altın korpusta 0 kez oluyor (ölçüldü); görünür yön bilinçli
/// tercih.
/// </para>
/// </summary>
[JsonConverter(typeof(RedactedPromptJsonConverter))]
public sealed partial class RedactedPrompt
{
    /// <summary>
    /// Entropi eşiği (bit/karakter). Rastgele base64 ~5-6, onaltılık ~4, düz
    /// İngilizce ~2.5-3 bit/karakter üretiyor; 3.5 ikisinin arasında duruyor.
    /// <b>Eşik yayınla birlikte kaydediliyor</b> — eşiksiz bir sayı, eşiği
    /// değiştiren bir sonraki turda kendi geçmişiyle karşılaştırılamaz.
    /// </summary>
    public const double DefaultEntropyThreshold = 3.5;

    /// <summary>
    /// Entropi hesabına giren en kısa belirteç. Kısa belirteçlerde Shannon
    /// entropisi anlamsız: dört farklı karakterli dört harflik bir sözcük de
    /// 2 bit/karakter verir.
    /// </summary>
    public const int DefaultMinShadowTokenLength = 20;

    private const string PemBegin = "-----BEGIN";
    private const string PemEnd = "-----END";

    private static readonly char[] TokenSeparators =
        [' ', '\t', '\n', '\r', '"', '\'', '=', ',', ';', '(', ')', '[', ']', '{', '}', '<', '>', '|'];

    /// <summary>
    /// JWT: üç base64url parçası ve <c>eyJ</c> ile başlayan bir başlık —
    /// <c>{"</c> dizgesinin base64'ü. Kendini tarif ediyor ve eskimiyor.
    /// </summary>
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}", RegexOptions.ExplicitCapture)]
    private static partial Regex JwtToken();

    /// <summary>
    /// <c>Authorization: Bearer &lt;token&gt;</c> / <c>Basic &lt;token&gt;</c>.
    /// <c>Authorization</c> bağlamı zorunlu: çıplak bir <c>Bearer</c> sözcüğü
    /// düz metinde de geçiyor.
    /// </summary>
    [GeneratedRegex(
        @"\bauthorization\s*[:=]\s*""?(?:bearer|basic)\s+(?<value>[A-Za-z0-9\-._~+/]+=*)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex AuthorizationHeader();

    /// <summary>
    /// <b>Tek yapıcı ve <c>private</c>.</b> Bir <see cref="RedactedPrompt"/>
    /// örneği ancak <see cref="Redact"/>'ten çıkabilir.
    /// </summary>
    private RedactedPrompt(
        string text,
        int maskedValues,
        int shadowCandidates,
        int evaluatedTokens,
        int totalTokens,
        double entropyThreshold,
        int minShadowTokenLength)
    {
        Text = text;
        MaskedValues = maskedValues;
        ShadowCandidates = shadowCandidates;
        EvaluatedTokens = evaluatedTokens;
        TotalTokens = totalTokens;
        EntropyThreshold = entropyThreshold;
        MinShadowTokenLength = minShadowTokenLength;
    }

    /// <summary>Maskelenmiş metin — prompt'a giren şey.</summary>
    public string Text { get; }

    /// <summary>C+B'nin maskelediği <b>ayrı</b> değer sayısı.</summary>
    public int MaskedValues { get; }

    /// <summary>
    /// Gölge katmanın adayları: entropi eşiğini geçen ve <b>C+B'nin
    /// maskelemediği</b> belirteç sayısı. Sorunun tamamı "ek olarak" —
    /// C+B'nin zaten maskelediğini saymak terfi kararını şişirirdi.
    /// </summary>
    public int ShadowCandidates { get; }

    /// <summary>
    /// Oranın <b>paydası</b>: entropi hesabına gerçekten giren belirteç
    /// sayısı. Payda söylenmeden oran okunamaz.
    /// </summary>
    public int EvaluatedTokens { get; }

    /// <summary>Metindeki toplam belirteç — paydanın yanlış okunmaması için.</summary>
    public int TotalTokens { get; }

    public double EntropyThreshold { get; }

    public int MinShadowTokenLength { get; }

    /// <summary>
    /// Ham sayı prompt uzunluğuyla büyüyor; terfi kararına bakan sayı bu.
    /// Payda sıfırsa oran da sıfır — "ölçülmedi" değil, "değerlendirilecek
    /// belirteç yoktu".
    /// </summary>
    public double ShadowRatio =>
        EvaluatedTokens > 0 ? (double)ShadowCandidates / EvaluatedTokens : 0d;

    /// <summary>
    /// Kanıt paketine yazılan alanlar. <b>Sıfırken de yazılıyor</b>:
    /// gizlenen bir sıfır "henüz ölçülmedi" ile "ölçüldü, sıfır" farkını
    /// siler. Paket saklandığı için sayı sonradan da okunabiliyor.
    /// </summary>
    public IReadOnlyDictionary<string, object> EvidenceFields() =>
        new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["redaction_masked_values"] = MaskedValues,
            ["redaction_shadow_candidates"] = ShadowCandidates,
            ["redaction_shadow_ratio"] = ShadowRatio,
            ["redaction_shadow_evaluated_tokens"] = EvaluatedTokens,
            ["redaction_shadow_total_tokens"] = TotalTokens,
            ["redaction_shadow_entropy_threshold"] = EntropyThreshold,
            ["redaction_shadow_min_token_length"] = MinShadowTokenLength,
        };

    /// <summary>
    /// Metni prompt'a girmeye uygun hâle getirir.
    ///
    /// <para>
    /// C ve B <b>bulur</b>, ikame <see cref="SecretRedactor.RedactExact"/>
    /// üzerinden yapılır — ikinci bir ikame uygulaması yok. A yalnızca sayar ve
    /// <b>maskelenmiş</b> metin üzerinde sayar; "ek olarak" sorusunun tanımı bu.
    /// </para>
    /// </summary>
    public static RedactedPrompt Redact(
        string? text,
        double entropyThreshold = DefaultEntropyThreshold,
        int minShadowTokenLength = DefaultMinShadowTokenLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new RedactedPrompt(string.Empty, 0, 0, 0, 0, entropyThreshold, minShadowTokenLength);
        }

        // Satır sonu normalize: `LogAssignment` çok satırlı ve `$` ile
        // çalışıyor; `\r` değere sızarsa maskelenen dizge metindekiyle
        // eşleşmez.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        var discovered = new HashSet<string>(StringComparer.Ordinal);

        CollectVendorAssignments(normalized, discovered);   // katman C
        CollectKnownFormats(normalized, discovered);        // katman B

        var masked = SecretRedactor.RedactExact(normalized, discovered);

        var (candidates, evaluated, total) = CountShadow(masked, entropyThreshold, minShadowTokenLength);

        return new RedactedPrompt(
            masked,
            discovered.Count,
            candidates,
            evaluated,
            total,
            entropyThreshold,
            minShadowTokenLength);
    }

    // ------------------------------------------------------- katman C

    private static void CollectVendorAssignments(string text, HashSet<string> discovered)
    {
        foreach (Match match in SecretPatterns.LogAssignment().Matches(text))
        {
            // Anahtar kelime bir açıklamanın İÇİNDE geçiyorsa dokunma —
            // config kolundaki muafiyetin aynısı, aynı sınırlı önek üzerinde.
            if (SecretPatterns.FreeTextField().IsMatch(match.Groups["prefix"].Value))
            {
                continue;
            }

            // Hangi grup dolduysa hangi sınır kuralı işledi: `assigned` (=/:)
            // satır sonuna kadar, `spaced` (boşluk) yalnızca ilk belirteç.
            var spaced = match.Groups["spaced"];
            var value = TirnakSoy((spaced.Success ? spaced.Value : match.Groups["assigned"].Value).Trim());

            if (spaced.Success && !BoslukliDegerSirBenziyor(value))
            {
                continue;
            }

            Add(discovered, value);
        }
    }

    private static string TirnakSoy(string value) =>
        value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0]
            ? value[1..^1].Trim()
            : value;

    /// <summary>
    /// Boşlukla ayrılmış bir değerin sır olup olmadığına bakan <b>tek</b> ölçüt:
    /// içinde küçük harf olmayan en az bir karakter var mı.
    ///
    /// <para>
    /// <b>Neden gerekli.</b> Boşluk ayırıcı düz anlatımı da tutuyor —
    /// <c>Failed password for admin …</c> satırında ilk belirteç <c>for</c>.
    /// Sınır ilk belirtece çekildikten sonra satırın geri kalanı kurtuluyor
    /// ama <c>for</c> maskelenmeye devam ederdi, ve ikame <b>bütün metinde</b>
    /// çalıştığı için <c>information</c> → <c>in[gizli]mation</c> olurdu:
    /// korunan hiçbir şey yok, prompt bozuk. O satırda maskelenecek bir değer
    /// zaten yok — anahtar kelime orada bir <i>hata sebebi</i>.
    /// </para>
    ///
    /// <para>
    /// <b>Neden bu ölçüt, bir sözcük listesi değil.</b> Liste bakım kalemi
    /// olurdu ve bu depo elle tutulan listelerin bekçiyi körleştirdiğini
    /// ölçtü. Üretici anahtarları rakam, büyük harf ya da ayraç taşıyor —
    /// <c>pub1</c>, <c>S3cret</c>, <c>qX2cV6bN8mK4jH7g</c>, base64 blob'ları.
    /// Ölçüt mekanik ve tek satır.
    /// </para>
    ///
    /// <para>
    /// <b>Neyi kaçırır — yazılı olsun:</b> boşlukla ayrılmış, tamamı küçük
    /// harf bir parola (<c>password correcthorse</c>). <c>=</c>/<c>:</c>
    /// ayırıcıda ve config kolunda bu ölçüt <b>hiç çalışmıyor</b>, yani
    /// oradaki aynı parola maskeleniyor. Bilinen ve dar bir boşluk.
    /// </para>
    /// </summary>
    private static bool BoslukliDegerSirBenziyor(string value) =>
        value.Any(static c => !char.IsLower(c));

    // ------------------------------------------------------- katman B

    private static void CollectKnownFormats(string text, HashSet<string> discovered)
    {
        foreach (Match match in JwtToken().Matches(text))
        {
            Add(discovered, match.Value);
        }

        foreach (Match match in AuthorizationHeader().Matches(text))
        {
            Add(discovered, match.Groups["value"].Value);
        }

        CollectPemBodies(text, discovered);
    }

    /// <summary>
    /// PEM gövdesi satır satır toplanıyor, tek bir çok satırlı desenle değil.
    ///
    /// <para>
    /// <c>BEGIN … END</c> arasını bir <c>.*?</c> ile yakalamak serbest joker
    /// demek; <c>END</c> hiç gelmezse desen bütün metni tarar. Doğrusal bir
    /// tarama aynı işi yapıyor ve maliyeti girdi uzunluğunda sabit kalıyor.
    /// </para>
    ///
    /// <para>
    /// <b>Kapanış satırı gelmezse gövde yine de maskeleniyor</b>: kesilmiş bir
    /// anahtar da anahtardır ve prompt'a giren metin çoğu zaman kesiliyor.
    /// </para>
    /// </summary>
    private static void CollectPemBodies(string text, HashSet<string> discovered)
    {
        var inside = false;

        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(PemBegin, StringComparison.Ordinal))
            {
                inside = true;
                continue;
            }

            if (line.Contains(PemEnd, StringComparison.Ordinal))
            {
                inside = false;
                continue;
            }

            if (inside)
            {
                Add(discovered, line.Trim());
            }
        }
    }

    private static void Add(HashSet<string> discovered, string value)
    {
        // Boş ve tek karakterlik değer maskelenmez: metnin her yerinde geçer ve
        // sonucu okunamaz hâle getirir. Uzunluk TABANI yok — dört karakterlik
        // bir SNMP community gerçek bir sır (bkz. SecretRedactor.MinFragment).
        if (value.Length >= 2 && !value.Contains(SecretRedactor.Mask, StringComparison.Ordinal))
        {
            discovered.Add(value);
        }
    }

    // ------------------------------------------------------- katman A (gölge)

    /// <summary>
    /// Gölge sayımı. <b>Hiçbir şeye dokunmuyor</b> — girdisi zaten maskelenmiş
    /// metin, çıktısı üç sayı.
    /// </summary>
    private static (int Candidates, int Evaluated, int Total) CountShadow(
        string masked,
        double entropyThreshold,
        int minTokenLength)
    {
        var candidates = 0;
        var evaluated = 0;
        var total = 0;

        foreach (var raw in masked.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ',', ';', ':', '!', '?');

            if (token.Length == 0)
            {
                continue;
            }

            total++;

            if (token.Length < minTokenLength || token.Contains(SecretRedactor.Mask, StringComparison.Ordinal))
            {
                continue;
            }

            evaluated++;

            if (ShannonEntropy(token) >= entropyThreshold)
            {
                candidates++;
            }
        }

        return (candidates, evaluated, total);
    }

    /// <summary>Karakter başına Shannon entropisi (bit).</summary>
    private static double ShannonEntropy(string token)
    {
        var counts = new Dictionary<char, int>();

        foreach (var c in token)
        {
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;
        }

        var entropy = 0d;

        foreach (var count in counts.Values)
        {
            var p = (double)count / token.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    /// <summary>Rapora ve kayda yazılacak tek satırlık özet.</summary>
    public static string Describe(RedactedPrompt result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"maskelenen={result.MaskedValues} gölge_aday={result.ShadowCandidates} " +
            $"gölge_oran={result.ShadowRatio:F4} payda={result.EvaluatedTokens} " +
            $"eşik={result.EntropyThreshold:F2}bit/krk min_uzunluk={result.MinShadowTokenLength}");
    }
}
