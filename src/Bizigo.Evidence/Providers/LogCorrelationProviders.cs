using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Evidence.Providers;

/// <summary>
/// Log korelasyonlarının ortak tabanı (T35).
///
/// <para>
/// Beşi de aynı şekli paylaşıyor: pencereyi <see cref="CorrelationWindow"/>'a
/// çevir, kapsam kapısından tek bir toplama sorgusu koştur, sonucu kanıt
/// satırlarına dönüştür — ve <b>boş sonucu doğru adlandır</b>. T34'ün ayrımı
/// burada da geçerli: "baktık, bir şey yok" bir kanıt.
/// </para>
/// </summary>
public abstract class LogCorrelationProvider(IScopedQuery query) : IEvidenceProvider
{
    /// <summary>Sağlayıcı başına kanıt satırı tavanı — bütçeden geliyor.</summary>
    protected IScopedQuery Query { get; } = query;

    public abstract string Id { get; }

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

        var correlation = new CorrelationWindow
        {
            From = window.From,
            To = window.To,
            BaselineFrom = window.BaselineFrom,
            BaselineTo = window.BaselineTo,
            OwnerGroups = window.OwnerGroups,
            SourceIds = window.SourceIds,
        };

        var (items, detail, truncated) = await GatherItemsAsync(
            correlation, window, scope, budget, cancellationToken);

        return new EvidenceSlice
        {
            ProviderId = Id,
            Kind = Kind,
            Status = items.Count > 0 ? EvidenceStatus.Gathered : EvidenceStatus.Empty,
            Items = items,
            Truncated = truncated,
            Detail = detail,
        };
    }

    protected abstract Task<(IReadOnlyList<EvidenceItem> Items, string Detail, bool Truncated)> GatherItemsAsync(
        CorrelationWindow correlation,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken);

    /// <summary>
    /// Kanıt satırından ham loga inen yol — yapılandırılmış sorgu, ham SQL
    /// değil (T34). Kanıt paketi saklandığı için SQL dizgisi yazmak, kapsam
    /// kapısını atlayan bir yolu diske yazmak olurdu.
    /// </summary>
    protected static EventQuery Drilldown(RcaWindow window, params FieldFilter[] filters) => new()
    {
        From = window.From,
        To = window.To,
        OwnerGroups = window.OwnerGroups,
        Filters = filters,
        Limit = 200,
        Ascending = true,
    };

    protected static string Trim(string body) =>
        body.Length > 160 ? body[..160] + "…" : body;
}

/// <summary>
/// <b>İlk-görülen imza</b> — RCA'nın tek en güçlü sinyali: "yeni bir şey oldu".
///
/// <para>
/// T29'dan önce bu sağlayıcı yazılamıyordu. <c>template_id</c> başarılı
/// olayların yalnızca %1'inde doluydu ve bir imzanın <b>ilk</b> görülüşünde
/// tanım gereği boştu — yani "yeni bir şey oldu" diyen tam o satırda kimlik
/// yoktu. <c>signature_hash</c> her olayda dolduğu için sinyal artık saf SQL.
/// </para>
/// </summary>
public sealed class FirstSeenSignatureProvider(IScopedQuery query) : LogCorrelationProvider(query)
{
    public override string Id => "logs.first-seen";

    protected override async Task<(IReadOnlyList<EvidenceItem>, string, bool)> GatherItemsAsync(
        CorrelationWindow correlation,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        var rows = await Query.GetFirstSeenSignaturesAsync(
            correlation, scope, budget.MaxItems + 1, cancellationToken);

        var truncated = rows.Count > budget.MaxItems;
        var kept = truncated ? rows.Take(budget.MaxItems).ToArray() : rows;

        var items = kept.Select(row => new EvidenceItem(
            $"first-seen:{row.SignatureHash}",
            "logs.first-seen",
            EvidenceKind.Log,
            row.FirstSeenAt,

            // Ağırlık kaynak yayılımından: aynı yeni imza on cihazda belirdiyse
            // tek cihazdakinden çok daha güçlü bir sinyal.
            Weight: row.SourceCount,
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"ilk kez görüldü · {row.SourceCount} kaynak · {row.EventCount} olay · {Trim(row.SampleBody)}"),
            Payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signature_hash"] = row.SignatureHash.ToString(CultureInfo.InvariantCulture),
                ["event_count"] = row.EventCount.ToString(CultureInfo.InvariantCulture),
                ["source_count"] = row.SourceCount.ToString(CultureInfo.InvariantCulture),
                ["sample_body"] = row.SampleBody,
            },
            Drilldown: Drilldown(window, new FieldFilter(
                "signature_hash",
                FilterOperator.Equals,
                [row.SignatureHash.ToString(CultureInfo.InvariantCulture)]))))
            .ToArray();

        var detail = items.Length > 0
            ? string.Empty
            : "Tabanda görülmeyen yeni imza yok — pencerede beliren her şey daha önce de görülmüş.";

        return (items,
            truncated ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en çok olay üretenler tutuldu." : detail,
            truncated);
    }
}

