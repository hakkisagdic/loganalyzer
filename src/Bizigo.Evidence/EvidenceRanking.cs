namespace Bizigo.Evidence;

/// <summary>
/// Kanıt satırlarını <b>tek bir sıraya</b> koyan karar (T36).
///
/// <para>
/// <b>Neden bir karar gerekiyor:</b> <see cref="EvidenceItem.Weight"/> sağlayıcı
/// içinde anlamlı, sağlayıcılar arasında <b>değil</b>. İlk-görülen imzada ağırlık
/// bir kaynak sayısı (3), hacim sapmasında bir z-score (8,4), ortak öznitelikte
/// bir kat (2,1), yayılmada 1/(1+saniye) (0,004). Bu dört sayıyı doğrudan
/// karşılaştırmak, birimleri farklı olduğu için sıralamayı <b>ölçek kazasına</b>
/// bırakmak demek: z-score her zaman kaynak sayısını yener, yayılma hiçbir zaman
/// üste çıkamaz. Ve bu yanlışlık hiçbir yerde hata vermez — rapor üretilir,
/// bulgular sıralanır, sıra sessizce yanlış olur.
/// </para>
///
/// <para>
/// <b>Seçilen yol iki katmanlı:</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>Sinyal sınıfı</b> (<see cref="ClassRank"/>) — hangi sinyal cinsinin kök
/// nedene daha yakın olduğu. Bu bir <b>yargı</b>, ölçüm değil; aşağıda tek tek
/// gerekçelendirildi ve doğrulanacağı yer altın küme (RCA §7).
/// </item>
/// <item>
/// <b>Sınıf içi büyüklük</b> — sağlayıcının kendi ağırlığı, o dilimdeki en büyük
/// değere bölünerek <c>[0,1]</c>'e taşınıyor. Sıra (rank) yerine oran seçildi:
/// sıra "z=20 ile z=3,1" farkını siler, oysa o fark sağlayıcının söylemek
/// istediği şeyin ta kendisi.
/// </item>
/// </list>
///
/// <para>
/// Sonuç <c>ClassRank + (w / max_w)</c>. Sınıf farkı 1,0 adım olduğu için
/// <b>sınıf içi hiçbir büyüklük bir üst sınıfı geçemiyor</b> — yani kararı
/// veren yargı, ölçek değil. İstenen tam olarak bu: en güçlü hacim sapması bile
/// bir değişiklik kaydının üstüne çıkmıyor, çünkü "ne patladı" ile "ne yapıldı"
/// aynı soruya cevap vermiyor.
/// </para>
///
/// <para>
/// <b>Bu sıralama rapordaki hipotez sırası değil.</b> Hipotez üretmek F4'ün işi
/// (K22). Burada sıralanan şey <b>kanıt satırları</b>: raporun hangi bulguyu
/// önce göstereceği ve LLM kapalıyken okuyanın önce neyi göreceği.
/// </para>
/// </summary>
public static class EvidenceRanking
{
    /// <summary>
    /// Sağlayıcı kimliği → sinyal sınıfı sırası. Büyük olan öne çıkıyor.
    ///
    /// <para>
    /// Anahtarlar <see cref="IEvidenceProvider.Id"/> ile aynı ve <b>şema kadar
    /// kalıcı</b> (T34): kanıt paketi saklandığı için bir kimliğin değişmesi
    /// geçmiş paketlerin sıralamasını değiştirir.
    /// </para>
    ///
    /// <para>
    /// Sıranın gerekçeleri — hepsi "kök nedene yakınlık" ekseninde:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>6 · <c>change.feed</c></b> — kayıtlı bir değişiklik, elimizdeki tek
    /// <b>niyet</b> kaydı. Diğer beşi olayın belirtisini ölçüyor; bu, birinin
    /// bilerek yaptığı bir şeyi gösteriyor ve aksiyona en yakın olan o (K21).
    /// </item>
    /// <item>
    /// <b>5 · <c>logs.first-seen</c></b> — "yeni bir şey oldu". RCA belgesi bunu
    /// log korelasyonları arasında tek en güçlü sinyal diye tanımlıyor: pencerede
    /// beliren ve tabanda hiç görülmemiş bir imza, tanımı gereği olayla eş
    /// zamanlı doğmuş bir olgu.
    /// </item>
    /// <item>
    /// <b>4 · <c>logs.propagation</c></b> — ilk bozulan çoğu zaman kök nedene en
    /// yakın olan. Nedensel bir yön veriyor; diğerleri yalnızca birliktelik.
    /// </item>
    /// <item>
    /// <b>3 · <c>logs.silence</c></b> — susan cihaz. Ağ tarafında kritik ama
    /// <b>ne bozulduğunu</b> söylüyor, <b>neden</b> bozulduğunu değil.
    /// </item>
    /// <item>
    /// <b>2 · <c>logs.volume</c></b> — var olan bir hatanın patlaması. Güçlü ama
    /// çoğunlukla belirti: hacim artışının kendisi nadiren nedendir.
    /// </item>
    /// <item>
    /// <b>1 · <c>logs.attribute-lift</c></b> — "hepsi aynı switch'in arkasında".
    /// Tek başına bir bulgu değil, diğer bulguları <b>gruplayan</b> bir içgörü;
    /// değeri destekleyici olmasında.
    /// </item>
    /// <item>
    /// <b>0 · <c>logs.window</c></b> — pencerenin ham bozuk satırları. Bağlam,
    /// bulgu değil; zaman çizelgesini ve ham loga inişi besliyor.
    /// </item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, int> ClassRanks = new(StringComparer.Ordinal)
    {
        ["change.feed"] = 6,
        ["logs.first-seen"] = 5,
        ["logs.propagation"] = 4,
        ["logs.silence"] = 3,
        ["logs.volume"] = 2,
        ["logs.attribute-lift"] = 1,
        ["logs.window"] = 0,
    };

