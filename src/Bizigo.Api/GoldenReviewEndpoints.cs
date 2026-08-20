using System.Text.Json.Serialization;
using Bizigo.ControlPlane;
using Bizigo.Evidence;

namespace Bizigo.Api;

/// <param name="Verdict"><c>correct</c> | <c>wrong</c> | <c>incomplete</c> | <c>unknown</c>.</param>
/// <param name="ContradictingEvidence">
/// <c>not_present</c> | <c>sound</c> | <c>trivial</c> | <c>unknown</c>.
/// </param>
public sealed record ReviewRequest
{
    [JsonPropertyName("bundle_id")]
    public Guid BundleId { get; init; }

    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = string.Empty;

    [JsonPropertyName("contradicting_evidence")]
    public string ContradictingEvidence { get; init; } = "not_present";

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Kaydın yazılacağı grup. Kapsamı tek gruplu kullanıcı vermek zorunda
    /// değil; çok gruplu kullanıcı <b>zorunda</b>.
    /// </summary>
    [JsonPropertyName("owner_group")]
    public string? OwnerGroup { get; init; }
}

/// <param name="Verdict"><c>correct</c> | <c>wrong</c> | <c>incomplete</c> | <c>unknown</c>.</param>
public sealed record CloseTriggerRequest
{
    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = string.Empty;

    [JsonPropertyName("contradicting_evidence")]
    public string ContradictingEvidence { get; init; } = "not_present";

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;
}

public sealed record ReviewResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("bundle_id")] Guid BundleId,
    [property: JsonPropertyName("trigger_id")] Guid? TriggerId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("contradicting_evidence")] string ContradictingEvidence,
    [property: JsonPropertyName("note")] string Note,
    [property: JsonPropertyName("reviewer")] string Reviewer,
    [property: JsonPropertyName("reviewed_at")] DateTimeOffset ReviewedAt);

public sealed record ReviewListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("reviews")] ReviewResponse[] Reviews);

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

/// <param name="BundleGenerated">
/// Paket bu kapatma sırasında mı üretildi. Ekran bunu söylemeli: kapatma artık
/// ucuz bir işlem değil ve beklemenin sebebi görünür olmalı.
/// </param>
public sealed record CloseTriggerResponse(
    [property: JsonPropertyName("trigger_id")] Guid TriggerId,
    [property: JsonPropertyName("closed_at")] DateTimeOffset ClosedAt,
    [property: JsonPropertyName("bundle_generated")] bool BundleGenerated,
    [property: JsonPropertyName("review")] ReviewResponse Review);

/// <summary>
/// Altın küme ve alarm kapatma uçları (T38).
///
/// <para>
/// <b>Kapatma neden <c>/v1/alerts</c> altında değil burada:</b> kapatma artık
/// bir alarm işlemi olmaktan çok bir <b>inceleme</b> işlemi — gövdesi karar
/// taşıyor, yan etkisi altın kümeye yazmak, ve paketi yoksa kanıt üretiyor.
/// Alarm grubuna konsaydı T37'nin ekranı iki ayrı sözleşmeye bakmak zorunda
/// kalırdı.
/// </para>
///
/// <para>
/// <b>Kapsam kontrolü burada değil</b>, <see cref="GoldenReviewStore"/> ve
/// <see cref="AlertClosureService"/>'te — <c>AlertEndpoints</c> ile aynı
/// bölünme. Uç yalnızca kullanıcının kapsamını geçiriyor.
/// </para>
/// </summary>
public static class GoldenReviewEndpoints
{
    public static IEndpointRouteBuilder MapGoldenReviews(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/reviews").WithTags("reviews");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("CreateReview")
            .Produces<ReviewResponse>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListReviews")
            .Produces<ReviewListResponse>();

        // Gösterge ayrı uçta: rapor ekranı onu paketten bağımsız gösteriyor ve
        // liste sorgusunu beklememesi gerekiyor.
        group.MapGet("/quality", QualityAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("GetGoldenSetQuality")
            .Produces<GoldenSetQualityResponse>();

        // Kapatma inceleme uçlarıyla aynı grupta: gövdesi karar taşıyor ve yan
        // etkisi altın kümeye yazmak.
        group.MapPost("/triggers/{triggerId:guid}/close", CloseAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("CloseAlertTrigger")
            .Produces<CloseTriggerResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        return routes;
    }

    /// <summary>
    /// Kullanıcı tetikli inceleme — <b>isteğe bağlı</b> olan yol.
    ///
    /// <para>
    /// Alarm tetiklide inceleme zorunlu ve kapatmanın parçası; burada zorlamak
    /// kullanıcıyı kaçırırdı (ticket'ın kendi ifadesi).
    /// </para>
    /// </summary>
    private static async Task<IResult> CreateAsync(
        ReviewRequest request,
        GoldenReviewStore reviews,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseVerdict(request.Verdict, out var verdict))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz karar.", "correct | wrong | incomplete | unknown"));
        }

        if (!TryParseContradicting(request.ContradictingEvidence, out var contradicting))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz çelişen kanıt kararı.", "not_present | sound | trivial | unknown"));
        }

