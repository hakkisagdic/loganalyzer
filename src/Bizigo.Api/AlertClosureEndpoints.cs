using System.Text.Json.Serialization;
using Bizigo.ControlPlane;
using Bizigo.Evidence;

namespace Bizigo.Api;

/// <param name="Verdict"><c>correct</c> | <c>wrong</c> | <c>incomplete</c> | <c>unknown</c>.</param>
/// <param name="ContradictingEvidence">
/// <c>not_present</c> | <c>sound</c> | <c>trivial</c> | <c>unknown</c>.
/// </param>
/// <param name="ActualRootCause">
/// Rapor yanlışsa <b>doğrusu</b>. Boş bırakılabilir ve boşluğu bilgi taşıyor:
/// <c>wrong</c> deyip burayı boş bırakan inceleyen, yanlışı görmüş ama doğrusunu
/// bilmiyor demektir.
/// </param>
public sealed record CloseTriggerRequest
{
    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = string.Empty;

    [JsonPropertyName("contradicting_evidence")]
    public string ContradictingEvidence { get; init; } = "not_present";

    [JsonPropertyName("actual_root_cause")]
    public string ActualRootCause { get; init; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;
}

/// <param name="BundleGenerated">
/// Paket bu kapatma sırasında mı üretildi. Ekran bunu söylemeli: kapatma artık
/// ucuz bir işlem değil ve beklemenin sebebi görünür olmalı.
/// </param>
public sealed record CloseTriggerResponse(
    [property: JsonPropertyName("trigger_id")] Guid TriggerId,
    [property: JsonPropertyName("closed_at")] DateTimeOffset ClosedAt,
    [property: JsonPropertyName("bundle_generated")] bool BundleGenerated,
    [property: JsonPropertyName("review_id")] Guid ReviewId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup);

/// <param name="Accuracy">
/// Doğruluk oranı. <b><see langword="null"/> olabilir</b> ve sıfırdan farklıdır:
/// karar verilmiş inceleme yoksa oran <i>yoktur</i>. Ekran ikisini aynı
/// göstermemeli — "%0 doğru" ile "henüz karar verilmedi" farklı cümleler.
/// </param>
/// <param name="UnknownRatio">
/// "Bilmiyorum" oranı — kendisi bir gösterge. Yüksekse ya kanıt paketi yetersiz
/// ya soru yanlış soruluyor.
/// </param>
public sealed record GoldenSetQualityResponse(
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("decided")] long Decided,
    [property: JsonPropertyName("correct")] long Correct,
    [property: JsonPropertyName("unknown")] long Unknown,
    [property: JsonPropertyName("accuracy")] double? Accuracy,
    [property: JsonPropertyName("unknown_ratio")] double? UnknownRatio);

/// <summary>
/// Alarm kapatma ve altın küme göstergesi (T38).
///
/// <para>
/// <b>İnceleme <i>yazma</i> ucu burada değil:</b> o <c>POST /v1/rca/{id}/review</c>
/// ve <c>EvidenceEndpoints</c>'te (T37). İki ajan bu tabloyu paralel yazınca iki
/// tablo doğmuştu; §9'un dediği gibi kesişen sözleşme önceden çivilenmeli.
/// Burada kalan iki uç, o ucun <b>kapsamadığı</b> iki iş.
/// </para>
///
/// <para>
/// <b>Neden iki ayrı önek:</b> kapatma bir <i>alarm</i> işlemi — ekranı alarm
/// ekranı, kaynağı bir tetiklenme, ve incelemeyi yan etki olarak yazıyor.
/// Gösterge ise rapor ekranının köşesinde duruyor, dolayısıyla <c>/v1/rca</c>
/// altında. Uçları ait oldukları önekten ayırıp tek bir "reviews" grubuna
/// toplamak, iki ekranı da kendi tabanının dışına bakmaya zorlardı.
/// </para>
///
/// <para>
/// <c>/v1/rca</c> öneki <c>EvidenceEndpoints</c> ile <b>paylaşılıyor</b>. Tek
/// alternatif o dosyayı düzenlemekti ve dosyanın sahibi şu anda üstünde
/// çalışıyor — §9'un kaçınmamızı istediği çakışma tam olarak bu.
/// </para>
/// </summary>
public static class AlertClosureEndpoints
{
    public static IEndpointRouteBuilder MapAlertClosure(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/alerts/triggers/{triggerId:guid}/close", CloseAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithTags("alerts")
            .WithName("CloseAlertTrigger")
            .Produces<CloseTriggerResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        routes.MapGet("/v1/rca/quality", QualityAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithTags("rca")
            .WithName("GetGoldenSetQuality")
            .Produces<GoldenSetQualityResponse>();

        return routes;
    }

    /// <summary>
    /// Alarmı kapatır. <b>İnceleme gövdenin zorunlu parçası</b> — incelemesiz
    /// kapatma diye bir istek yok, çünkü <c>CloseAsync</c> onu parametre olarak
    /// istiyor.
    /// </summary>
    private static async Task<IResult> CloseAsync(
        Guid triggerId,
        CloseTriggerRequest request,
        AlertClosureService closures,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ReviewWire.TryParseVerdict(request.Verdict, out var verdict))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz karar.", "correct | wrong | incomplete | unknown"));
        }

