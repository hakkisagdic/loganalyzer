using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bizigo.Parsing.Grok;

/// <summary>Tek bir maske tanımı — ad, regex ve grok karşılığı.</summary>
/// <param name="Name">
/// Hem Drain3'ün <c>mask_with</c> değeri hem de bir <b>grok pattern adı</b>.
/// İkisinin aynı olması K14'ün "maskeleme sinerjisi" maddesinin tamamı:
/// mined şablondaki <c>&lt;IPV4&gt;</c> doğrudan <c>%{IPV4:...}</c> taslağına
/// dönüşebiliyor.
/// </param>
public sealed record MaskDefinition(string Name, string Pattern, Regex Regex);

/// <summary>
/// Maskeleme sözlüğünün .NET tarafı — kaynak
/// <c>catalog/masks/bizigo-masks.yaml</c> (K14, F1 §9).
///
/// <para>
/// <b>Neden .NET de maskeliyor:</b> sidecar sıcak yolda değil, dolayısıyla
/// bir olayın <c>template_id</c>'sini yazma anında ondan soramayız. Bunun
/// yerine burada <b>aynı</b> maskeleri uygulayıp bir imza üretiyoruz; sidecar
/// bir kez o imzanın hangi kümeye düştüğünü söyleyince aynı imzalı sonraki
/// olaylar sidecar'a hiç gitmeden etiketleniyor
/// (<c>Bizigo.Ingest.Discovery</c>).
/// </para>
///
/// <para>
/// Bu ancak iki motorun <b>birebir</b> aynı çıktıyı vermesiyle çalışır.
/// Sözlükteki <c>golden</c> bölümü tam olarak bunu sınıyor ve aynı örnekler
/// Python tarafında da koşuyor (<c>sidecar/tests/test_masks.py</c>). Regex'ler
/// bu yüzden iki motorun ortak alt kümesinde yazılmak zorunda.
/// </para>
/// </summary>
public sealed class MaskCatalog
{
    /// <summary>
    /// Maskeleme <b>güvenilmeyen</b> metin üzerinde koşuyor, o yüzden bir sınır
    /// gerekiyor — ama sınır <b>uzunluk</b>, süre değil.
    ///
    /// <para>
    /// <b>Neden duvar saati değil:</b> zaman aşımı pattern'i değil makineyi
    /// ölçer. Yüklü bir makinede sağlıklı bir maske bütçeyi aşar, hızlı bir
    /// makinede tehlikeli bir maske bütçeye sığar; ikisi de yanlış cevap. Üstelik
    /// bedeli sessiz: <see cref="Signature"/> boş dönüyor, olay etiketsiz kalıyor
    /// ve keşif kuyruğuna hiç girmiyor. Aynı hata sınıfı F1'de üç ayrı yerde
    /// çıktı (bkz. F1 kapanışı).
    /// </para>
    ///
    /// <para>
    /// <b>Neden uzunluk yetiyor:</b> sözlükteki hiçbir maskede iç içe niceleyici
    /// yok — hepsi sınırlı tekrar (<c>{1,4}</c>, <c>{0,6}</c>, <c>{1,3}</c>) ya da
    /// tek düzey <c>+</c>. Felaket geri izleme (catastrophic backtracking) bu
    /// yapılarla mümkün değil; maliyet girdi uzunluğunda <b>doğrusal</b>. Yani
    /// korunması gereken tek şey dev bir satır, ve onu uzunluk deterministik
    /// olarak durduruyor.
    /// </para>
    /// </summary>
    public const int MaxInputLength = 16 * 1024;

    private readonly MaskDefinition[] _masks;
    private long _skippedTooLong;

    /// <summary>
    /// Uzunluk sınırı yüzünden maskelenmeden geçen satır sayısı.
    ///
    /// <para>
    /// Sıfırdan büyük olması bir arıza değil ama bir <b>bilgi</b>: o satırlar
    /// <c>template_id</c> almıyor ve keşif kuyruğuna girmiyor. Sessizce olması,
    /// zaman aşımının en kötü yanıydı; sayaç tam da onu geri getirmemek için var.
    /// </para>
    /// </summary>
    public long SkippedTooLong => Interlocked.Read(ref _skippedTooLong);

