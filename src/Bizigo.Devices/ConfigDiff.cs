namespace Bizigo.Devices;

/// <param name="Section">Config bölümü — fark raporunun "hangi bölüm" cevabı.</param>
/// <param name="Added">Bu bölümde beliren satır sayısı.</param>
/// <param name="Removed">Bu bölümden kaybolan satır sayısı.</param>
public sealed record SectionChange(string Section, int Added, int Removed);

/// <param name="Sections">Değişen bölümler, en çok değişenden başlayarak.</param>
public sealed record ConfigDiffResult(
    IReadOnlyList<SectionChange> Sections,
    int Added,
    int Removed)
{
    public static ConfigDiffResult Empty { get; } = new([], 0, 0);

    public bool HasChanges => Added + Removed > 0;

    /// <summary>
    /// İnsan okuyacak özet. <b>Değişen satırların İÇERİĞİ yok</b> — config
    /// satırları SNMP community'si, ön-paylaşımlı anahtar ya da özetlenmiş
    /// parola taşıyabiliyor ve bu metin <c>change_events.summary</c>'ye
    /// yazılıyor. Ne değiştiği bölüm adıyla söyleniyor, ne olduğu değil.
    /// </summary>
    public string Describe(string targetId)
    {
        if (!HasChanges)
        {
            return $"{targetId}: config değişmedi.";
        }

        var top = Sections.Take(3).Select(s => s.Section);
        var rest = Sections.Count > 3 ? $" (+{Sections.Count - 3} bölüm daha)" : string.Empty;

        return $"{targetId}: {Added} satır eklendi, {Removed} satır silindi — {string.Join(", ", top)}{rest}";
    }
}

/// <summary>
/// İki config anlık görüntüsü arasındaki <b>anlamlı</b> farkı çıkarır (T26).
///
/// <para>
/// <b>Neden LCS değil, bölüm başına çoklu-küme farkı:</b> klasik bir metin
/// farkı, yeri değişen bir satırı "silindi + eklendi" diye raporlar. Ağ config'i
/// <i>bildirimsel</i>: aynı bölümdeki iki ayarın sırası anlam taşımıyor ve
/// cihazlar onları kendi iç sıralarıyla basıyor — bir yeniden yazımdan sonra
/// sıra değişebiliyor. LCS ile her yeniden yazım yüzlerce sahte değişiklik
/// üretirdi, ki bu tam olarak ticket'ın kaçındığı hâl.
/// </para>
///
/// <para>
/// İkinci sebep maliyet: LCS iki tarafın çarpımı kadar iş yapıyor ve 50 bin
/// satırlık bir config'te bu çekim turunu kilitler. Buradaki iş girdi
/// uzunluğunda <b>doğrusal</b> — F1'in "doğrusal zaman garantisi al" dersinin
/// bu ticket'taki karşılığı.
/// </para>
///
/// <para>
/// Bedeli açık: bir satır bölüm İÇİNDE yer değiştirirse fark görünmüyor. Bu
/// kabul edilen bir kayıp — bildirimsel bir config'te o zaten bir değişiklik
/// değil. Satır <b>bölümler arası</b> taşınırsa iki bölümde birden görünüyor,
/// ki doğru olan da bu.
/// </para>
/// </summary>
public static class ConfigDiff
{
    public static ConfigDiffResult Compare(
        IReadOnlyList<ConfigLine> before,
        IReadOnlyList<ConfigLine> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var old = Index(before);
        var fresh = Index(after);

        var sections = new List<SectionChange>();
        var added = 0;
        var removed = 0;

        foreach (var section in old.Keys.Union(fresh.Keys, StringComparer.Ordinal))
        {
            var oldLines = old.GetValueOrDefault(section);
            var newLines = fresh.GetValueOrDefault(section);

            var sectionAdded = Surplus(newLines, oldLines);
            var sectionRemoved = Surplus(oldLines, newLines);

            if (sectionAdded + sectionRemoved == 0)
            {
                continue;
            }

            sections.Add(new SectionChange(section, sectionAdded, sectionRemoved));
            added += sectionAdded;
            removed += sectionRemoved;
        }

        return new ConfigDiffResult(
            // En çok değişen bölüm başta: özet ilk üçü yazıyor ve okuyan için
            // en ilgili olanlar onlar.
            [.. sections
                .OrderByDescending(s => s.Added + s.Removed)
                .ThenBy(s => s.Section, StringComparer.Ordinal)],
            added,
            removed);
    }

    /// <summary>
    /// <paramref name="left"/>'te olup <paramref name="right"/>'ta olmayan satır
    /// sayısı — tekrarları da sayarak.
    ///
    /// <para>
    /// Tekrar sayısı önemli: aynı ACL satırının iki kez geçmesi ile bir kez
    /// geçmesi farklı config'ler. Küme farkı bunu kaçırırdı.
    /// </para>
    /// </summary>
    private static int Surplus(
        Dictionary<string, int>? left,
        Dictionary<string, int>? right)
    {
        if (left is null)
        {
            return 0;
        }

        var total = 0;

        foreach (var (line, count) in left)
        {
            var other = right?.GetValueOrDefault(line) ?? 0;

            if (count > other)
            {
                total += count - other;
            }
        }

        return total;
    }

    private static Dictionary<string, Dictionary<string, int>> Index(IReadOnlyList<ConfigLine> lines)
    {
        var index = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!index.TryGetValue(line.Section, out var counts))
            {
                counts = new Dictionary<string, int>(StringComparer.Ordinal);
                index[line.Section] = counts;
            }

            counts[line.Text] = counts.GetValueOrDefault(line.Text) + 1;
        }

        return index;
    }

    /// <summary>
    /// Normalize satırların kalıcı hâli. Anlık görüntü olarak <b>bu</b>
    /// saklanıyor, cihazın ham çıktısı değil: ham config bu ürünün tutmadığı
    /// bir yedek olurdu ve gizli değerleri de beraberinde getirirdi.
    /// </summary>
    public static string Serialize(IReadOnlyList<ConfigLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return string.Join('\n', lines.Select(l => $"{l.Section}\t{l.Text}"));
    }

    public static IReadOnlyList<ConfigLine> Deserialize(string? snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
        {
            return [];
        }

        var lines = new List<ConfigLine>();

        foreach (var row in snapshot.Split('\n'))
        {
            var tab = row.IndexOf('\t', StringComparison.Ordinal);

            if (tab > 0)
            {
                lines.Add(new ConfigLine(row[..tab], row[(tab + 1)..]));
            }
        }

        return lines;
    }
}
