using System.Text.Json.Serialization;
using Bizigo.Replay;

namespace Bizigo.Api;

/// <remarks>
/// Alan adları <c>snake_case</c>, projenin geri kalanıyla aynı. Tek bir ucun
/// yanıtta bir dil, istekte başka bir dil konuşması iki uç ailesinin
/// ayrışmasından beter olurdu — T25'te aynı gerekçeyle
/// <c>ChangeWriteRequest</c> da çevrilmişti.
/// </remarks>
public sealed record ReplayRequest
{
    [JsonPropertyName("from")]
    public required DateTimeOffset From { get; init; }

    [JsonPropertyName("to")]
    public required DateTimeOffset To { get; init; }

    [JsonPropertyName("parser_id")]
    public string ParserId { get; init; } = string.Empty;

    [JsonPropertyName("parser_version")]
    public string ParserVersion { get; init; } = string.Empty;

    [JsonPropertyName("owner_groups")]
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    [JsonPropertyName("source_ids")]
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>Varsayılan <see langword="true"/>: yazma <b>açıkça</b> istenmeli.</summary>
    [JsonPropertyName("dry_run")]
    public bool DryRun { get; init; } = true;

    [JsonPropertyName("continue_on_missing_objects")]
    public bool ContinueOnMissingObjects { get; init; }

    /// <summary>
    /// Hâlâ yazılan bölümü de kapsamaya izin ver (T27 bulgusu). Varsayılan
    /// <see langword="false"/>: açık bölümü replay etmek canlı veriyi sessizce
    /// siliyor.
    /// </summary>
    [JsonPropertyName("allow_open_partition")]
    public bool AllowOpenPartition { get; init; }
}

/// <summary>
/// Replay ucu (T10 · T11, F1 §7.2).
///
/// <para>
/// <b>Varsayılan kuru koşu.</b> <c>dryRun</c> alanı gönderilmezse rapor üretilir,
/// yazma yapılmaz. Geçmişi yeniden yazmak açıkça istenmesi gereken bir şey;
/// varsayılanı "uygula" yapmak, bir alan unutulduğunda üretim verisini
/// değiştirirdi.
/// </para>
/// </summary>
public static class ReplayEndpoints
{
    public static IEndpointRouteBuilder MapReplay(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/replay", HandleAsync)
            // Geçmişi yeniden yazmak yönetici işi.
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("Replay")
            .WithTags("replay")
            // Yanıt tipi T27'de geldi. Uç bugüne kadar `ReplayReport`'u
            // doğrudan döndürüyordu: tip bildirilmemişti ama domain tipi yine
            // de tele sızıyordu — tiplenmemiş olmaktan kötü bir hâl.
            .Produces<ReplayResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ReplayBlockedResponse>(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> HandleAsync(
        ReplayRequest request,
        ReplayEngine engine,
        CancellationToken cancellationToken)
    {
        if (request.From >= request.To)
        {
            return Results.BadRequest(
                new ErrorResponse("'from' değeri 'to' değerinden küçük olmalı."));
        }

        if (request.ParserId.Length > 0 && request.ParserVersion.Length == 0)
        {
            // Sürümsüz sabitleme, "en güncel parser" demek ve replay'i
            // tekrarlanamaz kılar: aynı komut iki ay sonra farklı sonuç verir.
            return Results.BadRequest(new ErrorResponse(
                "'parser_id' verildiyse 'parser_version' da zorunlu — replay tekrarlanabilir olmalı.",
                "Sürümü sabitlemek replay'i tekrarlanabilir kılıyor."));
        }

        var plan = new ReplayPlan
        {
            From = request.From,
            To = request.To,
            ParserId = request.ParserId,
            ParserVersion = request.ParserVersion,
            OwnerGroups = request.OwnerGroups,
            SourceIds = request.SourceIds,
            ContinueOnMissingObjects = request.ContinueOnMissingObjects,
            AllowOpenPartition = request.AllowOpenPartition,
        };

        ReplayReport report;
        try
        {
            report = request.DryRun
                ? await engine.DryRunAsync(plan, cancellationToken)
                : await engine.ApplyAsync(plan, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // Katalogda olmayan ya da sürümü uyuşmayan parser.
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        // Eksik nesne varken uygulanmadıysa bu bir hata değil, bir DURUŞ:
        // kullanıcı devam edip etmeyeceğine karar vermeli.
        if (report.HasMissingObjects && !report.Applied && !request.DryRun)
        {
            return Results.Conflict(new ReplayBlockedResponse(
                "Manifest'teki bazı nesneler arşivde bulunamadı; replay eksik veri üretirdi.",
                report.MissingObjects,
                "Devam etmek için continue_on_missing_objects: true gönderin."));
        }

        return Results.Ok(ReplayResponse.From(report));
    }
}
