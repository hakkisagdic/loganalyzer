using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

/// <summary>
/// <b>Ortak topoloji özniteliği</b> — bozulan cihazların paylaştığı üst düğüm,
/// segment ya da firmware (F5 · S1, K21'in <see cref="EvidenceKind.Topology"/>
/// türü).
///
/// <para>
/// <b>Bu sağlayıcının varlık sebebi ölçülmüş bir boşluk.</b> RCA §3.1 ortak
/// öznitelik sinyalini *"aynı VLAN, aynı upstream, aynı firmware … topoloji
/// olmadan topoloji sezgisi"* diye tarif ediyordu ve F5 kapsam kararı üç alanın
/// da ne olay tablosunda ne envanterde olduğunu ölçtü. Yani telafi bir niyetti,
/// bir yetenek değil — ve F5'i ertelemenin gerekçelerinden biri ona
/// dayanıyordu.
/// </para>
///
/// <para>
/// <b>Kapsam sınırı — türün adı "topoloji" ama grafiği yok.</b> Bu sağlayıcı
/// envanterdeki <b>düz öznitelikleri</b> okuyor; RCA §3'ün tarif ettiği
/// *"etkilenen cihazların ortak üst düğümü"*nü tek kademede cevaplıyor. İki
/// kademe yukarıdaki ortak ata (<c>A → B → C</c>) <b>hesaplanmıyor</b>: o bir
/// ilişki grafiği ve envanterde grafik yok. Tür bu yüzden "karşılanıyor" ama
/// sınırı yazılı; sınır <see cref="EvidenceSlice.Detail"/>'a da giriyor,
/// yalnızca bu yorumda kalmıyor.
/// </para>
///
/// <para>
/// <b>Sorgu yüzeyi paylaşılıyor, kopyalanmıyor</b> (§9). "Etkilenen cihaz"
/// tanımı <see cref="PropagationProvider"/>'ınkiyle <b>aynı</b> —
/// <c>GetPropagationAsync</c>. İkinci bir "bozulma" tanımı yazmak, aynı raporun
/// iki bölümünün farklı cihaz kümesinden bahsetmesi demek olurdu ve fark
/// hiçbir yerde görünmezdi. Envanter tarafı da öyle:
/// <c>SearchSourcesAsync</c> zaten kapsam kapısından geçen tek envanter yolu.
/// Yeni bir <c>IScopedQuery</c> metodu <b>eklenmedi</b>.
/// </para>
/// </summary>
public sealed class TopologyProvider(IScopedQuery query) : IEvidenceProvider
{
    /// <summary>
    /// "Bozulma" eşiği — <see cref="PropagationProvider"/> ile <b>aynı değer ve
    /// aynı gerekçe</b>. <b>Ölçülmemiş varsayılan.</b> İkisi ayrışırsa iki
    /// bölüm farklı cihaz kümesinden bahseder.
    /// </summary>
    public byte SeverityAtOrBelow { get; init; } = 3;

    /// <summary>
    /// Bir değerin "ortak" sayılması için en az kaç bozulan cihazda görülmesi
    /// gerektiği.
    ///
    /// <para>
    /// <b>Tanım gereği 2, ayarlanabilir bir eşik değil:</b> tek cihazın
    /// özniteliği paylaşılan bir şey değildir. Bu sayıyı büyütmek bir ayar
    /// kararı olurdu ve ölçülmeden yapılmıyor.
    /// </para>
    /// </summary>
    public int MinAffected { get; init; } = 2;

    public string Id => "topology.shared-attribute";

    public EvidenceKind Kind => EvidenceKind.Topology;

    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(budget);

        var correlation = new CorrelationWindow
        {
            From = window.From,
            To = window.To,
            BaselineFrom = window.BaselineFrom,
            BaselineTo = window.BaselineTo,
            OwnerGroups = window.OwnerGroups,
            SourceIds = window.SourceIds,
        };

        var onsets = await query.GetPropagationAsync(
            correlation, scope, SeverityAtOrBelow, budget.MaxItems + 1, cancellationToken);

        var truncated = onsets.Count > budget.MaxItems;
        var affected = truncated ? onsets.Take(budget.MaxItems).ToArray() : [.. onsets];

        var inventory = await query.SearchSourcesAsync(scope, cancellationToken);

        // **Payda pencereyle aynı daralmayı görmek zorunda.** `SearchSourcesAsync`
        // yalnızca `AccessScope`'u biliyor; `RcaWindow`'un grup/kaynak daraltmasını
        // bilmiyor. Uygulanmazsa lift, tek gruba daraltılmış bir olayı kurumun
        // TAMAMINA oranlar ve her değer olduğundan güçlü görünür — sayı yanlış
        // olmaz, ANLAMI yanlış olur ve raporda ikisi aynı görünür.
        var population = Narrow(inventory, window);

        var byId = population.ToDictionary(s => s.SourceId, StringComparer.Ordinal);

