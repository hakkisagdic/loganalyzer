using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Evidence;
using Bizigo.Rca.Reasoning;

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
/// düzleştirilmiyor.</b> Beş olgu — <c>empty</c>, <c>never_fed</c>,
/// <c>unavailable</c>/<c>failed</c>, <c>not_registered</c>, <c>out_of_scope</c>
/// — tek bir "veri yok" değerine indirgenirse rapor, <b>bakmadığı bir şeye
/// bakmış gibi görünür</b> ve bunu hiçbir hata mesajı bozmaz. En pahalısı
/// <c>never_fed</c>: "değişiklik akışı hiç beslenmemiş" ekranda "değişiklik
/// olmadı" diye okunursa kullanıcı bir sinyalin <b>yokluğunu</b> bulgu sanar.
/// </para>
///
/// <para>
/// <c>out_of_scope</c> ile <c>not_registered</c>'ın ayrı olması F5 · S1'in
/// kararı: birincisi <b>verilmiş bir karar</b> ("bu ürün bakmıyor"), ikincisi
/// bir <b>bekleyiş</b>. Tek değere toplansalardı ekran verilmiş bir karardan
/// sonra da bekletmeye devam ederdi.
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
        EvidenceStatus.OutOfScope => "out_of_scope",
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
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("contradicting_evidence")] string ContradictingEvidence,
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("actual_root_cause")] string ActualRootCause,
    [property: JsonPropertyName("note")] string Note)
{
    public static RcaReviewResponse Of(GoldenReviewEntity review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new RcaReviewResponse(
            review.Id,
            review.BundleId,
            review.ReviewedAt,
            // Enum adı değil `snake_case` dizge: ekran bu dizgilere göre
            // dallanıyor, yani onlar sözleşme. `ToString()`'e bırakmak
            // `NotPresent`'ı tele `NotPresent` diye yazardı — camelCase
            // politikasının `idp_groups`'u `idpGroups` yaptığı kaza.
            WireName(review.Verdict.ToString()),
            WireName(review.ContradictingEvidence.ToString()),
            review.ReviewerSubject,
            review.ActualRootCause,
            review.Note);
    }

    private static string WireName(string enumName) =>
        string.Concat(enumName.Select((c, i) =>
            char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
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
    [property: JsonPropertyName("review")] RcaReviewResponse? Review,

    /// <summary>
    /// LLM'in ürettiği rapor — <b>model hiç koşmadıysa <see langword="null"/></b>
    /// (T51).
    ///
    /// <para>
    /// <b><see langword="null"/> "bulgu yok" DEĞİL.</b> Ayrım F3'ün dört
    /// durumuyla aynı sınıf ve aynı sebeple taşınıyor: koşup <b>her cümlesi
    /// atılmış</b> bir rapor da boş bulgu listesi taşıyor, ve ikisi ekranda tek
    /// bir "bulgu yok" kutusuna düşerse modelin uydurup elendiği gerçeği
    /// kaybolur — F4'ün ölçmek istediği tam olarak o.
    /// </para>
    /// </summary>
    [property: JsonPropertyName("reasoning")] RcaReasoningResponse? Reasoning)
{
    public static RcaReportResponse Of(
        DeterministicReport report,
        GoldenReviewEntity? review,
        StoredRcaReport? reasoning = null)
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
            review is null ? null : RcaReviewResponse.Of(review),
            reasoning is null ? null : RcaReasoningResponse.Of(reasoning));
    }
}

/// <param name="Hypothesis">Rapora giren metin — <b>yalnızca bağlanan cümleler</b>.</param>
public sealed record RcaReasoningFindingResponse(
    [property: JsonPropertyName("hypothesis")] string Hypothesis,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
    [property: JsonPropertyName("contradicting_evidence_ids")] IReadOnlyList<string> ContradictingEvidenceIds);

public sealed record RcaReasoningActionResponse(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds);

/// <param name="StepId">Hangi adım — gruplama <b>bu alandan</b>, dizge ayrıştırarak değil.</param>
/// <param name="Reason">Kapalı küme: <c>ran</c> · <c>not_applicable</c> · <c>skipped</c>.</param>
/// <param name="Detail">İnsan okunur gerekçe; ayrıştırılmak için değil.</param>
public sealed record RcaSentenceGateResponse(
    [property: JsonPropertyName("step_id")] string StepId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("detail")] string Detail);

