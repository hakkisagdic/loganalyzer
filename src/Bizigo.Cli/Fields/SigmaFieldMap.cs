using System.Text.RegularExpressions;

namespace Bizigo.Cli.Fields;

/// <summary>
/// Sigma taxonomy adı → <c>events_ocsf</c> kolon adı, <b>pipeline'ın kendi
/// dosyasından</b> okunuyor.
///
/// <para>
/// <b>Neden ikinci bir kopya yazılmadı:</b> bu sözlük
/// <c>prototypes/t30-sigma/bizigo_pipeline.py</c> içinde ve orası onun tek
/// gerçeği — derlenen SQL'in kolon adları oradan çıkıyor. C# tarafına elle
/// kopyalasaydık, iki tarafın aynı kuralı farklı kolona bağladığı gün
/// hiçbir yerde görünmezdi: erişilebilirlik ölçümü doğru görünen ama yanlış
/// kolona bakan bir cevap üretirdi.
/// </para>
///
/// <para>
/// Ayrıştırıcı <b>dar ve gürültülü</b>: yalnızca <c>"a": "b",</c> satırlarını
/// tanıyor ve sözlüğü bulamazsa istisna atıyor. Sessizce boş dönmek, bütün
/// kuralların "alan bir kolona bağlı değil" diye raporlanmasına yol açardı — ve
/// o tablo "hiçbir şey ölçülemedi" değil "hiçbir sorun yok" diye okunurdu.
/// </para>
/// </summary>
public static class SigmaFieldMap
{
    private static readonly Regex Block = new(
        @"FIELD_MAP:\s*dict\[str,\s*str\]\s*=\s*\{(?<body>.*?)^\}",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant
            | RegexOptions.ExplicitCapture);

    private static readonly Regex Entry = new(
        @"^\s*""(?<from>[^""]+)""\s*:\s*""(?<to>[^""]+)""\s*,",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static IReadOnlyDictionary<string, string> Read(string pipelinePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelinePath);

        if (!File.Exists(pipelinePath))
        {
            throw new FileNotFoundException(
                $"Sigma pipeline dosyası bulunamadı: {pipelinePath}. Alan adı çevirisi onsuz " +
                "yapılamıyor ve çevirisiz her kural 'kolona bağlı değil' görünürdü.",
                pipelinePath);
        }

        var match = Block.Match(File.ReadAllText(pipelinePath));

        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"{pipelinePath} içinde `FIELD_MAP` sözlüğü bulunamadı. Sözlüğün biçimi " +
                "değiştiyse bu ayrıştırıcı genişletilmeli; boş dönmek ölçümü sessizce " +
                "anlamsız yapardı.");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match entry in Entry.Matches(match.Groups["body"].Value))
        {
            map[entry.Groups["from"].Value] = entry.Groups["to"].Value;
        }

        return map.Count > 0
            ? map
            : throw new InvalidOperationException($"{pipelinePath} içindeki `FIELD_MAP` boş.");
    }
}
