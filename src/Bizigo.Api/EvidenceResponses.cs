using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Evidence;

namespace Bizigo.Api;

/// <summary>
/// Kanıttan ham loga inen yolun <b>tel</b> hâli.
///
/// <para>
/// Kaynağı <see cref="EventQuery"/> — ham SQL <b>değil</b> (T34 §5). Ekran bunu
/// olay arama ekranının URL'ine çeviriyor ve sorgu tekrar <c>IScopedQuery</c>'den
/// geçiyor, yani kapsam kapısı (K17) yeniden uygulanıyor. SQL dizgisi taşımak,
/// saklanan bir pakete kapsam kapısını atlayan bir yol yazmak olurdu.
/// </para>
///
/// <para>
/// <b>Yalnızca ekranın kurabileceği kadarı taşınıyor.</b> <c>Limit</c>,
/// <c>After</c>, <c>Ascending</c> dışarıda: onlar sorgu değil sayfalama ve
/// ekranın kendi kararı.
/// </para>
/// </summary>
public sealed record RcaDrilldownResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("owner_groups")] IReadOnlyList<string> OwnerGroups,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds,
    [property: JsonPropertyName("full_text")] string? FullText,
    [property: JsonPropertyName("filters")] IReadOnlyList<RcaDrilldownFilterResponse> Filters)
{
    public static RcaDrilldownResponse? Of(EventQuery? query) => query is null
        ? null
        : new RcaDrilldownResponse(
            query.From,
            query.To,
            query.OwnerGroups,
            query.SourceIds,
            query.FullText,
            [.. query.Filters.Select(f => new RcaDrilldownFilterResponse(
                f.Field,
                f.Operator.ToString().ToLowerInvariant(),
                f.Values))]);
}

public sealed record RcaDrilldownFilterResponse(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("values")] IReadOnlyList<string> Values);

/// <summary>
/// Tek bir bulgu satırı.
///
/// <para>
/// <b>Skor yok — bilerek</b> (T36 devir notu §2). Türetilmiş bir değer, pakete
/// yazılmıyor, ve <c>4.73</c> ekranda hiçbir şey ifade etmediği gibi ölçülmüş
/// bir kesinlik iddiası olurdu. Gösterilmesi gereken şey <b>sıra</b> ve
/// sağlayıcının adı; ikisi de burada.
/// </para>
///
/// <para>
/// <c>Payload</c> sağlayıcıya özgü ham sayıları taşıyor (<c>signature_hash</c>,
/// <c>z_score</c>, <c>lift</c>…) ve detay panelinin girdisi. Anahtarları
/// <c>snake_case</c>, sağlayıcıdan geldiği gibi.
/// </para>
/// </summary>
public sealed record RcaFindingResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("payload")] IReadOnlyDictionary<string, string> Payload,

    /// <summary><see langword="null"/> ise bağlantı yok — ekran boş arama açmamalı.</summary>
    [property: JsonPropertyName("drilldown")] RcaDrilldownResponse? Drilldown)
{
    public static RcaFindingResponse Of(RankedEvidence ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        return new RcaFindingResponse(
            ranked.Item.Id,
            ranked.Item.ProviderId,
            ranked.Item.Kind.ToString().ToLowerInvariant(),
            ranked.Item.Timestamp,
            ranked.Item.Summary,
            ranked.Item.Payload,
            RcaDrilldownResponse.Of(ranked.Item.Drilldown));
    }
}

