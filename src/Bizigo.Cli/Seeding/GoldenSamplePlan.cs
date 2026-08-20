using System.Globalization;

namespace Bizigo.Cli.Seeding;

/// <param name="Anchor">Yayılımın sağ ucu — pratikte "şimdi".</param>
/// <param name="Span">Geriye doğru yayılım süresi.</param>
/// <param name="TotalEvents">Hedeflenen toplam olay sayısı (yuvarlamayla sapar).</param>
/// <param name="ZipfExponent">Sıklık yasasının üssü; büyüdükçe kuyruk seyrekleşir.</param>
/// <param name="Seed">Deterministik üretim için tohum.</param>
public sealed record SeedPlanOptions(
    DateTimeOffset Anchor,
    TimeSpan Span,
    int TotalEvents,
    double ZipfExponent,
    int Seed);

/// <param name="GroupIndex">İmza grubunun sırası — yalnızca teşhis için.</param>
/// <param name="LineIndex">Örnek satırın dizini.</param>
/// <param name="At">Olayın ekileceği an (tam saniye).</param>
public readonly record struct PlannedOccurrence(int GroupIndex, int LineIndex, DateTimeOffset At);

/// <summary>
/// Örnek satırların zamana yayılımı.
///
/// <para>
/// <b>Bu seçim ölçülen sayıyı doğrudan belirliyor</b>, o yüzden burada yazılı.
/// Baseline ölçümü (T35) "penceredeki imzaların yüzde kaçı tabanda hiç
/// görülmemiş" diye soruyor ve cevabın taban uzunluğuyla nasıl düştüğüne
/// bakıyor. O düşüş tamamen <b>hangi imzanın ne sıklıkla geçtiğinin</b>
/// sonucudur: bütün imzalar sık geçiyorsa eğri ilk saatte dibe vurur, hepsi
/// seyrekse hiç düşmez. İkisi de ölçülecek şeyin değil fixture'ın özelliğidir.
/// </para>
///
/// <h3>Üç karar</h3>
///
/// <para>
/// <b>1 · Sıklık yasası: Zipf, sıra üzerinden.</b> Her <b>imzaya</b> (satıra
/// değil) bir sıra veriliyor ve oran <c>1/sıra^s</c>. Gerekçe: log şablonlarının
/// gerçek dağılımı ağır kuyruklu — birkaç şablon hacmin çoğunu taşır, çoğu
/// şablon nadirdir. Eşit dağıtsaydık her imza her saatte görünürdü ve
/// "ilk-görülen" oranı <b>tanım gereği</b> sıfıra yakın çıkardı; o sıfır
/// tabanın uzunluğu hakkında hiçbir şey söylemezdi. Ölçümün elde ettiği dirsek
/// yaklaşık <c>1/λ_min</c> civarında oluşur, yani <b>kuyruğun seyrekliği kadar
/// uzaktadır</b>. Bu, seçilen <c>s</c>'in ve süre/hacim oranının sonucudur;
/// üretim verisinde farklı çıkar ve karar sayı bağlayıcı sayılmadan önce gerçek
/// veriyle tekrarlanmalıdır.
/// </para>
///
/// <para>
/// <b>2 · Sıra, imza üzerinden — satır üzerinden değil.</b> Maskeleme birden çok
/// örnek satırı aynı imzaya indiriyor (K35). Sırayı satıra verseydik aynı
/// imzanın parçaları farklı sıralara düşer ve imzanın gerçek oranı sıraların
/// toplamı olurdu: seçtiğimiz yasa ile ölçülen dağılım ayrışırdı, üstelik
/// sessizce.
/// </para>
///
/// <para>
/// <b>2b · Sıra vendor'lar arasında sırayla dağıtılıyor.</b> Tek bir küresel
/// karışım denendi ve <b>ölçüldü</b>: tohum 39 ile FortiGate satırların
/// %69'unu, MikroTik %1,7'sini alıyordu — bir vendor'ın <i>en nadir</i> şablonu
/// başka bir vendor'ın <i>tüm trafiğinden</i> sık. Hiçbir kurulum böyle
/// görünmüyor ve dahası, Sigma ölçümü vendor başına saydığı için bu çarpıklık
/// doğrudan onun paydasına giriyor. Bunun yerine her vendor kendi içinde
/// karıştırılıyor ve küresel sıra vendor'lar arasında sırayla dağıtılıyor: 1.
/// sıra A'nın en gürültülü şablonu, 2. sıra B'nin, … Ağır kuyruk her vendor'ın
/// <b>kendi içinde</b> korunuyor, vendor'lar arasında değil.
/// </para>
///
/// <para>
/// <b>3 · Varış zamanları düzgün (uniform) — günlük ritim <b>yok</b>.</b> İlk
/// düşünülen şey iş saatlerine ağırlık veren gerçekçi bir ritimdi ve bilinçle
/// bırakıldı: ritim, ölçümün 45 dakikalık olay penceresinin yoğunluğunu
/// <b>yükleyicinin koşturulduğu saate</b> bağlar. Gece koşulan bir yükleme
/// oranın paydasını küçültür ve ölçülen eğri, taban uzunluğuyla hiç ilgisi
/// olmayan bir sebeple değişir. Ritim ikinci kesitin işi ve son pencereye dair
/// bir garantiyle birlikte gelmeli.
/// </para>
///
/// <para>
/// <b>Her satır en az bir kez</b> yazılıyor: Sigma ölçümünün ön kontrolü altın
/// örnek dosyalarının <b>en uzun satırından</b> türetilmiş bir sonda arıyor ve
/// o satır kuyruğa düşerse ölçüm hiç başlamaz.
/// </para>
/// </summary>
public static class GoldenSamplePlan
{
    /// <param name="signatures">
    /// Satır dizinine karşılık imza hash'i. Aynı hash'i taşıyan satırlar tek bir
    /// grup sayılıyor; <c>0</c> ("imza yok") satırları kendi başlarına grup olur,
    /// çünkü ölçüm açısından ayırt edilemezler.
    /// </param>
    /// <param name="vendors">
    /// Satır dizinine karşılık vendor anahtarı (parser dizini). Sıranın
    /// vendor'lar arasında sırayla dağıtılması için gerekiyor.
    /// </param>
    public static IReadOnlyList<PlannedOccurrence> Build(
        IReadOnlyList<ulong> signatures,
        IReadOnlyList<string> vendors,
        SeedPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        ArgumentNullException.ThrowIfNull(vendors);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TotalEvents);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.Span, TimeSpan.Zero);

        if (signatures.Count != vendors.Count)
        {
            throw new ArgumentException(
                "İmza ve vendor listeleri aynı uzunlukta olmalı.", nameof(vendors));
        }

        var groups = GroupBySignature(signatures);
        if (groups.Count == 0)
        {
            return [];
        }

        var random = new Random(options.Seed);
        var order = RankGroups(groups, vendors, random);

        var weights = new double[groups.Count];
        var total = 0.0;

        for (var rank = 0; rank < order.Length; rank++)
        {
            var weight = 1.0 / Math.Pow(rank + 1, options.ZipfExponent);
            weights[order[rank]] = weight;
            total += weight;
        }

        var start = options.Anchor - options.Span;
        var seconds = (long)options.Span.TotalSeconds;
        var occurrences = new List<PlannedOccurrence>(options.TotalEvents);

        for (var group = 0; group < groups.Count; group++)
        {
            var lines = groups[group];
            var target = (int)Math.Round(options.TotalEvents * weights[group] / total,
                MidpointRounding.AwayFromZero);

            // Grubun her satırı en az bir kez yazılmalı — yoksa Sigma sondası
            // kuyruktaki bir satıra denk geldiğinde ölçüm hiç başlamaz.
            var count = Math.Max(lines.Count, target);

            for (var i = 0; i < count; i++)
            {
                var at = start.AddSeconds(random.NextInt64(seconds + 1));
                occurrences.Add(new PlannedOccurrence(group, lines[i % lines.Count], Truncate(at)));
            }
        }

        // Zaman sırası: ClickHouse `(owner_group, source_id, ts)` ile sıralı ve
        // sıralı yazım daha az parça birleştirmesi demek. Doğruluk için gerekli
        // değil, ucuz olduğu için yapılıyor.
        occurrences.Sort(static (a, b) => a.At.CompareTo(b.At));
        return occurrences;
    }

    /// <summary>Aynı imzayı taşıyan satır dizinleri; ilk görülme sırasına göre.</summary>
    private static List<List<int>> GroupBySignature(IReadOnlyList<ulong> signatures)
    {
        var groups = new List<List<int>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var line = 0; line < signatures.Count; line++)
        {
            var hash = signatures[line];

            // İmzasız satırlar birleştirilmiyor: "imza yok" bir kimlik değil.
            var key = hash == 0
                ? string.Create(CultureInfo.InvariantCulture, $"none:{line}")
                : hash.ToString(CultureInfo.InvariantCulture);

            if (!index.TryGetValue(key, out var group))
            {
                group = groups.Count;
                index[key] = group;
                groups.Add([]);
            }

            groups[group].Add(line);
        }

        return groups;
    }

    /// <summary>
    /// Küresel sıra: her vendor kendi içinde karıştırılıp sıraya dizilir, sonra
    /// vendor'lar arasında sırayla okunur (A₁ B₁ C₁ D₁ A₂ B₂ …). Vendor sırası
    /// da tohumdan geliyor — alfabetik olsaydı FortiGate her koşumda 1. sırayı
    /// alırdı ve hacim üstünlüğü bir tercih değil bir tesadüf olurdu.
    /// </summary>
    /// <returns>Sıra → grup dizini.</returns>
    private static int[] RankGroups(List<List<int>> groups, IReadOnlyList<string> vendors, Random random)
    {
        var byVendor = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var group = 0; group < groups.Count; group++)
        {
            // Grubun vendor'ı ilk satırından geliyor. Aynı imzanın iki vendor'a
            // yayılması teorik olarak mümkün ama ölçüm açısından o zaten tek bir
            // şeydir; birini seçmek gerekiyor ve ilk satır deterministik.
            var vendor = vendors[groups[group][0]];

            if (!byVendor.TryGetValue(vendor, out var list))
            {
                list = [];
                byVendor[vendor] = list;
            }

            list.Add(group);
        }

        var vendorOrder = Shuffle(
            [.. byVendor.Keys.OrderBy(static key => key, StringComparer.Ordinal)],
            random);

        var queues = vendorOrder.Select(vendor => Shuffle([.. byVendor[vendor]], random)).ToArray();
        var order = new List<int>(groups.Count);

        for (var depth = 0; order.Count < groups.Count; depth++)
        {
            foreach (var queue in queues)
            {
                if (depth < queue.Length)
                {
                    order.Add(queue[depth]);
                }
            }
        }

        return [.. order];
    }

    private static T[] Shuffle<T>(T[] items, Random random)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    /// <summary>
    /// Tam saniyeye indiriyor. Syslog ve <c>dd/MMM/yyyy:HH:mm:ss</c> biçimleri
    /// saniyenin altını taşımıyor; ekilen an saniyenin altında bir şey içerseydi
    /// yeniden yazılan satır onu kaybeder ve doğrulama her satırda patlardı.
    /// </summary>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Offset);
}