    private MaskCatalog(
        int version,
        string maskPrefix,
        string maskSuffix,
        MaskDefinition[] masks,
        IReadOnlyList<MaskSample> golden,
        string sourcePath)
    {
        Version = version;
        MaskPrefix = maskPrefix;
        MaskSuffix = maskSuffix;
        _masks = masks;
        Golden = golden;
        SourcePath = sourcePath;
    }

    public int Version { get; }

    public string MaskPrefix { get; }

    public string MaskSuffix { get; }

    public string SourcePath { get; }

    public IReadOnlyList<MaskDefinition> Masks => _masks;

    public IReadOnlyList<MaskSample> Golden { get; }

    public IEnumerable<string> Names => _masks.Select(static m => m.Name);

    /// <summary>Sözlük yüklenemediğinde kullanılan boş katalog — imza üretmez.</summary>
    public static MaskCatalog Empty { get; } =
        new(0, "<", ">", [], [], "(yok)");

    public static MaskCatalog LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Maskeleme sözlüğü bulunamadı: {path}. Sidecar ile .NET aynı dosyayı okumak zorunda (K14).",
                path);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var document = deserializer.Deserialize<MaskDocument>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path}: sözlük boş.");

        if (document.Masks is null || document.Masks.Count == 0)
        {
            throw new InvalidOperationException($"{path}: `masks` boş — imza üretilemez.");
        }

        var masks = new List<MaskDefinition>(document.Masks.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in document.Masks)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Regex))
            {
                throw new InvalidOperationException($"{path}: `name` ve `regex` zorunlu.");
            }

            if (!seen.Add(entry.Name))
            {
                throw new InvalidOperationException(
                    $"{path}: '{entry.Name}' maskesi iki kez tanımlı. Parametre çıkarımı belirsizleşir.");
            }

            // Sıra anlamlı: sözlükteki sıra uygulama sırası. Sözlüğü alfabetik
            // sıralamak cazip ama şablonu bozar (özel olan önce gelmeli).
            masks.Add(new MaskDefinition(entry.Name, entry.Regex, Compile(entry.Regex, path)));
        }

        var golden = (document.Golden ?? [])
            .Select(static g => new MaskSample(g.Input ?? string.Empty, g.Masked ?? string.Empty))
            .ToArray();

        var catalog = new MaskCatalog(
            document.Version,
            document.MaskPrefix ?? "<",
            document.MaskSuffix ?? ">",
            [.. masks],
            golden,
            path);

        catalog.Warmup();
        return catalog;
    }

    /// <summary>
    /// Önce doğrusal motor, olmazsa geri izleme — <see cref="GrokCompiler"/> ile
    /// aynı kalıp, ama <b>hiçbirinde zaman aşımı yok</b>.
    ///
    /// <para>
    /// Sözlüğün lookaround kullanan maskeleri (<c>IPV4</c>, <c>IPV6</c>,
    /// <c>BASE16NUM</c>, <c>NUMBER</c>) <c>NonBacktracking</c>'i desteklemiyor ve
    /// burada T08'in <c>\b</c> ikamesi <b>uygulanamaz</b>: sınırlar <c>.</c>
    /// karakterini de kapsıyor, <c>\b</c> ise onu geçiriyor — yani ikame daha
    /// geçirgen olurdu. Üstelik maskeler Python sidecar ile birebir aynı çıktıyı
    /// vermek zorunda (K14), dolayısıyla tek taraflı yeniden yazılamazlar.
    /// </para>
    ///
    /// <para>
    /// Geri izlemede kalmaları sorun değil: koruma artık
    /// <see cref="MaxInputLength"/>. Test bu ifadelerde iç içe niceleyici
    /// olmadığını sabitliyor — o gün gelirse uzunluk sınırı yetmez.
    /// </para>
    /// </summary>
    private static Regex Compile(string pattern, string path)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Lookaround / geri referans / atomik grup.
            return new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                Regex.InfiniteMatchTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{path}: '{pattern}' geçersiz düzenli ifade: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Her maskeyi bir kez koşturur. Amacı sonuç değil, <b>kurulum maliyetini
    /// yüklemeye kaydırmak</b>.
    ///
    /// <para>
    /// İki motor da işini tembel yapıyor: <c>NonBacktracking</c> otomatı ilk
    /// eşleşmede kuruyor, <c>Compiled</c> kodu ilk eşleşmede üretiyor. Ödenmezse
    /// bu maliyet ilk gerçek log satırına biner. Zaman aşımı kaldırıldığı için
    /// artık <b>sessiz bir hataya</b> dönüşmüyor — ama ilk satırın sebepsiz yere
    /// yavaş olması yine istenmez.
    /// </para>
    /// </summary>
    private void Warmup()
    {
        // Her maskenin kendi motorunu dokunduracak kadar çeşitli, kısa bir satır.
        Signature("ısınma 10.0.0.1 2001:db8::1 0xff 42 a@b.co /tmp/x "
            + "6f9619ff-8b86-d011-b42d-00cf4fc964ff 00:1b:44:11:3a:b7 http://h/p");
    }

    /// <summary>
    /// Geri izlemeli motorda derlenen maskelerin adları. Zaman aşımı olmadığı
    /// için bu artık bir maruziyet listesi değil, bir <b>envanter</b>: bu
    /// ifadelerde iç içe niceleyici çıkarsa <see cref="MaxInputLength"/> koruması
    /// yetmez ve karar yeniden verilmelidir.
    /// </summary>
    public IReadOnlyList<string> BacktrackingMasks =>
        [.. _masks.Where(static m => (m.Regex.Options & RegexOptions.NonBacktracking) == 0).Select(static m => m.Name)];

    /// <summary>
    /// Satırın maskelenmiş imzası — Drain3'ün <c>LogMasker.mask</c>'ı ile aynı
    /// işlem: maskeler sırayla uygulanır, her eşleşme
    /// <c>&lt;AD&gt;</c> ile değiştirilir.
    /// </summary>
    public string Signature(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > MaxInputLength)
        {
            // Dev bir satır maskelemeye değmez: şablon madenciliği zaten
            // tekrarlayan yapıyı arıyor ve 16 KB'lık tekil bir gövde tekrarlayan
            // bir şey değil. Satır kaybolmuyor — yalnızca `template_id` almıyor.
            Interlocked.Increment(ref _skippedTooLong);
            return string.Empty;
        }

        foreach (var mask in _masks)
        {
            text = mask.Regex.Replace(text, MaskPrefix + mask.Name + MaskSuffix);
        }

        return text;
    }

    /// <summary>Şablonda geçen maske adları — F4'te grok taslağının iskeleti.</summary>
    public IReadOnlyList<string> MaskNamesIn(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return [.. _masks
            .Where(m => template.Contains(MaskPrefix + m.Name + MaskSuffix, StringComparison.Ordinal))
            .Select(static m => m.Name)];
    }

    private sealed class MaskDocument
    {
        public int Version { get; set; }

        public string? MaskPrefix { get; set; }

        public string? MaskSuffix { get; set; }

        public List<MaskEntry>? Masks { get; set; }

        public List<GoldenEntry>? Golden { get; set; }
    }

    private sealed class MaskEntry
    {
        public string? Name { get; set; }

        public string? Regex { get; set; }

        public string? Note { get; set; }
    }

    private sealed class GoldenEntry
    {
        public string? Input { get; set; }

        public string? Masked { get; set; }
    }
}

/// <summary>Çapraz dil doğrulama örneği: girdi → beklenen imza.</summary>
public sealed record MaskSample(string Input, string Masked);
