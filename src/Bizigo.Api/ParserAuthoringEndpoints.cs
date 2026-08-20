using Bizigo.Authoring;
using Bizigo.Parsing.Dispatch;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api;

public sealed record ParserDraftRequest(string Yaml);

/// <summary>
/// Parser yazarlık yüzeyi (T18, K33).
///
/// <para>
/// <b>Rol ayrımı burada:</b> taslak yazmak <c>author</c>, yayınlamak ve geri
/// almak <c>admin</c> istiyor. K16'daki 50 kişilik kurumda herkesin katkı
/// yapabilmesi ama yayının sınırlı kalması bu ayrımla sağlanıyor — yayın,
/// çalışan boru hattının davranışını anında değiştiriyor.
/// </para>
/// </summary>
public static class ParserAuthoringEndpoints
{
    public static IEndpointRouteBuilder MapParserAuthoring(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/parsers/drafts").WithTags("parsers");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("ListParserDrafts")
            .Produces<ParserDraftListResponse>();

        // T20'nin fark görünümü: taslağın gövdesi ve YAYINDAKİ sürümün gövdesi
        // AYNI yanıtta. İki ayrı istekle çekmek, inceleme sırasında yayın
        // değişirse kullanıcının olmayan bir farka bakması demekti.
        group.MapGet("/{id:guid}", DetailAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("GetParserDraft")
            .Produces<ParserDraftDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateParserDraft");

        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("UpdateParserDraft");

        group.MapPost("/{id:guid}/submit", SubmitAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("SubmitParserDraft");

        group.MapPost("/{id:guid}/return", ReturnAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("ReturnParserDraft")
            .Produces<ParserAuthoringResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("PublishParserDraft")
            .Produces<ParserPublishResponse>()
            .Produces<ParserAuthoringResponse>(StatusCodes.Status422UnprocessableEntity);

        routes.MapPost("/v1/parsers/{parserId}/rollback", RollbackAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("RollbackParser")
            .WithTags("parsers")
            .Produces<ParserPublishResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        // Altın örnek kapsamı (T20 kabul kriteri). Ölçüm katalog anlık
        // görüntüsüne bağlı önbellekten geliyor; `force` ekrandaki "yeniden
        // ölç" düğmesi için — örnek DOSYALARI katalog değişmeden de
        // düzenlenebiliyor ve o durumda anlık görüntü aynı kalıyor.
        routes.MapGet("/v1/parsers/coverage", CoverageAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ParserCoverage")
            .WithTags("parsers")
            .Produces<CatalogCoverageResponse>();

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IDbContextFactory<ControlPlaneDbContext> factory,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Parsers
            .AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ParserDraftListResponse(rows.Count, [.. rows.Select(Describe)]));
    }

    /// <summary>
    /// Taslağın gövdesi, yayındaki sürümün gövdesi ve kapı kararı.
    ///
    /// <para>
    /// Fark <b>istemcide</b> alınıyor: sunucuda satır satır karşılaştırma
    /// yapmak, gövdeyi zaten iki kez göndermişken üçüncü bir temsil üretirdi.
    /// </para>
    /// </summary>
    private static async Task<IResult> DetailAsync(
        Guid id,
        IDbContextFactory<ControlPlaneDbContext> factory,
        ParserPublishGate gate,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var draft = await db.Parsers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (draft is null)
        {
            return Results.NotFound();
        }

        // Karşılaştırma tabanı: aynı parser_id için HÂLEN YAYINDA olan sürüm.
        // "Bir önceki kayıt" değil — inceleyenin sorusu "yayına ne girecek",
        // yani karşılaştırılacak şey yayındaki.
        var published = await db.Parsers.AsNoTracking()
            .Where(p => p.ParserId == draft.ParserId
                && p.State == ParserState.Published
                && p.Id != draft.Id)
            .OrderByDescending(p => p.PublishedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // Kapı yayın anında yeniden koşuyor; inceleyenin gördüğü karar da
        // taze olmalı — kaydedildiği andaki karar aradan geçen sürede
        // değişmiş olabilir (pattern kütüphanesi, eşleme tabloları).
        var verdict = gate.Inspect(draft.Yaml, draft.ParserId);

        return Results.Ok(new ParserDraftDetailResponse(
            draft.Id,
            draft.ParserId,
            draft.Version,
            draft.State.ToString().ToLowerInvariant(),
            draft.Owner,
            draft.Yaml,
            Describe(verdict),
            draft.UpdatedAt,

            // T20'nin fark görünümü için; T19 bunları yok sayıyor.
            published?.Version,
            published?.Yaml));
    }

    private static IResult CoverageAsync(
        CatalogCoverageCache cache,
        Dispatcher dispatcher,
        IConfiguration configuration,
        bool? force)
    {
        var coverage = cache.Measure(
            configuration["Parsing:CatalogDirectory"] ?? "catalog",
            dispatcher,
            force ?? false);

        return Results.Ok(new CatalogCoverageResponse(
            coverage.Ok,
            coverage.Partial,
            coverage.Failed,
            coverage.Total,
            coverage.MeasuredAt,

            // Ölçüm katalogdan eskiyse bunu SÖYLÜYORUZ. Bayat bir oranı taze
            // gibi göstermek, ekranın tek sayısal göstergesini işe yaramaz kılar.
            cache.IsStale,
            [.. coverage.ByParser.Select(p => new ParserCoverageEntryResponse(p.ParserId, p.Wins))]));
    }

    private static Task<IResult> CreateAsync(
        ParserDraftRequest request,
        ParserAuthoringService authoring,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(null, request, authoring, user, cancellationToken);

    private static Task<IResult> UpdateAsync(
        Guid id,
        ParserDraftRequest request,
        ParserAuthoringService authoring,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveAsync(id, request, authoring, user, cancellationToken);

    private static async Task<IResult> SaveAsync(
        Guid? id,
        ParserDraftRequest request,
        ParserAuthoringService authoring,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Yaml))
        {
            return Results.BadRequest(new ErrorResponse("'yaml' boş olamaz."));
        }

        var result = await authoring.SaveDraftAsync(
            id, request.Yaml, user.Scope.Subject, cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result))
            : Results.BadRequest(new ErrorResponse(result.Error));
    }

    private static async Task<IResult> SubmitAsync(
        Guid id,
        ParserAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        var result = await authoring.SubmitForReviewAsync(id, cancellationToken);

        // Kapıdan geçemeyen taslak 422: istek biçimsel olarak doğru, içeriği
        // kabul edilebilir değil. 400 bunu "yanlış yazdın" gibi gösterirdi.
        return result.Ok
            ? Results.Ok(Describe(result))
            : Results.UnprocessableEntity(Describe(result));
    }

    private static async Task<IResult> ReturnAsync(
        Guid id,
        ParserAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        var result = await authoring.ReturnToDraftAsync(id, cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result))
            : Results.BadRequest(new ErrorResponse(result.Error));
    }

    private static async Task<IResult> PublishAsync(
        Guid id,
        ParserAuthoringService authoring,
        PublishedParserLoader loader,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await authoring.PublishAsync(id, cancellationToken);

        if (!result.Ok)
        {
            return Results.UnprocessableEntity(Describe(result));
        }

        // Yayın sonrası katalog HEMEN tazeleniyor: periyodik tazelemeyi beklemek,
        // kullanıcıya "yayınlandı" deyip davranışın dakikalarca değişmemesi olurdu.
        var report = await loader.LoadAsync(ParserDirectory(configuration), cancellationToken);

        return Results.Ok(new ParserPublishResponse(Describe(result), Describe(report)));
    }

    private static async Task<IResult> RollbackAsync(
        string parserId,
        ParserAuthoringService authoring,
        PublishedParserLoader loader,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await authoring.RollbackAsync(parserId, cancellationToken);

        if (!result.Ok)
        {
            return Results.BadRequest(new ErrorResponse(result.Error));
        }

        var report = await loader.LoadAsync(ParserDirectory(configuration), cancellationToken);

        return Results.Ok(new ParserPublishResponse(Describe(result), Describe(report)));
    }

    private static string ParserDirectory(IConfiguration configuration) =>
        configuration["Parsing:ParserDirectory"] ?? "catalog/parsers";

    private static ParserAuthoringResponse Describe(AuthoringResult result) => new(
        result.Draft?.Id,
        result.Draft?.ParserId,
        result.Draft?.Version,
        result.Draft?.State.ToString().ToLowerInvariant(),
        result.Error,
        result.Verdict is null ? null : Describe(result.Verdict));

    private static PublishVerdictResponse Describe(PublishVerdict verdict) => new(
        verdict.Ok,
        verdict.PassingTests,
        [.. verdict.Errors],
        [.. verdict.Warnings]);

    private static ParserDraftResponse Describe(ParserEntity parser) => new(
        parser.Id,
        parser.ParserId,
        parser.Version,
        parser.Vendor,
        parser.Product,
        parser.State.ToString().ToLowerInvariant(),
        parser.Owner,
        parser.PassingTests,
        parser.Quarantined,
        parser.CreatedAt,
        parser.PublishedAt);

    private static CatalogReloadResponse Describe(CatalogSourceReport report) => new(
        report.Loaded,
        report.FromRepository,
        report.FromDatabase,
        [.. report.Shadowed],
        [.. report.Errors]);
}