/// <summary>
/// <b>Hacim sapması</b> — var olan bir hatanın patlaması.
///
/// <para>
/// T29'dan önce bu sinyal kurulamıyordu: başarılı olaylarda sayılar gerçeğin
/// %1'iydi ve Poisson bunun üstüne kurulamaz. Örnekleme düzeltmesi <b>yok</b>,
/// çünkü örnekleme yok.
/// </para>
/// </summary>
public sealed class VolumeDeviationProvider(IScopedQuery query) : LogCorrelationProvider(query)
{
    /// <summary>
    /// Kanıt sayılmak için gereken en küçük z-score. <b>Ölçülmüş değil,
    /// varsayılan</b> — 3σ yaygın bir eşik ama bu veriye karşı sınanmadı ve
    /// baseline uzunluğuyla birlikte gözden geçirilmeli.
    /// </summary>
    public double MinZScore { get; init; } = 3.0;

    /// <summary>
    /// Küçük sayılarda Poisson gürültülü: beklenen 0,2 iken gözlenen 2 olması
    /// z ≈ 4 verir ama söylediği şey yoktur. Alt sınır o sahte sinyalleri kesiyor.
    /// </summary>
    public long MinWindowCount { get; init; } = 5;

    public override string Id => "logs.volume";

    protected override async Task<(IReadOnlyList<EvidenceItem>, string, bool)> GatherItemsAsync(
        CorrelationWindow correlation,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        var rows = await Query.GetSignatureVolumeAsync(correlation, scope, budget.MaxItems * 4, cancellationToken);

        var windowLength = window.To - window.From;
        var baselineLength = window.BaselineTo - window.BaselineFrom;

        var scored = rows
            .Where(row => row.WindowCount >= MinWindowCount)
            .Select(row =>
            {
                var expected = CorrelationMath.ExpectedInWindow(row.BaselineCount, baselineLength, windowLength);
                return (Row: row, Expected: expected, Z: CorrelationMath.PoissonZScore(row.WindowCount, expected));
            })
            .Where(scored => scored.Z >= MinZScore)
            .OrderByDescending(scored => scored.Z)
            .ToArray();

        var truncated = scored.Length > budget.MaxItems;
        var kept = truncated ? scored.Take(budget.MaxItems) : scored;

        var items = kept.Select(scored => new EvidenceItem(
            $"volume:{scored.Row.SignatureHash}",
            "logs.volume",
            EvidenceKind.Log,
            window.From,
            Weight: scored.Z,
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"hacim {scored.Row.WindowCount} olay, beklenen {scored.Expected:0.#} " +
                $"(z={scored.Z:0.#}) · {Trim(scored.Row.SampleBody)}"),
            Payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signature_hash"] = scored.Row.SignatureHash.ToString(CultureInfo.InvariantCulture),
                ["window_count"] = scored.Row.WindowCount.ToString(CultureInfo.InvariantCulture),
                ["baseline_count"] = scored.Row.BaselineCount.ToString(CultureInfo.InvariantCulture),
                ["expected"] = scored.Expected.ToString("0.###", CultureInfo.InvariantCulture),
                ["z_score"] = scored.Z.ToString("0.###", CultureInfo.InvariantCulture),
            },
            Drilldown: Drilldown(window, new FieldFilter(
                "signature_hash",
                FilterOperator.Equals,
                [scored.Row.SignatureHash.ToString(CultureInfo.InvariantCulture)]))))
            .ToArray();

        var detail = items.Length > 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Tabana göre anlamlı hacim sapması yok (eşik z≥{MinZScore:0.#}, en az {MinWindowCount} olay).");

        return (items,
            truncated ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en yüksek z-score'lar tutuldu." : detail,
            truncated);
    }
}