        // Envanterde topoloji verisi HİÇ yoksa bu bir bulgu değil, ölçümün
        // yokluğu (T34). "Ortak öznitelik yok" cümlesi, alanları kimsenin
        // doldurmadığı bir envanterde de aynen kurulurdu — ve okuyan onu bir
        // kanıt sanardı.
        if (!population.Any(HasAnyTopology))
        {
            return new EvidenceSlice
            {
                ProviderId = Id,
                Kind = Kind,
                Status = EvidenceStatus.NeverFed,
                Detail =
                    "Envanterde topoloji özniteliği (upstream/vlan/firmware) hiç doldurulmamış — " +
                    "bu türe bakılamadı. Bu, 'ortak öznitelik yok' DEĞİL.",
            };
        }

        if (affected.Length < MinAffected)
        {
            return new EvidenceSlice
            {
                ProviderId = Id,
                Kind = Kind,
                Status = EvidenceStatus.Empty,
                Truncated = truncated,
                Detail = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Pencerede bozulan cihaz sayısı {affected.Length}; ortak öznitelikten " +
                    $"söz edebilmek için en az {MinAffected} gerekiyor."),
            };
        }

        // Bozulan cihazların envanterde karşılığı olmayanları ayrıca sayılıyor.
        // Sinyal 9 cihazın 2'si üzerinden hesaplanıp "9 cihaz" gibi okunursa,
        // rapor ölçmediği bir kapsamı ölçmüş gibi gösterir.
        var unknown = affected.Count(row => !byId.ContainsKey(row.SourceId));

        var items = new List<EvidenceItem>();

        foreach (var field in CorrelationFields.Topology)
        {
            items.AddRange(ItemsForField(field, affected, byId, population, window));
        }

        var ranked = items
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = Kind,
            Status = ranked.Length > 0 ? EvidenceStatus.Gathered : EvidenceStatus.Empty,
            Items = ranked,
            Truncated = truncated,
            Detail = Describe(ranked.Length, affected.Length, unknown, truncated, budget),
        };
    }

    /// <summary>
    /// Envanteri pencerenin daraltmasına indirger. Boş liste "daraltma yok"
    /// demek — <see cref="RcaWindow"/>'un kendi semantiği.
    /// </summary>
    private static IReadOnlyList<SourceSummary> Narrow(
        IReadOnlyList<SourceSummary> inventory, RcaWindow window)
    {
        IEnumerable<SourceSummary> rows = inventory;

        if (window.OwnerGroups.Count > 0)
        {
            var groups = window.OwnerGroups.ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(s => groups.Contains(s.OwnerGroup));
        }

        if (window.SourceIds.Count > 0)
        {
            var ids = window.SourceIds.ToHashSet(StringComparer.Ordinal);
            rows = rows.Where(s => ids.Contains(s.SourceId));
        }

        return [.. rows];
    }

    private static bool HasAnyTopology(SourceSummary source) =>
        Values(source).Any(pair => pair.Value.Length > 0);

    private static IEnumerable<(string Field, string Value)> Values(SourceSummary source) =>
    [
        ("upstream", source.Upstream ?? string.Empty),
        ("vlan", source.Vlan ?? string.Empty),
        ("firmware", source.Firmware ?? string.Empty),
    ];

    private static string ValueOf(SourceSummary source, string field) =>
        Values(source).First(pair => pair.Field == field).Value;

    /// <summary>
    /// Tek bir öznitelik için kanıt satırları.
    ///
    /// <para>
    /// <b>Lift burada zamana değil, popülasyona göre.</b> Diğer korelasyonlar
    /// pencereyi <b>tabana</b> oranlıyor; burada soru başka: *"bozulanlar bu
    /// değeri, kapsamın geneline göre ne kadar fazla paylaşıyor"*. Paydalar
    /// bu yüzden kanıt satırının içinde adlarıyla duruyor — okuyan hangi iki
    /// oranın bölündüğünü tahmin etmek zorunda kalmasın.
    /// </para>
    /// </summary>
    private static IEnumerable<EvidenceItem> ItemsForField(
        string field,
        IReadOnlyList<SourceOnset> affected,
        IReadOnlyDictionary<string, SourceSummary> byId,
        IReadOnlyList<SourceSummary> population,
        RcaWindow window)
    {
        var affectedValues = affected
            .Select(row => byId.TryGetValue(row.SourceId, out var source) ? ValueOf(source, field) : string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();

        var populationValues = population
            .Select(source => ValueOf(source, field))
            .Where(value => value.Length > 0)
            .ToArray();

        if (affectedValues.Length == 0 || populationValues.Length == 0)
        {
            yield break;
        }

        // **Alan başına eksiklik ayrı bir sayı.** `Detail`'daki "envanterde yok"
        // sayısı yalnızca envanterde HİÇ kaydı olmayan cihazları anlatıyor;
        // kaydı olup bu alanı boş bırakılmış cihazlar ondan farklı bir olgu ve
        // ikisi tek sayıya toplanamaz.
        //
        // Kapatılan boşluk şu: 20 cihaz bozulur, 18'inin `upstream`'i boştur,
        // kalan 2'si core-01'dedir — payda sessizce 2'ye düşer ve satır
        // "bozulan 2/2 cihaz core-01'de" der. Cümle doğru, ama okuyan onu
        // "bozulanların hepsi" diye okur. Ölçülmemiş 18 cihaz raporda hiçbir iz
        // bırakmadan kaybolurdu.
        var withoutValue = affected.Count - affectedValues.Length;

        var populationCounts = populationValues
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.Ordinal);

        foreach (var group in affectedValues.GroupBy(value => value, StringComparer.Ordinal))
        {
            var inAffected = group.Count();

            if (inAffected < 2)
            {
                continue;
            }

            populationCounts.TryGetValue(group.Key, out var inPopulation);

            var lift = CorrelationMath.Lift(
                inAffected, affectedValues.Length, inPopulation, populationValues.Length);

            // 1.0 ve altı "genelden fazla paylaşılmıyor" demek — bir bulgu
            // değil. Bu bir ayar eşiği DEĞİL, sinyalin tanımı: üstünde bir sayı
            // seçmek ölçüm ister ve ölçülmedi, o yüzden seçilmedi. Sıralama
            // zaten en güçlüyü başa alıyor.
            if (lift <= 1.0)
            {
                continue;
            }

            yield return new EvidenceItem(
                $"topology:{field}:{group.Key}",
                "topology.shared-attribute",
                EvidenceKind.Topology,

                // Kanıtın zamanı, bu değeri paylaşan cihazların **ilk** bozulma
                // anı: hipotezin zaman çizelgesindeki yeri orası.
                affected
                    .Where(row => byId.TryGetValue(row.SourceId, out var s) && ValueOf(s, field) == group.Key)
                    .Min(row => row.FirstDegradedAt),
                Weight: lift,
                Summary: string.Create(
                    CultureInfo.InvariantCulture,
                    $"{field}={group.Key} · bozulan {inAffected}/{affectedValues.Length} cihaz · " +
                    $"kapsamda {inPopulation}/{populationValues.Length} · lift {lift:0.#}×" +
                    (withoutValue > 0
                        ? $" · ⚠ bozulan {withoutValue} cihazda {field} boş, orandan çıkarıldı"
                        : string.Empty)),
                Payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["field"] = field,
                    ["value"] = group.Key,
                    ["affected_count"] = inAffected.ToString(CultureInfo.InvariantCulture),
                    ["affected_total"] = affectedValues.Length.ToString(CultureInfo.InvariantCulture),
                    ["population_count"] = inPopulation.ToString(CultureInfo.InvariantCulture),
                    ["population_total"] = populationValues.Length.ToString(CultureInfo.InvariantCulture),

                    // Paydaya girmeyen bozulan cihaz sayısı. Sıfır olması da
                    // bilgi: "hepsinin bu alanı doluydu" ile "kaçının boş
                    // olduğunu bilmiyoruz" ayrı şeyler ve alan her satırda var.
                    ["affected_without_value"] = withoutValue.ToString(CultureInfo.InvariantCulture),
                    ["lift"] = lift.ToString("0.###", CultureInfo.InvariantCulture),
                },

                // **Drilldown kaynak listesine iniyor, alana değil.** `upstream`
                // olay tablosunda bir kolon değil; `EventQuery`'ye `upstream=x`
                // filtresi yazmak, sorgu tarafında karşılığı olmayan bir alan
                // adı göndermek olurdu. Bunun yerine o değeri paylaşan
                // cihazların kendisi filtreye giriyor.
                Drilldown: new EventQuery
                {
                    From = window.From,
                    To = window.To,
                    OwnerGroups = window.OwnerGroups,
                    SourceIds =
                    [
                        .. affected
                            .Where(row => byId.TryGetValue(row.SourceId, out var s) && ValueOf(s, field) == group.Key)
                            .Select(row => row.SourceId)
                    ],
                    Limit = 200,
                    Ascending = true,
                });
        }
    }

    private string Describe(int found, int affectedCount, int unknown, bool truncated, GatherBudget budget)
    {
        // Türün sınırı **her** dilimde yazılı: "topoloji" adı ilişki grafiği
        // çağrıştırıyor ve bu sağlayıcıda grafik yok. Sınırı yalnızca kod
        // yorumunda bırakmak, raporu okuyanın olduğundan fazlasını varsayması
        // demek olurdu.
        const string limit =
            "Tek kademe: ortak üst düğüm bakılıyor, ilişki grafiği (iki kademe yukarısı) yok.";

        var missing = unknown > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" ⚠ Bozulan {affectedCount} cihazın {unknown} tanesi envanterde yok; " +
                $"oranlar kalan cihazlar üzerinden hesaplandı.")
            : string.Empty;

        if (truncated)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en erken bozulanlar tutuldu ve " +
                $"oranlar bu kısmi küme üzerinden hesaplandı. {limit}{missing}");
        }

        return found > 0
            ? limit + missing
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Bozulan {affectedCount} cihazın paylaştığı, kapsamın genelinden ayrışan bir " +
                $"öznitelik yok. {limit}{missing}");
    }
}