        if (!ReviewWire.TryParseContradicting(request.ContradictingEvidence, out var contradicting))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz çelişen kanıt kararı.", "not_present | sound | trivial | unknown"));
        }

        try
        {
            var closure = await closures.CloseAsync(
                triggerId,
                verdict,
                contradicting,
                request.Note,
                user.Scope,
                cancellationToken,
                request.ActualRootCause);

            return Results.Ok(new CloseTriggerResponse(
                closure.Trigger.Id,
                closure.Trigger.ClosedAt ?? default,
                closure.BundleGenerated,
                closure.Review.Id,
                closure.Review.OwnerGroup));
        }
        catch (ReviewRejectedException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> QualityAsync(
        GoldenReviewStore reviews,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var quality = await reviews.QualityAsync(user.Scope, cancellationToken);

        return Results.Ok(new GoldenSetQualityResponse(
            quality.Total,
            quality.Decided,
            quality.Correct,
            quality.Unknown,
            quality.Accuracy,
            quality.UnknownRatio));
    }
}

/// <summary>
/// Karar değerlerinin tel karşılıkları — <b>tek yer</b>.
///
/// <para>
/// <c>ToString()</c> kullanılmıyor: o dizgiler sözleşme ve enum'a eklenen bir
/// değer telde kendiliğinden belirmemeli. B2'nin şekli buydu — <c>targetKind</c>
/// bir yöne metin öbür yöne sayı gidiyordu ve sözleşme sessizce kırılmıştı.
/// </para>
///
/// <para>
/// <b>Neden <c>public</c>:</b> bu eşleme sözleşmenin kendisi ve sınanabilmesi
/// gerekiyor (<c>ReviewWireTests</c>); ayrıca <c>EvidenceEndpoints</c>'in
/// <c>Enum.TryParse</c> ile yaptığı gevşek çözüm buraya taşınabilsin diye
/// erişilebilir. İkinci bir eşleme yazmak §9'un yasakladığı kopyalama olurdu.
/// </para>
/// </summary>
public static class ReviewWire
{
    public static bool TryParseVerdict(string? text, out ReviewVerdict verdict)
    {
        verdict = ReviewVerdict.Unknown;

        switch (text?.Trim())
        {
            case "correct": verdict = ReviewVerdict.Correct; return true;
            case "wrong": verdict = ReviewVerdict.Wrong; return true;
            case "incomplete": verdict = ReviewVerdict.Incomplete; return true;
            case "unknown": verdict = ReviewVerdict.Unknown; return true;
            default: return false;
        }
    }

    public static bool TryParseContradicting(string? text, out ContradictingEvidenceVerdict verdict)
    {
        verdict = ContradictingEvidenceVerdict.NotPresent;

        switch (text?.Trim())
        {
            case "not_present": verdict = ContradictingEvidenceVerdict.NotPresent; return true;
            case "sound": verdict = ContradictingEvidenceVerdict.Sound; return true;
            case "trivial": verdict = ContradictingEvidenceVerdict.Trivial; return true;
            case "unknown": verdict = ContradictingEvidenceVerdict.Unknown; return true;
            default: return false;
        }
    }
}
