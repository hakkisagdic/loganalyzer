using Bizigo.Replay;

namespace Bizigo.Api;

public sealed record ReplayRequest
{
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }

    public string ParserId { get; init; } = string.Empty;
    public string ParserVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>Varsayılan <see langword="true"/>: yazma <b>açıkça</b> istenmeli.</summary>
    public bool DryRun { get; init; } = true;

    public bool ContinueOnMissingObjects { get; init; }
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
            .WithTags("replay");

        return routes;
    }

    private static async Task<IResult> HandleAsync(
        ReplayRequest request,
        ReplayEngine engine,
        CancellationToken cancellationToken)
    {
        if (request.From >= request.To)
        {
            return Results.BadRequest(new { error = "'from' değeri 'to' değerinden küçük olmalı." });
        }

        if (request.ParserId.Length > 0 && request.ParserVersion.Length == 0)
        {
            // Sürümsüz sabitleme, "en güncel parser" demek ve replay'i
            // tekrarlanamaz kılar: aynı komut iki ay sonra farklı sonuç verir.
            return Results.BadRequest(new
            {
                error = "'parserId' verildiyse 'parserVersion' da zorunlu — replay tekrarlanabilir olmalı.",
            });
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
            return Results.BadRequest(new { error = ex.Message });
        }

        // Eksik nesne varken uygulanmadıysa bu bir hata değil, bir DURUŞ:
        // kullanıcı devam edip etmeyeceğine karar vermeli.
        if (report.HasMissingObjects && !report.Applied && !request.DryRun)
        {
            return Results.Conflict(new
            {
                error = "Manifest'teki bazı nesneler arşivde bulunamadı; replay eksik veri üretirdi.",
                missing_objects = report.MissingObjects,
                hint = "Devam etmek için continueOnMissingObjects: true gönderin.",
            });
        }

        return Results.Ok(report);
    }
}
