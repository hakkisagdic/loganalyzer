using Bizigo.Authoring;
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
            .WithName("ListParserDrafts");

        // `Produces<T>` yalnızca belge süsü değil: T14'ün ürettiği TypeScript'te
        // gövde `unknown` kalmasın diye. Bu üçünün tüketicisi T19'un editörü;
        // okuma uçları ve yayın/geri alma T20'nin ekranıyla birlikte tipleniyor.
        group.MapPost("/", CreateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateParserDraft")
            .Produces<ParserDraftResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("UpdateParserDraft")
            .Produces<ParserDraftResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        // 422 de aynı tipi taşıyor: kapıdan geçemeyen taslağın cevabı, geçenle
        // aynı gövde — farkı `gate.ok`. Ayrı bir hata tipi, ekranı aynı bilgiyi
        // iki kez ele almaya zorlardı.
        group.MapPost("/{id:guid}/submit", SubmitAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("SubmitParserDraft")
            .Produces<ParserDraftResponse>()
            .Produces<ParserDraftResponse>(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{id:guid}/return", ReturnAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("ReturnParserDraft");

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("PublishParserDraft");

        routes.MapPost("/v1/parsers/{parserId}/rollback", RollbackAsync)
            .RequireAuthorization(BizigoAuthPolicies.Admin)
            .WithName("RollbackParser")
            .WithTags("parsers");

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
            .Select(p => new
            {
                id = p.Id,
                parser_id = p.ParserId,
                version = p.Version,
                state = p.State.ToString(),
                owner = p.Owner,
                passing_tests = p.PassingTests,
                quarantined = p.Quarantined,
                created_at = p.CreatedAt,
                published_at = p.PublishedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(new { count = rows.Count, drafts = rows });
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
            return Results.BadRequest(new { error = "'yaml' boş olamaz." });
        }

        var result = await authoring.SaveDraftAsync(
            id, request.Yaml, user.Scope.Subject, cancellationToken);

        // Kaydetme kapıya TAKILMIYOR — taslak bozuk hâlde de saklanabilmeli,
        // yoksa yarım kalmış bir parser kaydedilemez ve kullanıcı işini
        // kaybeder. Kapı kararı yine de gövdede (`gate`): editör "şu an
        // yayınlanabilir miyim" sorusunu ikinci bir istek atmadan cevaplıyor.
        return result.Ok
            ? Results.Ok(ParserDraftResponse.From(result))
            : Results.BadRequest(new { error = result.Error });
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
            ? Results.Ok(ParserDraftResponse.From(result))
            : Results.UnprocessableEntity(ParserDraftResponse.From(result));
    }

    private static async Task<IResult> ReturnAsync(
        Guid id,
        ParserAuthoringService authoring,
        CancellationToken cancellationToken)
    {
        var result = await authoring.ReturnToDraftAsync(id, cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result))
            : Results.BadRequest(new { error = result.Error });
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

        return Results.Ok(new
        {
            draft = Describe(result),
            catalog = new
            {
                loaded = report.Loaded,
                from_repository = report.FromRepository,
                from_database = report.FromDatabase,
                shadowed = report.Shadowed,
                errors = report.Errors,
            },
        });
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
            return Results.BadRequest(new { error = result.Error });
        }

        var report = await loader.LoadAsync(ParserDirectory(configuration), cancellationToken);

        return Results.Ok(new
        {
            draft = Describe(result),
            catalog = new { loaded = report.Loaded, shadowed = report.Shadowed },
        });
    }

    private static string ParserDirectory(IConfiguration configuration) =>
        configuration["Parsing:ParserDirectory"] ?? "catalog/parsers";

    private static object Describe(AuthoringResult result) => new
    {
        id = result.Draft?.Id,
        parser_id = result.Draft?.ParserId,
        version = result.Draft?.Version,
        state = result.Draft?.State.ToString(),
        error = result.Error,
        verdict = result.Verdict is null ? null : new
        {
            ok = result.Verdict.Ok,
            passing_tests = result.Verdict.PassingTests,
            errors = result.Verdict.Errors,
            warnings = result.Verdict.Warnings,
        },
    };
}