    /// <summary>
    /// Sıralaması yazılı olan sağlayıcılar. <b>Bir bekçi testi, kayıtlı her
    /// sağlayıcının bu listede olduğunu tutuyor.</b>
    ///
    /// <para>
    /// Sebep: listede olmayan bir sağlayıcı <see cref="UnrankedClass"/>'a düşer
    /// ve kanıtı raporun en dibinde, sessizce görünür. F5'te trace sağlayıcısı
    /// geldiğinde onu buraya yazmayı unutmak, kanıtını "en önemsiz" ilan etmek
    /// olurdu — ve hiçbir şey kırmızı yanmazdı.
    /// </para>
    /// </summary>
    public static IReadOnlyCollection<string> RankedProviders => ClassRanks.Keys;

    /// <summary>
    /// Tanınmayan sağlayıcının sınıfı. <b>Yok saymak yerine en dibe</b>: rapordan
    /// düşürmek, bilinmeyen bir kanıt türünü sessizce silmek olurdu.
    /// </summary>
    public const int UnrankedClass = -1;

    public static int ClassRank(string providerId) =>
        ClassRanks.TryGetValue(providerId, out var rank) ? rank : UnrankedClass;

    /// <summary>
    /// Bir dilimin satırlarını sınıf içi orana çevirip sıralanabilir tek bir
    /// skora taşır.
    ///
    /// <para>
    /// Bölen dilimdeki <b>en büyük</b> ağırlık. Negatif ya da sıfır ağırlıklar
    /// (bir sağlayıcı ağırlık üretmiyorsa hepsi 0 olur) tek bir orana, 0'a
    /// düşüyor — o zaman sıralamayı sınıf belirliyor ve dilim içi sıra
    /// sağlayıcının verdiği sırayı koruyor. Yayılma sırası tam olarak bunu
    /// gerektiriyor: satırların sırası zaten sinyalin kendisi.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RankedEvidence> Rank(EvidenceSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Items.Count == 0)
        {
            return [];
        }

        var classRank = ClassRank(slice.ProviderId);
        var max = slice.Items.Max(item => item.Weight);

        return
        [
            .. slice.Items.Select((item, index) => new RankedEvidence(
                item,
                classRank,
                max > 0 ? item.Weight / max : 0,
                index))
        ];
    }

    /// <summary>
    /// Bütün dilimlerin satırlarını tek sıraya dizer.
    ///
    /// <para>
    /// Eşitlik bozucular <b>kararlı olmak zorunda</b>: kanıt paketi aynı girdiyle
    /// aynı çıktıyı vermek zorunda (T36 kabul kriteri) ve iki satır aynı skoru
    /// aldığında sıralamanın makineye göre değişmesi paketi deterministik
    /// olmaktan çıkarırdı. Sıra: skor ↓, sonra sağlayıcı içi orijinal sıra ↑,
    /// sonra kimlik ↑.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RankedEvidence> RankAll(IEnumerable<EvidenceSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);

        return
        [
            .. slices
                .SelectMany(Rank)
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.OrderInSlice)
                .ThenBy(r => r.Item.Id, StringComparer.Ordinal)
        ];
    }
}

/// <summary>
/// Sıralanmış tek bir kanıt satırı — <b>skor türetilmiş, saklanmıyor</b>.
///
/// <para>
/// Bilerek: sıralama yargısı zamanla değişebilir, kanıtın kendisi değişmez.
/// Skoru pakete yazmak, altı ay sonra sıralamayı düzelttiğimizde geçmiş
/// paketleri eski yargıyla dondururdu — oysa paketin saklanma sebebi tam tersi,
/// aynı kanıt üzerinde farklı yorumları karşılaştırabilmek (F4).
/// </para>
/// </summary>
/// <param name="ClassRank">Sinyal sınıfı sırası — tam sayı adım.</param>
/// <param name="RelativeWeight">Dilim içi <c>[0,1]</c> oran.</param>
/// <param name="OrderInSlice">Sağlayıcının verdiği orijinal sıra; kararlı eşitlik bozucu.</param>
public sealed record RankedEvidence(
    EvidenceItem Item,
    int ClassRank,
    double RelativeWeight,
    int OrderInSlice)
{
    public double Score => ClassRank + RelativeWeight;
}
