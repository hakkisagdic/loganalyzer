using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;

namespace Bizigo.Api;

/// <param name="Line">Denenecek ham satır.</param>
/// <param name="ParserId">
/// Boşsa dispatcher karar verir — yani "bu satır kataloğa düşer mi" sorusu.
/// Doluysa o parser <b>zorlanır</b>: "neden düşmedi" sorusunun cevabı ancak
/// böyle alınabilir, çünkü dispatcher elenen adayın gerekçesini taşımıyor.
/// </param>
public sealed record ParserTryRequest(string Line, string ParserId = "");

/// <summary>
/// Parser kataloğunun okuma yüzeyi (T10).
///
/// <para>
/// <b>Yazma yok — bilinçli.</b> Katalog bu fazda repodan geliyor ve sıcak
/// yeniden yükleme atomik (<see cref="ParserCatalog"/>). Uçtan parser
/// yayınlamak, gözden geçirme ve sürümleme akışı olmadan kataloğu tek bir
/// isteğin bozabileceği bir yere çevirirdi; taslak→inceleme→yayın F2'nin işi.
/// </para>
/// </summary>
public static class ParsersEndpoints
{
    public static IEndpointRouteBuilder MapParsers(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/parsers").WithTags("parsers");

        // Katalog veri değil, yapılandırma: kapsam filtresi uygulanmıyor.
        // Bir ekibin hangi parser'ların var olduğunu görmesi kimsenin logunu
        // görmesi anlamına gelmiyor.
        group.MapGet("/", List)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListParsers")
            .Produces<ParserListResponse>();

        group.MapGet("/{id}", Get)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("GetParser")
            .Produces<ParserDetailResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

        group.MapPost("/try", Try)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("TryParser");

        return routes;
    }

    private static IResult List(ParserCatalog catalog)
    {
        var snapshot = catalog.Current;

        var parsers = snapshot.Parsers
            .OrderBy(static p => p.Id, StringComparer.Ordinal)
            .Select(Summarize)
            .ToArray();

        // Katalog geneli GROK003. Ekranın uyarıyı gösterebilmesi için parser
        // başına detay çekmesi gerekseydi katalog açılışı N istek atardı — ve
        // çoğu ekran bunu yapmayıp uyarıyı hiç göstermezdi.
        return Results.Ok(new ParserListResponse(
            parsers.Length,
            parsers,
            parsers.Sum(static p => p.BacktrackingGroks)));
    }

    private static IResult Get(string id, ParserCatalog catalog) =>
        catalog.Current.ByParserId.TryGetValue(id, out var parser)
            ? Results.Ok(Detail(parser))
            : Results.NotFound(new ErrorResponse($"'{id}' kataloğda yok."));

    /// <summary>
    /// Bir satırı kataloğa karşı dener. Ne yazar ne okur — F2'deki parser
    /// editörünün önizlemesi ve "bu satır neden ayrıştırılamadı" sorusunun
    /// cevabı buradan geliyor.
    /// </summary>
    private static IResult Try(ParserTryRequest request, ParserCatalog catalog, Dispatcher dispatcher)
    {
        if (string.IsNullOrEmpty(request.Line))
        {
            return Results.BadRequest(new { error = "'line' boş olamaz." });
        }

        if (request.ParserId.Length > 0)
        {
            if (!catalog.Current.ByParserId.TryGetValue(request.ParserId, out var parser))
            {
                return Results.NotFound(new { error = $"'{request.ParserId}' kataloğda yok." });
            }

            return Results.Ok(new
            {
                dispatched = false,
                result = Describe(parser.Parse(request.Line)),
            });
        }

        var dispatch = dispatcher.Dispatch(request.Line, boundParserId: null);

        return Results.Ok(new
        {
            dispatched = true,
            // Hangi kademenin karar verdiği, sonucun kendisi kadar bilgilendirici:
            // envanter bağı yerine literal filtreye düşmüş bir satır, parser
            // yanlış olmasa bile envanterin eksik olduğunu söylüyor.
            tier = dispatch.Tier.ToString(),
            attempts = dispatch.Attempts,
            result = Describe(dispatch.Result),
        });
    }

    private static ParserSummaryResponse Summarize(CompiledParser parser)
    {
        var metadata = parser.Definition.Metadata;

        return new ParserSummaryResponse(
            metadata.Id,
            metadata.Version,
            metadata.Vendor,
            metadata.Product,
            metadata.Description,
            metadata.License,
            metadata.Specificity,
            parser.Groks.Count(static g => !g.IsLinearTime));
    }

    private static ParserDetailResponse Detail(CompiledParser parser)
    {
        var groks = parser.Groks.ToArray();
        var match = parser.Definition.Match;

        return new ParserDetailResponse(
            Summarize(parser),
            new ParserMatchResponse([.. match.Transport], [.. match.Contains], match.SourceLabels),
            parser.Steps.Count,
            new ParserGrokResponse(
                groks.Length,
                // Geri izlemeye düşen ifade `MatchTimeout` ödüyor ve o duvar
                // saatini ölçüyor: yüklü makinede sağlıklı bir satır `failed`
                // olabiliyor. Katalog bugün sıfır veriyor ve öyle kalmalı.
                groks.Count(static g => !g.IsLinearTime),
                [.. groks
                    .Where(static g => !g.IsLinearTime)
                    .Select(static g => g.FallbackReason)
                    .Where(static reason => !string.IsNullOrEmpty(reason))
                    .Select(static reason => reason!)]));
    }

    private static object Describe(ParseResult result) => new
    {
        parser_id = result.ParserId,
        parser_version = result.ParserVersion,
        status = result.Status.ToString(),
        // Sıfırdan farklıysa sonuç "uymadı" değil "ölçülemedi" demektir;
        // ikisini karıştırmak sağlıklı bir parser'ı karantinaya sokar.
        timed_out = result.TimedOut,
        timestamp = result.Timestamp,
        tags = result.Tags,
        fields = result.Fields,
        core = result.Core,
        ocsf = result.Ocsf,
        otel = result.Otel,
        issues = result.Issues.Select(static i => new { step = i.Step, message = i.Message }),
    };
}