/// <param name="PromptTokens">
/// Bildirilmediyse <see langword="null"/> — <b>0 değil</b>. Sıfır "ölçüldü ve
/// sıfır" demek.
/// </param>
/// <param name="UnreportedAttempts">
/// Sayı bildirmeyen deneme sayısı. Sıfır değilse toplam bir <b>alt sınır</b>.
/// </param>
/// <param name="TokensComplete">
/// Toplam tam mı. Okuyanın <see cref="UnreportedAttempts"/>'i yorumlamak zorunda
/// kalmaması için ayrı bir alan: türetilebilir olması, her tüketicinin aynı
/// çıkarımı kendi yapması demekti ve biri onu yanlış yapardı.
/// </param>
/// <param name="BoundaryOverridden">
/// K6 sınır doğrulaması <b>atlandı mı</b> (T54).
///
/// <para>
/// <b>Her iki hâlde de taşınıyor</b> ve ekran ikisini de yazıyor. Yalnız
/// <c>true</c> iken görünen bir rozet, muafiyetsiz koşumu <i>"bu soru
/// sorulmamış"</i> hâline sokardı — bu deponun <i>"bakılmadı" ile "bakıldı,
/// temiz" ayrı cümleler</i> kuralının aynısı.
/// </para>
/// </param>
/// <param name="BoundaryOverrideReason">
/// Gerekçe; muafiyet yoksa <see langword="null"/>. <b>Boş dize değil</b> — boş
/// dize <i>"gerekçe yazılmadı"</i> ile <i>"muafiyet yok"</i>u aynı değere
/// indirirdi, ve iki alanın birlikte taşınmasının bütün sebebi bu.
/// </param>
public sealed record RcaReasoningModelResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
    [property: JsonPropertyName("unreported_attempts")] int UnreportedAttempts,
    [property: JsonPropertyName("tokens_complete")] bool TokensComplete,
    [property: JsonPropertyName("boundary_overridden")] bool BoundaryOverridden,
    [property: JsonPropertyName("boundary_override_reason")] string? BoundaryOverrideReason);

