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
/// <item>Cümle bölme <b>noktalama tabanlı</b> ve iki koruması var (T47'de
/// ölçüldü): kapalı bir <b>kısaltma listesi</b> ve satır başındaki
/// <c>\d+\.</c>. Ondalık sayı hiç sorun değildi — noktadan sonra boşluk
/// yok — ve o iddia ölçülüp <b>yanlışlandı</b>. Listede olmayan bir kısaltma
/// hâlâ cümleyi ikiye ayırıyor; bedeli iki parçanın da aynı atıfı taşıması
/// hâlinde yok, taşımıyorsa atıfsız parça <b>atılmış</b> sayılıyor.</item>
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
    /// <b>Kısaltma listesi — elle yazılmış ve KAPALI</b> (T47).
    ///
    /// <para>
    /// Türetmeye çalışmak bu üründe başka yerlerde kaybedilmiş bir bahis: tam
    /// olması gereken bir liste, tam olmadığı gün bekçiyi körleştiriyor. Elle
    /// yazılmış bir liste ise <b>eksik olduğunu itiraf ediyor</b> ve eksikliği
    /// ölçülebilir: listede olmayan bir kısaltma cümleyi ikiye ayırır, ve o
    /// ayrılma <see cref="SentenceBinding.Dropped"/> içinde <b>görünür</b>.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="RegexOptions.CultureInvariant"/> zorunlu</b>, süs değil:
    /// <see cref="RegexOptions.IgnoreCase"/> tek başına <b>o anki kültürle</b>
    /// katlama yapıyor ve <c>tr-TR</c>'de <c>I</c>/<c>ı</c> eşlemesi
    /// bambaşka — bu depoda aynı tuzak <c>.editorconfig</c>'de altı analizör
    /// kuralıyla hata seviyesine çekilmiş durumda.
    /// </para>
    ///
    /// <para>
    /// Listenin kaynağı ölçüm: <c>vb.</c> · <c>örn.</c> · <c>bkz.</c> üçü
    /// T47'de <b>ölçülerek</b> bulundu (cümleyi ikiye ayırdıkları görüldü);
    /// gerisi aynı sınıfın gündelik üyeleri. <b>Genişletmek bir satırlık iş</b>
    /// ve bilinçli olarak öyle: bir sonraki kişi yeni bir kısaltma ölçtüğünde
    /// buraya yazacak, bir sezgi kurmaya çalışmayacak.
    /// </para>
    /// </summary>
    private const string Abbreviations =
        @"vb|vs|örn|ör|bkz|age|sy|yy|çev|haz|Dr|Doç|Prof|Sn|Nu|no|etc|e\.g|i\.e|Fig|vol";

    /// <summary>
    /// Cümle sonu: <c>.</c> <c>!</c> <c>?</c> ve ardından boşluk. Satır sonu da
    /// bir sınır — modeller madde işaretli liste üretiyor.
    ///
    /// <h3>İki koruma, ve ikisi ÖLÇÜLEREK eklendi (T47)</h3>
    ///
    /// <para>
    /// İlk hâli <c>(?&lt;=[.!?])\s+|\r?\n+</c> idi. T47 §5 onu *"kısaltma ve
    /// ondalık yanlış bölünebiliyor"* diye şüpheli işaretlemişti; ölçüm
    /// iddianın <b>yarısını çürüttü</b> ve anılmayan bir hâl buldu:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>Ondalık zaten güvenliydi</b> — <c>3.14</c>'te noktadan sonra
    /// boşluk yok, yani kural onu hiç bölmüyordu. Aynı sebeple IP adresi,
    /// sürüm numarası ve alan adı da güvenli. Bu satır <b>bir koruma
    /// eklemedi</b>, çünkü eklenecek bir şey yoktu.</item>
    /// <item><b>Kısaltma gerçekten bölüyordu</b> → <see cref="Abbreviations"/>
    /// lookbehind'ı.</item>
    /// <item><b>Numaralı liste de bölüyordu ve ticket bunu SAYMIYORDU</b> —
    /// <c>1. Kök neden …</c>. Modeller gerekçeyi numaralı liste hâlinde
    /// yazıyor, yani kural <b>en sık karşılaştığı biçimi</b> bölüyordu →
    /// satır başındaki <c>\d+\.</c> lookbehind'ı.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Neden iki ayrı kural:</b> *"noktadan sonra küçük harf geliyorsa
    /// bölme"* sezgisi ikisini birden kapatmıyor — <c>1. Kök</c> büyük harfle
    /// devam ediyor. Tek sezgiyle çözmeye çalışmak, ölçülen iki hâlden birini
    /// sessizce açık bırakırdı.
    /// </para>
    ///
    /// <h3>Düzeltmenin ZAMANI da bir karar</h3>
    ///
    /// <para>
    /// Yanlış bölme <b>ücretsiz değildi</b>: atıf parçalardan yalnızca birinde
    /// kalıyorsa diğer parça atıfsız sayılıyor ve <i>atılan cümle oranının</i>
    /// <b>payına</b> yazılıyor — yani metrik, model kötü yazmadığı hâlde
    /// *"kötü yazdı"* diyor. Ölçü hâlâ tek bir bağlayıcı sayı üretmediği için
    /// (T47 açık, canlı model koşumu yapılmadı) bu bir <b>tanım değişikliği
    /// değil</b>, tanımın ilk kullanımdan önce doğru kurulması. Yarın aynı
    /// değişiklik korunacak bir geçmişi bozardı.
    /// </para>
    ///
    /// <h3>Bu kuralın TUTAMADIĞI</h3>
    ///
    /// <list type="bullet">
    /// <item><b>Listede olmayan kısaltma</b> hâlâ bölüyor. Liste kapalı ve
    /// bunu itiraf ediyor.</item>
    /// <item><b>Ondalık ayırıcısı boşluklu yazılırsa</b> (<c>3. 14</c>) bölünür
    /// — ölçülmedi, ve gerçek bir metinde beklenmiyor.</item>
    /// <item><b>Türkçe dışı diller ölçülmedi.</b> İngilizce kısaltmalar listede
    /// var ama davranışları sınanmadı.</item>
    /// </list>
    /// </summary>
    [GeneratedRegex(
        @"(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",
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
