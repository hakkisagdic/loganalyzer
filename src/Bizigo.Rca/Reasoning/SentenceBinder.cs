using System.Text;
using System.Text.RegularExpressions;

namespace Bizigo.Rca.Reasoning;

/// <param name="Text">Cümlenin kendisi.</param>
/// <param name="Bound">Bir kanıta bağlandı mı.</param>
/// <param name="CitedIds">Çözülen atıflar; bağlanmayan cümlede boş.</param>
/// <param name="UnresolvedCitations">
/// Köşeli parantez içinde geçen ama <b>görülen kanıta çözülmeyen</b> şeyler.
///
/// <para>
/// Sayaç için gerekmiyor — atıfsız cümle de çözülmeyen atıflı cümle de atılıyor.
/// Ayrı tutulmasının sebebi T47: <i>"model hiç atıf yapmıyor"</i> ile
/// <i>"model atıf uyduruyor"</i> iki farklı kalite sorunu ve iki farklı karar
/// gerektiriyor (prompt mu yetersiz, model mi uygun değil).
/// </para>
/// </param>
public sealed record BoundSentence(
    string Text,
    bool Bound,
    IReadOnlyList<string> CitedIds,
    IReadOnlyList<string> UnresolvedCitations);

/// <summary>
/// Bir serbest metin alanının bağlanmış hâli.
/// </summary>
/// <param name="Text">Rapora giren metin — <b>yalnızca bağlanan cümleler</b>.</param>
/// <param name="Sentences">Üretilen cümlelerin tamamı, kararlarıyla.</param>
public sealed record SentenceBinding(string Text, IReadOnlyList<BoundSentence> Sentences)
{
    /// <summary>Karar 1'in <b>payı</b>.</summary>
    public int Dropped => Sentences.Count(s => !s.Bound);

    /// <summary>Karar 1'in <b>paydası</b> — <i>"12 cümle üretti"</i> kısmı.</summary>
    public int Produced => Sentences.Count;

    public int Kept => Produced - Dropped;
}

/// <summary>
/// <b>İkinci kapı</b> — F4'ün Karar 1'i (RCA §2).
///
/// <para>
/// <i>"Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
/// gösteriliyor."</i> İçerik atılıyor, <b>sayı kalıyor</b>. Gerekçesi kayıtlı ve
/// iki yönlü: yalnızca göstermek bir rozeti okumayanı ikna ederdi; yalnızca
/// atmak kaliteyi ölçülemez yapardı — <i>"ölçemedim"</i> ile <i>"sorun yok"</i>un
/// aynı çıktıya inmesi.
/// </para>
///
/// <para>
/// <b>Atmak ile saymak ayrı iddialar</b> ve bu tip ikisini ayrı taşıyor:
/// <see cref="SentenceBinding.Text"/> atmanın sonucu,
/// <see cref="SentenceBinding.Sentences"/> saymanın kaynağı. Tek bir dizgi
/// döndürseydik sayı türetilemezdi ve Karar 1'in yarısı sessizce düşerdi.
/// </para>
///
/// <h3>Bağlanma ölçütü ve neden atıf sözdizimi TAHMİN EDİLMİYOR</h3>
///
/// <para>
/// Bir cümle, <b>bu adımın gördüğü</b> bir kimliği metninde geçiriyorsa
/// bağlanmış sayılıyor. Ölçüt kimliğin <i>şeklini</i> değil <b>kümesini</b>
/// kullanıyor: <c>EV-\d+</c> gibi bir desen yazmak, kanıt kimliğinin biçimini
/// varsaymak olurdu ve <c>EvidenceItem.Id</c> için öyle bir söz hiçbir yerde
/// verilmedi — sağlayıcı ne üretirse o. Kümeyle eşleştirmek bu varsayımı
/// tümden kaldırıyor.
/// </para>
///
/// <para>
/// <b>Kapının TANIYAMADIĞI</b> — T41'in kapısıyla aynı dürüstlük borcu:
/// </para>
/// <list type="bullet">
/// <item>Doğru kimliğe atıf yapan ama <b>o kimlikle ilgisiz</b> bir cümle
/// bağlanmış sayılıyor. Bu kapı atfın <i>varlığını</i> ölçüyor,
/// <i>yerindeliğini</i> değil; yerindelik altın kümenin işi (T47).</item>
/// <item>Cümle bölme noktalama tabanlıdır; ölçülen iki koruması vardır: kapalı
/// bir kısaltma listesi ve satır başındaki <c>\d+\.</c>. Ondalık sayı noktadan
/// sonra boşluk taşımadığı için zaten güvenlidir. Listede olmayan kısaltmalar
/// hâlâ bölünebilir ve bu durum <see cref="SentenceBinding.Dropped"/> içinde
/// görünür.</item>
/// <item>Atıfsız bir cümlenin <b>bir önceki cümleden</b> bağlamı devralması
/// tanınmıyor: her cümle kendi atfını taşımak zorunda. Bilerek — devralma
/// kabul edilseydi tek atıflı bir paragrafın tamamı bağlanmış sayılırdı ve
/// kapı ilk cümleden sonra hiçbir şey ölçmezdi.</item>
/// </list>
/// </summary>
public static partial class SentenceBinder
{
    /// <summary>Köşeli parantezli atıf adayı — <b>çözülmeyenleri</b> saymak için.</summary>
    [GeneratedRegex(@"\[(?<ref>[^\]\[]{1,120})\]", RegexOptions.ExplicitCapture)]
    private static partial Regex BracketCitation();