/// <summary>
/// LLM raporunun tel hâli (T51) — ve <b>Karar 1'in sayacının göründüğü yer</b>.
///
/// <para>
/// <i>"Referanssız cümle rapora hiç girmiyor — ama atıldığı sayılıyor ve
/// gösteriliyor."</i> İçerik <see cref="Findings"/>'te yok; sayı
/// <see cref="DroppedSentenceCount"/>'ta var. Yalnızca atmak kaliteyi ölçülemez
/// yapardı — <i>"ölçemedim"</i> ile <i>"sorun yok"</i>un aynı çıktıya inmesi.
/// </para>
/// </summary>
/// <param name="ProducedSentenceCount">
/// Karar 1'in <b>paydası</b>. Paysız payda okunamaz, paydasız pay bir oran
/// değil.
/// </param>
/// <param name="DroppedSentenceRatio">
/// Payda sıfırsa <b><see langword="null"/></b>, <c>0</c> değil.
///
/// <para>
/// <c>0.0</c> bu telde <i>"hiç cümle atılmadı"</i> demek — yani <b>mükemmel
/// kalite</b>. Ölçülemeyen bir oranın en iyi sonuçla aynı baytları üretmesi,
/// bu deponun defalarca adını koyduğu sınıfın kendisi olurdu. Tüketici
/// <see langword="null"/>'ı paydadan düşebiliyor; <c>0.0</c>'ı düşemez, çünkü o
/// geçerli bir ölçüm.
/// </para>
/// </param>
/// <param name="FabricatedCitationSentenceCount">
/// Atıf <b>uydurmuş</b> cümleler; <paramref name="DroppedSentenceCount"/>'un alt
/// kümesi. <i>"Hiç atıf yapmadı"</i> ile <i>"atıf uydurdu"</i> iki farklı kalite
/// sorunu: biri prompt'un, diğeri modelin.
/// </param>
/// <param name="SentenceGateSkipped">
/// Cümle bağlamanın <b>koşmadığı</b> adımlar. Boş liste "her adımda koştu"
/// demek; sessizce atlayan bir kapı, kapının kendisinden tehlikeli.
/// </param>
public sealed record RcaReasoningResponse(
    [property: JsonPropertyName("report_id")] Guid ReportId,
    [property: JsonPropertyName("bundle_id")] Guid BundleId,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("scenario_id")] string ScenarioId,
    [property: JsonPropertyName("scenario_version")] string ScenarioVersion,
    [property: JsonPropertyName("findings")] IReadOnlyList<RcaReasoningFindingResponse> Findings,
    [property: JsonPropertyName("actions")] IReadOnlyList<RcaReasoningActionResponse> Actions,
    [property: JsonPropertyName("produced_sentence_count")] int ProducedSentenceCount,
    [property: JsonPropertyName("dropped_sentence_count")] int DroppedSentenceCount,
    [property: JsonPropertyName("dropped_sentence_ratio")] double? DroppedSentenceRatio,
    [property: JsonPropertyName("fabricated_citation_sentence_count")] int FabricatedCitationSentenceCount,
    [property: JsonPropertyName("sentence_gate_skipped")] IReadOnlyList<RcaSentenceGateResponse> SentenceGateSkipped,
    [property: JsonPropertyName("model")] RcaReasoningModelResponse Model)
{
    public static RcaReasoningResponse Of(StoredRcaReport stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var document = stored.Document;

        return new RcaReasoningResponse(
            stored.Id,
            document.BundleId,
            stored.CreatedAt,
            document.ScenarioId,
            document.ScenarioVersion,
            [.. document.Findings.Select(f => new RcaReasoningFindingResponse(
                f.Hypothesis, f.EvidenceIds, f.ContradictingEvidenceIds))],
            [.. document.Actions.Select(a => new RcaReasoningActionResponse(a.Text, a.EvidenceIds))],
            document.ProducedSentenceCount,
            document.DroppedSentenceCount,
            document.DroppedSentenceRatio,
            document.FabricatedCitationSentenceCount,
            [.. document.SentenceGateSkipped.Select(g => new RcaSentenceGateResponse(
                g.StepId, SnakeCase(g.Outcome), g.Detail))],
            new RcaReasoningModelResponse(
                document.ModelInfo.Provider,
                document.ModelInfo.Model,
                document.ModelInfo.PromptTokens,
                document.ModelInfo.CompletionTokens,
                document.ModelInfo.UnreportedAttempts,
                document.ModelInfo.TokensComplete,
                document.ModelInfo.BoundaryOverridden,
                document.ModelInfo.BoundaryOverrideReason));
    }

    /// <summary>
    /// <c>NotApplicable</c> → <c>not_applicable</c>. §8'in adlandırma kuralı
    /// telde de geçerli; <c>camelCase</c> politikası bu depoda
    /// <c>idp_groups</c>'u bir kez sessizce kırdı.
    /// </summary>
    private static string SnakeCase(SentenceGateOutcome outcome) => outcome switch
    {
        SentenceGateOutcome.Ran => "ran",
        SentenceGateOutcome.NotApplicable => "not_applicable",
        SentenceGateOutcome.Skipped => "skipped",

        // Kapalı kümeye üye eklenirse burası patlıyor — sessizce `ToString()`
        // düşmüyor. Bir tel değerinin enum adına bakarak sessizce değişmesi,
        // sözleşmeyi kimse karar vermeden kırardı.
        _ => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome, "Cümle kapısı durumunun tel karşılığı yazılmamış."),
    };
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

/// <param name="Rejection">
/// Kapalı kümeden gelen ret sebebi: <c>none</c> | <c>debounced</c> |
/// <c>depthexceeded</c> | <c>ancestorrepeat</c> | <c>quotaexceeded</c>.
///
/// <para>
/// Tek bir "reddedildi" cevabı, sonraki kişinin <b>hangi kapının kapattığını</b>
/// bilememesi demek olurdu — sınır, döngü ve kota farklı şeyler söylüyor
/// (RCA §5, T45).
/// </para>
/// </param>
public sealed record RcaAdmissionResponse(
    [property: JsonPropertyName("run_id")] Guid RunId,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("rejection")] string Rejection,
    [property: JsonPropertyName("detail")] string Detail);