/// <summary>
/// Bir sağlayıcının koşusunun <b>sonuç cinsi</b> — raporun en kolay
/// kaybedeceği bilgi.
///
/// <para>
/// <b><see cref="Status"/> tel üzerinde ayrı bir alan olarak duruyor ve
/// düzleştirilmiyor.</b> Dört olgu — <c>empty</c>, <c>never_fed</c>,
/// <c>unavailable</c>/<c>failed</c>, <c>not_registered</c> — tek bir "veri yok"
/// değerine indirgenirse rapor, <b>bakmadığı bir şeye bakmış gibi görünür</b> ve
/// bunu hiçbir hata mesajı bozmaz. En pahalısı <c>never_fed</c>: "değişiklik
/// akışı hiç beslenmemiş" ekranda "değişiklik olmadı" diye okunursa kullanıcı
/// bir sinyalin <b>yokluğunu</b> bulgu sanar.
/// </para>
///
/// <para>
/// <see cref="Detail"/> her <c>gathered</c> olmayan durumda dolu ve insan
/// okunur: "neden bakılmadı" sorusunun cevabı orada. Durum etiketi tek başına
/// yeterli bilgi vermiyor.
/// </para>
/// </summary>
public sealed record RcaSliceResponse(
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("truncated")] bool Truncated)
{
    public static RcaSliceResponse Of(EvidenceSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return new RcaSliceResponse(
            slice.ProviderId,
            slice.Kind.ToString().ToLowerInvariant(),
            SnakeCase(slice.Status),
            slice.Detail,
            slice.Items.Count,
            slice.Truncated);
    }

    /// <summary>
    /// <c>NeverFed</c> → <c>never_fed</c>. Elle yazılıyor çünkü tel üzerindeki
    /// bu dizgiler <b>sözleşme</b>: ekran onlara göre dallanıyor ve bir gün
    /// <c>ToString()</c> davranışı değişirse dört durum sessizce tek görünüme
    /// düşerdi.
    /// </summary>
    private static string SnakeCase(EvidenceStatus status) => status switch
    {
        EvidenceStatus.Gathered => "gathered",
        EvidenceStatus.Empty => "empty",
        EvidenceStatus.NeverFed => "never_fed",
        EvidenceStatus.Unavailable => "unavailable",
        EvidenceStatus.Failed => "failed",
        EvidenceStatus.NotRegistered => "not_registered",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Bilinmeyen kanıt durumu."),
    };
}

/// <summary>
/// Pencerenin zamanına ne kadar güvenilebileceği.
///
/// <para>
/// <see cref="Measured"/> ayrı bir alan: <b>"ölçemedik" ile "sıfır" farklı</b> ve
/// ikincisi "sorun yok" diye okunuyor. <see cref="UnreliableRatio"/> ölçülmediyse
/// <see langword="null"/> — sıfır değil.
/// </para>
/// </summary>
public sealed record RcaTrustResponse(
    [property: JsonPropertyName("measured")] bool Measured,
    [property: JsonPropertyName("total_events")] long TotalEvents,
    [property: JsonPropertyName("unreliable_time_events")] long UnreliableTimeEvents,
    [property: JsonPropertyName("unreliable_ratio")] double? UnreliableRatio)
{
    public static RcaTrustResponse Of(WindowTrust trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return new RcaTrustResponse(
            trust.Measured, trust.TotalEvents, trust.UnreliableTimeEvents, trust.UnreliableRatio);
    }
}

public sealed record RcaWindowResponse(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("baseline_from")] DateTimeOffset BaselineFrom,
    [property: JsonPropertyName("baseline_to")] DateTimeOffset BaselineTo,
    [property: JsonPropertyName("owner_groups")] IReadOnlyList<string> OwnerGroups,
    [property: JsonPropertyName("source_ids")] IReadOnlyList<string> SourceIds)
{
    public static RcaWindowResponse Of(RcaWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new RcaWindowResponse(
            window.From, window.To, window.BaselineFrom, window.BaselineTo,
            window.OwnerGroups, window.SourceIds);
    }
}

public sealed record RcaReviewResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("bundle_id")] Guid BundleId,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset ReviewedAt,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("actual_root_cause")] string ActualRootCause,
    [property: JsonPropertyName("note")] string Note)
{
    public static RcaReviewResponse Of(EvidenceReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new RcaReviewResponse(
            review.Id, review.BundleId, review.ReviewedAt,
            review.State, review.Reviewer, review.ActualRootCause, review.Note);
    }
}