    /// <summary>
    /// Elle yazılmış ve bilerek kapalı kısaltma listesi. Listeyi türetmek,
    /// eksik olduğu gün bekçiyi sessizce körleştirirdi; eksik üyeler bugün
    /// ölçülebilir biçimde bölünmeye devam eder.
    /// </summary>
    private const string PrefixAbbreviations = @"Dr|Doç|Prof|Sn";

    private const string ContinuationAbbreviations =
        @"vb|vs|örn|ör|bkz|age|sy|yy|çev|haz|Nu|no|etc|e\.g|i\.e|Fig|vol";

    private const string Abbreviations =
        ContinuationAbbreviations + "|" + PrefixAbbreviations;

    /// <summary>
    /// Cümle sonu: <c>.</c> <c>!</c> <c>?</c> ve ardından boşluk. Satır sonu
    /// ayrıca sınırdır. Devam kısaltması (<c>vb.</c>, <c>vs.</c>) küçük harfle
    /// sürüyorsa korunur; büyük harfle yeni bir cümle başlıyorsa sınır geri
    /// açılır. Unvanlar (<c>Dr.</c>, <c>Prof.</c>) özel addan önce büyük harfle
    /// sürebildiği için ayrı sınıftır ve korunur. Bu ayrım yapılmazsa atıfsız
    /// ilk cümle sonraki cümlenin atfını devralır.
    ///
    /// Satır başı numarası koruması bunlardan bağımsızdır.
    /// </summary>
    [GeneratedRegex(
        @"(?:(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)|" +
        @"(?<=\b(?:" + ContinuationAbbreviations + @")\.)(?=\s+(?-i:\p{Lu})))" +
        @"(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    /// <param name="text">Modelin ürettiği serbest metin.</param>
    /// <param name="visibleIds">
    /// <b>Adımın gördüğü</b> kimlikler. Paketin tamamı değil — aksi hâlde bir
    /// cümle hiç görülmemiş bir kanıta atıf yapıp rapora girerdi.
    /// </param>
    public static SentenceBinding Bind(string text, IReadOnlySet<string> visibleIds)
    {
        ArgumentNullException.ThrowIfNull(visibleIds);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new SentenceBinding(string.Empty, []);
        }

        var sentences = new List<BoundSentence>();
        var kept = new StringBuilder();

        foreach (var raw in SentenceBreak().Split(text))
        {
            var sentence = raw.Trim();

            if (sentence.Length == 0)
            {
                continue;
            }

            var cited = visibleIds
                .Where(id => id.Length > 0 && sentence.Contains(id, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            var unresolved = BracketCitation()
                .Matches(sentence)
                .Select(m => m.Groups["ref"].Value.Trim())
                .Where(candidate => !visibleIds.Contains(candidate))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var bound = cited.Length > 0;

            sentences.Add(new BoundSentence(sentence, bound, cited, unresolved));

            if (!bound)
            {
                // ATILIYOR: rapora hiç girmiyor. Sayısı yukarıdaki listede kalıyor.
                continue;
            }

            if (kept.Length > 0)
            {
                kept.Append(' ');
            }

            kept.Append(sentence);
        }

        return new SentenceBinding(kept.ToString(), sentences);
    }
}
