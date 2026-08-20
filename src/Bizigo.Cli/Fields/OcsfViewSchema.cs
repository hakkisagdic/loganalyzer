using System.Text.RegularExpressions;

namespace Bizigo.Cli.Fields;

/// <param name="Source"><c>events</c> tablosundaki kaynak kolon.</param>
/// <param name="Alias">Görünümdeki OCSF adı — Sigma kurallarının vurduğu ad.</param>
public sealed record OcsfViewColumn(string Source, string Alias);

/// <summary>
/// <c>events_ocsf</c> görünümünün kolon listesini <b>göç dosyasından</b> okur.
///
/// <para>
/// <b>Neden elle yazılmadı:</b> alan kapsamı ölçümü "hangi OCSF alanı boş"
/// sorusunu cevaplıyor ve elle yazılmış bir liste, görünüme kolon eklendiğinde
/// onu <b>hiç sormaz</b>. Sonuç, eksik bir tablonun tam görünmesi — bu depoda
/// <c>Produces</c> kapısının 16 ucu görmediği olayın aynısı. Liste kaynaktan
/// okununca sürüklenmesi imkânsız.
/// </para>
///
/// <para>
/// Ayrıştırıcı bilinçli olarak dar: yalnızca <c>SELECT … FROM events</c>
/// arasındaki basit <c>kolon AS ad</c> çiftlerini tanıyor. Görünüm bir gün
/// ifade taşırsa (ör. <c>toUInt32(x) AS y</c>) burası kaynağı çözemez ve
/// <b>hata verir</b> — sessizce atlamaz.
/// </para>
/// </summary>
public static class OcsfViewSchema
{
    private static readonly Regex Definition = new(
        @"CREATE\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?events_ocsf\s+AS\s+SELECT(?<body>.*?)FROM\s+events",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant
            | RegexOptions.ExplicitCapture);

    /// <summary>
    /// <paramref name="migrationsDirectory"/> altındaki <b>tek</b> tanımı bulur.
    ///
    /// <para>
    /// Birden fazla tanım bulursa hata veriyor: göçler sırayla uygulanıyor ve
    /// sonraki tanım öncekini eziyor, yani "hangisi geçerli" sorusunun cevabı
    /// dosya adına gizlenmiş olurdu. Bugün tek tanım var (<c>0003</c>); ikinci
    /// bir tanım eklendiği gün bu araç durup dikkat çekmeli.
    /// </para>
    /// </summary>
    public static IReadOnlyList<OcsfViewColumn> Read(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Göç dizini yok: {migrationsDirectory}");
        }

        var matches = new List<(string File, Match Match)>();

        foreach (var file in Directory
                     .EnumerateFiles(migrationsDirectory, "*.sql")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            foreach (Match match in Definition.Matches(File.ReadAllText(file)))
            {
                matches.Add((Path.GetFileName(file), match));
            }
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"{migrationsDirectory} altında `events_ocsf` görünümünün tanımı bulunamadı.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                "`events_ocsf` birden fazla göçte tanımlanmış (" +
                string.Join(", ", matches.Select(entry => entry.File)) +
                "). Hangisinin geçerli olduğu dosya adına gizlenmiş olurdu; " +
                "araç durdu.");
        }

        return Parse(matches[0].Match.Groups["body"].Value);
    }

    private static List<OcsfViewColumn> Parse(string body)
    {
        var columns = new List<OcsfViewColumn>();

        foreach (var raw in SplitTopLevel(StripComments(body)))
        {
            var entry = raw.Trim();

            if (entry.Length == 0)
            {
                continue;
            }

            var parts = entry.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            switch (parts.Length)
            {
                case 1:
                    // `owner_group` gibi takma adsız kolon.
                    columns.Add(new OcsfViewColumn(parts[0], parts[0]));
                    break;

                case 3 when parts[1].Equals("AS", StringComparison.OrdinalIgnoreCase):
                    columns.Add(new OcsfViewColumn(parts[0], parts[2].Trim('"')));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"`events_ocsf` tanımındaki `{entry}` çözülemedi. Bu araç yalnızca " +
                        "`kolon AS ad` biçimini tanıyor; görünüm ifade taşımaya başladıysa " +
                        "ayrıştırıcı genişletilmeli. Sessizce atlamak, o alanı hiç sormamak olurdu.");
            }
        }

        return columns;
    }

    private static string StripComments(string body) =>
        string.Join(
            '\n',
            body.Split('\n').Select(static line =>
            {
                var comment = line.IndexOf("--", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

    /// <summary>
    /// Virgülle böler ama parantez içine girmez — bugün gerekmiyor, yarın bir
    /// <c>toUInt32(a, b)</c> eklendiğinde listeyi ikiye bölmesin diye.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        yield return body[start..];
    }
}