/// <summary>
/// <b>Ortak öznitelik (lift)</b> — "hepsi aynı switch'in arkasında".
///
/// <para>
/// Topoloji olmadan topoloji sezgisi. Lift oranların oranı olduğu için, pencerede
/// toplam hacim artsa bile yalnızca <b>yoğunlaşan</b> değerler öne çıkıyor.
/// </para>
/// </summary>
public sealed class AttributeLiftProvider(IScopedQuery query) : LogCorrelationProvider(query)
{
    /// <summary>Kanıt sayılmak için gereken en küçük lift. <b>Ölçülmemiş varsayılan.</b></summary>
    public double MinLift { get; init; } = 2.0;

    /// <summary>Tek bir olayın "8× lift" üretmesini engelleyen alt sınır.</summary>
    public long MinWindowCount { get; init; } = 5;

    public override string Id => "logs.attribute-lift";

    protected override async Task<(IReadOnlyList<EvidenceItem>, string, bool)> GatherItemsAsync(
        CorrelationWindow correlation,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        var rows = await Query.GetAttributeLiftAsync(
            correlation, scope, CorrelationFields.Lift, 50, cancellationToken);

        // Toplamlar alan bazında: her alan aynı olay kümesini bölüyor, yani
        // alan başına toplam pencere/taban hacmini veriyor. Tek bir küresel
        // toplam kullanmak, boş değeri elenen alanlarda oranı bozardı.
        var windowTotals = rows.GroupBy(r => r.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.WindowCount), StringComparer.Ordinal);
        var baselineTotals = rows.GroupBy(r => r.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.BaselineCount), StringComparer.Ordinal);

        var scored = rows
            .Where(row => row.WindowCount >= MinWindowCount)
            .Select(row => (Row: row, Lift: CorrelationMath.Lift(
                row.WindowCount, windowTotals[row.Field], row.BaselineCount, baselineTotals[row.Field])))
            .Where(scored => scored.Lift >= MinLift)
            .OrderByDescending(scored => scored.Lift)
            .ToArray();

        var truncated = scored.Length > budget.MaxItems;
        var kept = truncated ? scored.Take(budget.MaxItems) : scored;

        var items = kept.Select(scored => new EvidenceItem(
            $"lift:{scored.Row.Field}={scored.Row.Value}",
            "logs.attribute-lift",
            EvidenceKind.Log,
            window.From,
            Weight: scored.Lift,
            Summary: string.Create(
                CultureInfo.InvariantCulture,
                $"{scored.Row.Field}={scored.Row.Value} · lift {scored.Lift:0.#}× · " +
                $"{scored.Row.WindowCount} olay (tabanda {scored.Row.BaselineCount})"),
            Payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["field"] = scored.Row.Field,
                ["value"] = scored.Row.Value,
                ["window_count"] = scored.Row.WindowCount.ToString(CultureInfo.InvariantCulture),
                ["baseline_count"] = scored.Row.BaselineCount.ToString(CultureInfo.InvariantCulture),
                ["lift"] = scored.Lift.ToString("0.###", CultureInfo.InvariantCulture),
            },
            Drilldown: Drilldown(window, new FieldFilter(
                scored.Row.Field, FilterOperator.Equals, [scored.Row.Value]))))
            .ToArray();

        var detail = items.Length > 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Etkilenen olayların paylaştığı belirgin bir alan değeri yok (eşik {MinLift:0.#}×).");

        return (items,
            truncated ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en yüksek lift'ler tutuldu." : detail,
            truncated);
    }
}

