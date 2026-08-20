using System.Text.Json.Serialization;
using Bizigo.Authoring;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;

namespace Bizigo.Api;

/// <summary>
/// <c>POST /v1/parsers/try</c> isteği.
///
/// <para>
/// <c>JsonPropertyName</c> <b>zorunlu</b>: varsayılan camelCase politikası
/// <c>parser_id</c>'yi <c>parserId</c> yapıyordu ve API'nin geri kalanı
/// (<c>/auth/me</c>, olay uçları, ClickHouse kolonları) <c>snake_case</c>.
/// Aynı yüzeyde iki adlandırma, ekranın hangisini göndereceğini her seferinde
/// tahmin etmesi demek — bu ucun ilk tüketicisi T19 olduğu için borç
/// büyümeden kapatıldı.
/// </para>
/// </summary>
/// <param name="Line">
/// Denenecek ham satır. <see cref="Yaml"/> verildiyse boş bırakılabilir —
/// o zaman yalnızca kapılar ve gömülü testler koşuyor, ki editörde satır
/// yazılmadan önceki hâl tam olarak budur.
/// </param>
/// <param name="ParserId">
/// Boşsa dispatcher karar verir — yani "bu satır kataloğa düşer mi" sorusu.
/// Doluysa o parser <b>zorlanır</b>: "neden düşmedi" sorusunun cevabı ancak
/// böyle alınabilir, çünkü dispatcher elenen adayın gerekçesini taşımıyor.
/// </param>
/// <param name="Yaml">
/// Henüz yayınlanmamış <b>taslak</b> parser'ın kendisi (T19).
///
/// <para>
/// Ticket'ın taşıyıcı fikri: parser yayınlanmadan önce denenebilmeli. Katalogda
/// olmayan bir parser'ı denemenin başka yolu yok — taslağı önce yayınlamak,
/// tam olarak kaçınılmak istenen şey.
/// </para>
///
/// <para>
/// Derleme <b>ad-hoc</b>: <see cref="ParserCatalog"/>'a dokunulmuyor, hiçbir
/// şey yazılmıyor, çalışan boru hattı bu istekten etkilenmiyor. Ucun güvenli
/// olmasının tek sebebi bu ve testle sabitlendi.
/// </para>
/// </param>
public sealed record ParserTryRequest(
    [property: JsonPropertyName("line")] string Line = "",
    [property: JsonPropertyName("parser_id")] string ParserId = "",
    [property: JsonPropertyName("yaml")] string Yaml = "");

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
            .WithName("TryParser")
            .Produces<ParserTryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status404NotFound);

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
    /// Bir satırı kataloğa <b>ya da henüz yayınlanmamış bir taslağa</b> karşı
    /// dener. Ne yazar ne kataloğa dokunur — F2'deki parser editörünün
    /// önizlemesi ve "bu satır neden ayrıştırılamadı" sorusunun cevabı buradan
    /// geliyor.
    ///
    /// <para>
    /// Üç mod var ve seçim <b>isteğin içeriğinden</b> çıkıyor:
    /// <c>yaml</c> doluysa taslak, <c>parser_id</c> doluysa zorlanmış katalog
    /// parser'ı, ikisi de boşsa dispatcher. Ayrı bir <c>mode</c> alanı istemek,
    /// içerikle çelişebilen ikinci bir gerçek kaynak açardı.
    /// </para>
    /// </summary>
    private static IResult Try(
        ParserTryRequest request,
        ParserCatalog catalog,
        Dispatcher dispatcher,
        ParserPublishGate gate)
    {
        var line = request.Line ?? string.Empty;
        var yaml = request.Yaml ?? string.Empty;
        var parserId = request.ParserId ?? string.Empty;

        if (line.Length == 0 && yaml.Length == 0)
        {
            return Results.BadRequest(new ErrorResponse(
                "'line' ya da 'yaml' verilmeli.",
                "Satır olmadan yalnızca taslak kapıları koşturulabilir; ikisi birden boşsa denenecek bir şey yok."));
        }

        if (yaml.Length > 0)
        {
            return Results.Ok(TryDraft(yaml, line, gate, dispatcher));
        }

        if (parserId.Length > 0)
        {
            if (!catalog.Current.ByParserId.TryGetValue(parserId, out var parser))
            {
                return Results.NotFound(new ErrorResponse($"'{parserId}' kataloğda yok."));
            }

            return Results.Ok(new ParserTryResponse(
                "forced",
                ParseOutcomeResponse.From(parser.Parse(line)),
                Draft: null,
                Dispatch: null));
        }

        return Results.Ok(new ParserTryResponse(
            "dispatch",
            Result: null,
            Draft: null,
            // Hangi kademenin karar verdiği, sonucun kendisi kadar bilgilendirici:
            // envanter bağı yerine literal filtreye düşmüş bir satır, parser
            // yanlış olmasa bile envanterin eksik olduğunu söylüyor.
            Dispatch: ParserDispatchResponse.From(dispatcher.Dispatch(line, boundParserId: null))));
    }

    /// <summary>
    /// Taslağı <b>yayın kapısının kendisiyle</b> denetleyip örnek satırı kapının
    /// onayladığı derlemeyle koşturuyor.
    ///
    /// <para>
    /// Kapıyı burada tekrar yazmak — "editör için hafif bir lint" — iki ayrı
    /// denetleyici demek olurdu: editörde yeşil yanan bir taslak yayında
    /// reddedilir ve kullanıcı hangisine inanacağını bilemez. T18 kapıları
    /// kurdu; buranın işi onları <b>okunur</b> kılmak, ikinci bir tanesini
    /// yazmak değil.
    /// </para>
    ///
    /// <para>
    /// Dispatcher <b>yine de</b> koşuyor: aynı satırın bugünkü katalogda ne
    /// yaptığı, taslağın ne yaptığı kadar bilgilendirici. "Taslağım çözüyor ama
    /// canlıda satır düşüyor" ile "zaten başka bir parser çözüyor" bambaşka iki
    /// durum ve ikisi de yalnızca yan yana bakınca görülüyor.
    /// </para>
    /// </summary>
    private static ParserTryResponse TryDraft(
        string yaml,
        string line,
        ParserPublishGate gate,
        Dispatcher dispatcher)
    {
        var verdict = gate.Inspect(yaml);

        return new ParserTryResponse(
            "draft",
            verdict.Compiled is { } compiled && line.Length > 0
                ? ParseOutcomeResponse.From(compiled.Parse(line))
                : null,
            PublishVerdictResponse.From(verdict),
            line.Length > 0
                ? ParserDispatchResponse.From(dispatcher.Dispatch(line, boundParserId: null))
                : null);
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
}