        try
        {
            var written = await reviews.AddAsync(
                new ReviewInput(
                    request.BundleId,
                    TriggerId: null,
                    verdict,
                    contradicting,
                    request.Note,
                    request.OwnerGroup),
                user.Scope,
                cancellationToken);

            return Results.Created($"/v1/reviews/{written.Id}", ToResponse(written));
        }
        catch (ReviewRejectedException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> ListAsync(
        Guid bundleId,
        GoldenReviewStore reviews,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var rows = await reviews.ForBundleAsync(bundleId, user.Scope, cancellationToken);
        var mapped = rows.Select(ToResponse).ToArray();

        return Results.Ok(new ReviewListResponse(mapped.Length, mapped));
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

    /// <summary>
    /// Alarmı kapatır. <b>İnceleme gövdenin zorunlu parçası</b> — incelemesiz
    /// kapatma diye bir istek yok.
    /// </summary>
    private static async Task<IResult> CloseAsync(
        Guid triggerId,
        CloseTriggerRequest request,
        AlertClosureService closures,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryParseVerdict(request.Verdict, out var verdict))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz karar.", "correct | wrong | incomplete | unknown"));
        }

        if (!TryParseContradicting(request.ContradictingEvidence, out var contradicting))
        {
            return Results.BadRequest(new ErrorResponse(
                "Geçersiz çelişen kanıt kararı.", "not_present | sound | trivial | unknown"));
        }

        try
        {
            var closure = await closures.CloseAsync(
                triggerId, verdict, contradicting, request.Note, user.Scope, cancellationToken);

            return Results.Ok(new CloseTriggerResponse(
                closure.Trigger.Id,
                closure.Trigger.ClosedAt ?? default,
                closure.BundleGenerated,
                ToResponse(closure.Review)));
        }
        catch (ReviewRejectedException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
    }

    private static ReviewResponse ToResponse(GoldenReviewEntity entity) =>
        new(
            entity.Id,
            entity.BundleId,
            entity.TriggerId,
            entity.OwnerGroup,
            VerdictName(entity.Verdict),
            ContradictingName(entity.ContradictingEvidence),
            entity.Note,
            entity.ReviewerSubject,
            entity.ReviewedAt);

    // Enum adları telde `snake_case` metin olarak duruyor, sayı olarak değil:
    // T25'te `targetKind` bir yöne metin öbür yöne sayı gidiyordu ve sözleşme
    // sessizce kırılmıştı (B2). Eşleme elle yazılıyor ki enum'a eklenen bir
    // değer telde kendiliğinden görünmesin — yeni değer bilinçli bir hareket.
    private static bool TryParseVerdict(string? text, out ReviewVerdict verdict)
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

    private static bool TryParseContradicting(string? text, out ContradictingEvidenceVerdict verdict)
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

    private static string VerdictName(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Correct => "correct",
        ReviewVerdict.Wrong => "wrong",
        ReviewVerdict.Incomplete => "incomplete",
        _ => "unknown",
    };

    private static string ContradictingName(ContradictingEvidenceVerdict verdict) => verdict switch
    {
        ContradictingEvidenceVerdict.Sound => "sound",
        ContradictingEvidenceVerdict.Trivial => "trivial",
        ContradictingEvidenceVerdict.Unknown => "unknown",
        _ => "not_present",
    };
}
