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
    /// Maskeler ayrıştırılamamış, yani <b>güvenilmeyen</b> metin üzerinde
    /// koşuyor. Grok tarafındaki kademeli zaman aşımıyla aynı gerekçe
    /// (F1 §4.1): tek bir kötü satır işçiyi kilitleyemez.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly MaskDefinition[] _masks;

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
            masks.Add(new MaskDefinition(
                entry.Name,
                entry.Regex,
                new Regex(entry.Regex, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout)));
        }

        var golden = (document.Golden ?? [])
            .Select(static g => new MaskSample(g.Input ?? string.Empty, g.Masked ?? string.Empty))
            .ToArray();

        return new MaskCatalog(
            document.Version,
            document.MaskPrefix ?? "<",
            document.MaskSuffix ?? ">",
            [.. masks],
            golden,
            path);
    }

    /// <summary>
    /// Satırın maskelenmiş imzası — Drain3'ün <c>LogMasker.mask</c>'ı ile aynı
    /// işlem: maskeler sırayla uygulanır, her eşleşme
    /// <c>&lt;AD&gt;</c> ile değiştirilir.
    /// </summary>
    public string Signature(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var mask in _masks)
        {
            try
            {
                text = mask.Regex.Replace(text, MaskPrefix + mask.Name + MaskSuffix);
            }
            catch (RegexMatchTimeoutException)
            {
                // İmza üretmek bir konfor; zaman aşımına uğrayan satır
                // etiketsiz kalır ve keşif kuyruğuna da girmez.
                return string.Empty;
            }
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