/// <summary>
/// Raporun tel hâli — <b>ekranın kaynağı</b>.
///
/// <para>
/// <b>Neden ayrı bir tip:</b> <see cref="DeterministicReport"/> ve
/// <see cref="EvidenceBundle"/> domain tipleri; onlara eklenen her alan kimse
/// karar vermeden API'ye sızardı (§8, T27'de <c>ReplayResponse</c> için verilen
/// kararın aynısı). Sözleşmeye neyin gireceği <b>sunucunun</b> kararı.
/// </para>
///
/// <para>
/// <b>İki liste ayrı duruyor ve birleştirilmemeli:</b> <see cref="Silent"/>
/// koşan ama bir şey bulamayanlar ("baktık, yok" — bu bir kanıt),
/// <see cref="NotConsulted"/> ise bakılamayanlar. Tek listede birleşmeleri
/// T34 ve T36'nın kurduğu her şeyi tek satırda geri alırdı.
/// </para>
///
/// <para>
/// <see cref="NotConsulted"/> boş olması "her şeye bakıldı" demek ve
/// <b>gösterilmeye değer bir bilgi</b>; ekran o bölümü sessizce kaybetmemeli.
/// </para>
/// </summary>
public sealed record RcaReportResponse(
    [property: JsonPropertyName("bundle_id")] Guid BundleId,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("gathered_at")] DateTimeOffset GatheredAt,
    [property: JsonPropertyName("window")] RcaWindowResponse Window,
    [property: JsonPropertyName("findings")] IReadOnlyList<RcaFindingResponse> Findings,
    [property: JsonPropertyName("timeline")] IReadOnlyList<RcaFindingResponse> Timeline,

    /// <summary>Koştu, kanıt çıkmadı — "baktık, yok" da bir cevap.</summary>
    [property: JsonPropertyName("silent")] IReadOnlyList<RcaSliceResponse> Silent,

    /// <summary>Bakılamayanlar; her satır <b>neden</b> bakılmadığını taşıyor.</summary>
    [property: JsonPropertyName("not_consulted")] IReadOnlyList<RcaSliceResponse> NotConsulted,

    [property: JsonPropertyName("trust")] RcaTrustResponse Trust,
    [property: JsonPropertyName("out_of_scope_count")] long OutOfScopeCount,
    [property: JsonPropertyName("is_partial")] bool IsPartial,

    /// <summary>Paketin son incelemesi; hiç incelenmemişse <see langword="null"/>.</summary>
    [property: JsonPropertyName("review")] RcaReviewResponse? Review)
{
    public static RcaReportResponse Of(DeterministicReport report, EvidenceReview? review)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new RcaReportResponse(
            report.BundleId,
            report.ContentHash,
            report.GatheredAt,
            RcaWindowResponse.Of(report.Window),
            [.. report.Findings.Select(RcaFindingResponse.Of)],
            [.. report.Timeline.Select(RcaFindingResponse.Of)],
            [.. report.Silent.Select(RcaSliceResponse.Of)],
            [.. report.NotConsulted.Select(RcaSliceResponse.Of)],
            RcaTrustResponse.Of(report.Trust),
            report.OutOfScopeCount,
            report.IsPartial,
            review is null ? null : RcaReviewResponse.Of(review));
    }
}

/// <summary>
/// Liste satırı — belge <b>açılmadan</b> okunuyor (üst veri kolonlarda).
/// </summary>
public sealed record RcaBundleSummaryResponse(
    [property: JsonPropertyName("bundle_id")] Guid BundleId,
    [property: JsonPropertyName("gathered_at")] DateTimeOffset GatheredAt,
    [property: JsonPropertyName("window_from")] DateTimeOffset WindowFrom,
    [property: JsonPropertyName("window_to")] DateTimeOffset WindowTo,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("out_of_scope_count")] long OutOfScopeCount,
    [property: JsonPropertyName("is_partial")] bool IsPartial)
{
    public static RcaBundleSummaryResponse Of(EvidenceBundleSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new RcaBundleSummaryResponse(
            summary.Id, summary.GatheredAt, summary.WindowFrom, summary.WindowTo,
            summary.ContentHash, summary.OutOfScopeCount, summary.IsPartial);
    }
}

public sealed record RcaBundleListResponse(
    [property: JsonPropertyName("bundles")] IReadOnlyList<RcaBundleSummaryResponse> Bundles);
