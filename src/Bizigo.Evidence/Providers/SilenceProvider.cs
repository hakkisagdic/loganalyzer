using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

/// <summary>
/// <b>Sessizlik</b> — tabanda düzenli gönderip pencerede susan kaynaklar.
///
/// <para>
/// Ağ tarafında kritik: çöken bir cihaz log göndermez. Diğer dört sinyal "ne
/// geldi" diye sorarken bu tek başına "ne <b>gelmedi</b>" diye soruyor, ve
/// yokluk hiçbir satırda görünmediği için ancak iki pencere karşılaştırılarak
/// bulunuyor.
/// </para>
///
/// <para>
/// <b>Sorgu yüzeyi paylaşılıyor, kopyalanmıyor.</b>
/// <c>GetSourceActivityAsync</c> T21'de tam bu amaçla tek yere kondu ve bugün
/// sessizlik alarmı, envanter listesi ve boru hattı sağlığı onu okuyor. Dördüncü
/// bir kopya yazmak, dört farklı zaman kolonu seçimi ve dört farklı kapsam
/// davranışı demek olurdu — ve ayrıştıkları ancak alarm yanlış tetiklendiğinde
/// fark edilirdi.
/// </para>
///
/// <para>
/// Alarm motorunun <b>değerlendiricisi</b> çağrılmıyor, bilerek: o farklı bir
/// soyutlama düzeyi (kural eşiği, susturma, zamanlama). RCA'nın istediği ham
/// olgu — "bu kaynak ne zamandır susuyor" — eşiksiz.
/// </para>
/// </summary>
public sealed class SilenceProvider(IScopedQuery query) : IEvidenceProvider
{
    /// <summary>
    /// Bir kaynağın "düzenli gönderiyordu" sayılması için tabanda pencere
    /// başına düşmesi gereken en az olay.
    ///
    /// <para>
    /// Bu eşik olmadan sinyal yanlış alarm makinesi: tabanda ayda bir satır
    /// yollayan bir kaynağın 45 dakikalık pencerede susması bir bulgu değil,
    /// normal. <b>Ölçülmemiş varsayılan</b> — baseline uzunluğuyla birlikte
    /// gözden geçirilmeli.
    /// </para>
    /// </summary>
    public double MinExpectedPerWindow { get; init; } = 5.0;

    public string Id => "logs.silence";

    public EvidenceKind Kind => EvidenceKind.Log;

    public bool IsAvailable => true;

    public async Task<EvidenceSlice> GatherAsync(
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(budget);

        var baseline = await query.GetSourceActivityAsync(
            new SourceActivityWindow
            {
                From = window.BaselineFrom,
                To = window.BaselineTo,
                OwnerGroups = window.OwnerGroups,
                SourceIds = window.SourceIds,
            },
            scope,
            cancellationToken);

        var current = await query.GetSourceActivityAsync(
            new SourceActivityWindow
            {
                From = window.From,
                To = window.To,
                OwnerGroups = window.OwnerGroups,
                SourceIds = window.SourceIds,
            },
            scope,
            cancellationToken);

        // `GetSourceActivityAsync` yalnızca **verisi olan** kaynakları
        // döndürüyor, yani pencerede hiç görünmeyen bir kaynak listede yok.
        // Sessizliğin tanımı tam olarak bu yokluk.
        var active = current.Select(row => row.SourceId).ToHashSet(StringComparer.Ordinal);

        var windowLength = window.To - window.From;
        var baselineLength = window.BaselineTo - window.BaselineFrom;

        var silent = baseline
            .Where(row => !active.Contains(row.SourceId))
            .Where(row => CorrelationMath.WasRegular(
                row.EventCount, baselineLength, windowLength, MinExpectedPerWindow))
            .Select(row => (Row: row, Expected: CorrelationMath.ExpectedInWindow(
                row.EventCount, baselineLength, windowLength)))
            .OrderByDescending(entry => entry.Expected)
            .ToArray();

        var truncated = silent.Length > budget.MaxItems;
        var kept = truncated ? silent.Take(budget.MaxItems) : silent;

        var items = kept.Select(entry => new EvidenceItem(
            $"silence:{entry.Row.SourceId}",
            Id,
            EvidenceKind.Log,

            // Kanıtın zamanı, kaynaktan son haber alınan an — "ne zamandır
            // susuyor" sorusunun cevabı orada başlıyor. `LastIngestedAt`
            // kullanılıyor, `LastEventAt` değil: soru "cihazdan haber aldık mı",
            // ve cihaz saati kayan bir kaynak aksi hâlde yanlış görünürdü.
            entry.Row.LastIngestedAt,
            Weight: entry.Expected,
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Row.SourceId} sustu · tabanda pencere başına ~{entry.Expected:0.#} olay · " +
                $"son haber {entry.Row.LastIngestedAt:yyyy-MM-dd HH:mm}"),
            Payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner_group"] = entry.Row.OwnerGroup,
                ["source_id"] = entry.Row.SourceId,
                ["baseline_count"] = entry.Row.EventCount.ToString(CultureInfo.InvariantCulture),
                ["expected_in_window"] = entry.Expected.ToString("0.###", CultureInfo.InvariantCulture),
                ["last_ingested_at"] = entry.Row.LastIngestedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            Drilldown: new EventQuery
            {
                // Susan bir kaynakta pencere boş; ilgi çekici olan **öncesi**.
                From = window.BaselineTo,
                To = window.To,
                SourceIds = [entry.Row.SourceId],
                Limit = 200,
            }))
            .ToArray();

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = EvidenceKind.Log,
            Status = items.Length > 0 ? EvidenceStatus.Gathered : EvidenceStatus.Empty,
            Items = items,
            Truncated = truncated,
            Detail = items.Length > 0
                ? truncated
                    ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en çok beklenen kaynaklar tutuldu."
                    : string.Empty
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Susan kaynak yok (tabanda pencere başına en az {MinExpectedPerWindow:0.#} olay eşiği)."),
        };
    }
}