/// <summary>
/// <b>Yayılma sırası</b> — kaynak başına ilk bozulma anı, sıralı.
///
/// <para>
/// İlk bozulan çoğu zaman kök nedene en yakın olandır. Sinyalin tamamı
/// <b>sıralama</b> olduğu için zamanın güvenilirliği burada kritik: zamanı
/// <c>parsed</c> olmayan bir olayın gerçek zamanı dakikalarca önce olabilir ve
/// sıra sessizce kayar. Sağlayıcı bunu <b>sayıyor ve söylüyor</b>.
/// </para>
/// </summary>
public sealed class PropagationProvider(IScopedQuery query) : LogCorrelationProvider(query)
{
    /// <summary>
    /// "Bozulma" eşiği — syslog ölçeğinde küçük sayı daha kötü. 3 = error.
    /// <b>Ölçülmemiş varsayılan.</b>
    /// </summary>
    public byte SeverityAtOrBelow { get; init; } = 3;

    public override string Id => "logs.propagation";

    private static string Describe(SourceOnset onset, TimeSpan lag)
    {
        var warning = onset.UnreliableTimeCount > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" · ⚠ {onset.UnreliableTimeCount} olayın zamanı güvenilmez")
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{onset.SourceId} · ilk bozulma +{lag.TotalSeconds:0}sn · {onset.DegradedCount} olay{warning}");
    }

    protected override async Task<(IReadOnlyList<EvidenceItem>, string, bool)> GatherItemsAsync(
        CorrelationWindow correlation,
        RcaWindow window,
        AccessScope scope,
        GatherBudget budget,
        CancellationToken cancellationToken)
    {
        var rows = await Query.GetPropagationAsync(
            correlation, scope, SeverityAtOrBelow, budget.MaxItems + 1, cancellationToken);

        var truncated = rows.Count > budget.MaxItems;
        var kept = truncated ? rows.Take(budget.MaxItems).ToArray() : rows;

        var items = CorrelationMath.WithLag(kept).Select(entry => new EvidenceItem(
            $"propagation:{entry.Onset.SourceId}",
            "logs.propagation",
            EvidenceKind.Log,
            entry.Onset.FirstDegradedAt,

            // Erken bozulan daha ağır: gecikme büyüdükçe ağırlık düşüyor.
            Weight: 1.0 / (1.0 + entry.Lag.TotalSeconds),
            Summary: Describe(entry.Onset, entry.Lag),
            Payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["owner_group"] = entry.Onset.OwnerGroup,
                ["source_id"] = entry.Onset.SourceId,
                ["lag_seconds"] = entry.Lag.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                ["degraded_count"] = entry.Onset.DegradedCount.ToString(CultureInfo.InvariantCulture),
                ["unreliable_time_count"] =
                    entry.Onset.UnreliableTimeCount.ToString(CultureInfo.InvariantCulture),
            },
            Drilldown: Drilldown(window, new FieldFilter(
                "source_id", FilterOperator.Equals, [entry.Onset.SourceId]))))
            .ToArray();

        // **Kabul kriteri:** penceresinde `time_source != parsed` olan olay
        // varsa çıktı bunu bildiriyor. Sıralamayı sunup zamanın güvenilmez
        // olduğunu söylememek, ölçülmemiş bir kesinlik iddia etmek olurdu.
        var unreliable = kept.Sum(row => row.UnreliableTimeCount);

        var detail = items.Length == 0
            ? "Pencerede bozulma sayılan olay yok."
            : unreliable > 0
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"⚠ {unreliable} olayın zamanı cihazdan değil (time_source != parsed); yayılma sırası kaymış olabilir.")
                : string.Empty;

        return (items,
            truncated ? $"Bütçe tavanı ({budget.MaxItems}) aşıldı; en erken bozulanlar tutuldu." : detail,
            truncated);
    }
}

/// <summary>Korelasyonların baktığı alanlar — izin listesinin kanıt tarafındaki adı.</summary>
public static class CorrelationFields
{
    /// <summary>
    /// Ortak öznitelik sinyalinin taradığı kolonlar. Depolama tarafındaki izin
    /// listesiyle <b>aynı olmak zorunda</b>; ayrışırsa sorgu istisna fırlatıyor
    /// (sessizce atlamıyor) ve bir test ikisini eşitliyor.
    /// </summary>
    public static readonly IReadOnlyList<string> Lift =
    [
        "source_id", "host", "vendor", "product", "parser_id", "proto", "action", "outcome",
    ];
}
