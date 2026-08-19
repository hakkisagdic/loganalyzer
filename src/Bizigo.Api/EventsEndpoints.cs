using System.Globalization;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.Raw;

namespace Bizigo.Api;

/// <param name="Field">Filtrelenecek alan adı.</param>
/// <param name="Op">Operatör: <c>eq|ne|in|gt|lt|contains|startswith</c>.</param>
/// <param name="Values">Değerler. <c>in</c> dışında tek eleman beklenir.</param>
public sealed record FieldFilterRequest(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("values")] IReadOnlyList<string> Values);

/// <summary>
/// Arama isteği.
///
/// <para>
/// Alan adları açıkça <c>snake_case</c>. Varsayılan politika camelCase kabul
/// ediyordu ama yanıttaki imleç <c>after_timestamp</c> adıyla dönüyor: ekran
/// aldığı imleci <b>olduğu gibi</b> geri gönderemiyordu ve yarım imleç sessizce
/// ilk sayfayı tekrarlıyordu. İki adlandırmayı yan yana taşımak yerine tek
/// tarafa çekildi (bkz. <see cref="EventResponse"/>).
/// </para>
/// </summary>
public sealed record EventSearchRequest
{
    [JsonPropertyName("from")]
    public DateTimeOffset? From { get; init; }

    [JsonPropertyName("to")]
    public DateTimeOffset? To { get; init; }

    [JsonPropertyName("full_text")]
    public string? FullText { get; init; }

    [JsonPropertyName("filters")]
    public IReadOnlyList<FieldFilterRequest> Filters { get; init; } = [];

    [JsonPropertyName("owner_groups")]
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];

    [JsonPropertyName("source_ids")]
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    [JsonPropertyName("parse_statuses")]
    public IReadOnlyList<string> ParseStatuses { get; init; } = [];

    /// <summary>Keyset imleci: önceki sayfanın son <c>ts</c> + <c>event_id</c> değeri.</summary>
    [JsonPropertyName("after_timestamp")]
    public DateTimeOffset? AfterTimestamp { get; init; }

    [JsonPropertyName("after_event_id")]
    public Guid? AfterEventId { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 200;

    [JsonPropertyName("ascending")]
    public bool Ascending { get; init; }
}

/// <summary>
/// Olay arama ve ham iniş uçları (F1 §10.2, T10).
///
/// <para>
/// <b>Serbest SQL kabul eden hiçbir uç yok</b> ve bu tartışmaya açık değil:
/// K17'nin tek zorlama noktası sorgu API'si. Ham SQL açılırsa kapsam ayrımı arka
/// kapıdan delinir. Filtreler bu yüzden alan/operatör/değer üçlüsü olarak
/// alınıyor ve bilinmeyen operatör <b>reddediliyor</b>.
/// </para>
/// </summary>
public static class EventsEndpoints
{
    /// <summary>
    /// Tek sayfada dönebilecek en fazla satır. Sınırsız bırakmak, tek bir
    /// isteğin ClickHouse'u ve belleği doyurmasına izin vermek olurdu.
    /// </summary>
    private const int MaxLimit = 1000;

    /// <summary>Zaman aralığı verilmezse bakılan pencere.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    public static IEndpointRouteBuilder MapEvents(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/events")
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithTags("events");

        // `Produces<T>` yalnızca belge süsü değil: T14'ün ürettiği TypeScript'te
        // gövde tipi buradan doğuyor. Bildirilmezse ekran `unknown` alıyor ve
        // tipi elle yazmak zorunda kalıyor — T14'ün önlemek için var olduğu şey.
        // `ProducesContractTests` bunu `/v1/*` altındaki her uç için zorluyor.
        group.MapPost("/search", SearchAsync)
            .WithName("SearchEvents")
            .Produces<EventSearchResponse>();

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetEvent")
            .Produces<EventDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/raw", GetRawAsync)
            .WithName("GetEventRaw")
            .Produces<EventRawResponse>()
            .Produces(StatusCodes.Status404NotFound);

        return routes;
    }

    /// <summary>
    /// Arama <c>POST</c> — filtre gövdesi sorgu dizesine sığmıyor ve tam metin
    /// terimlerinin URL'de günlüklenmesi istenmiyor.
    /// </summary>
    private static async Task<IResult> SearchAsync(
        EventSearchRequest request,
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        // Boş kapsam "hiçbir şey" demek. 403 dönmek yerine boş sayfa dönmek,
        // "yetkin yok" ile "sonuç yok"u karıştırırdı.
        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        if (!TryBuild(request, out var eventQuery, out var error))
        {
            return Results.BadRequest(new { error });
        }

        var page = await query.SearchEventsAsync(eventQuery, scope, cancellationToken);

        return Results.Ok(new EventSearchResponse(
            [.. page.Events.Select(EventResponse.From)],
            page.Next is null ? null : new EventCursorResponse(page.Next.Timestamp, page.Next.EventId),
            page.HasMore));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        var found = await query.GetEventAsync(id, scope, cancellationToken);

        // Kapsam dışı olay da 404: 403 dönmek "böyle bir olay var ama göremezsin"
        // bilgisini sızdırırdı.
        if (found is null)
        {
            return Results.NotFound();
        }

        // İki görünüm de aynı istekte. Sekme değiştirmek yeni bir istek
        // gerektirseydi kullanıcı boş ekran görür, üç ayrı hata durumu ele
        // alınırdı.
        var ocsf = await query.GetEventViewAsync(id, EventViewKind.Ocsf, scope, cancellationToken);
        var otel = await query.GetEventViewAsync(id, EventViewKind.Otel, scope, cancellationToken);

        return Results.Ok(new EventDetailResponse(
            EventResponse.From(found),
            [.. ocsf.Select(f => new EventFieldResponse(f.Name, f.Value))],
            [.. otel.Select(f => new EventFieldResponse(f.Name, f.Value))]));
    }

    /// <summary>
    /// Ham bayta iniş. Kapsam <b>iki kez</b> kontrol ediliyor: olay okunurken
    /// (kapsam dışıysa olay zaten dönmüyor) ve nesne indirilmeden önce anahtardaki
    /// <c>owner_group</c> üzerinden.
    /// </summary>
    private static async Task<IResult> GetRawAsync(
        Guid id,
        IScopedQuery query,
        RawEventLocator locator,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        var found = await query.GetEventAsync(id, scope, cancellationToken);
        if (found is null)
        {
            return Results.NotFound();
        }

        RawLookup? lookup;
        try
        {
            lookup = await locator.FindAsync(found, scope, cancellationToken);
        }
        catch (RawAccessDeniedException)
        {
            return Results.Forbid();
        }

        if (lookup is null)
        {
            // Arşivde yok: yükleyici geride kalmış ya da manifest ile arşiv
            // ayrışmış olabilir. İkisini de sessiz bırakmıyoruz.
            return Results.NotFound(new
            {
                error = "Ham kayıt arşivde bulunamadı.",
                raw_ref = found.RawRef,
                hint = "Nesne henüz yüklenmemiş olabilir; /v1/health/pipeline arşiv gecikmesini gösterir.",
            });
        }

        // Tespit edilen kodlama olay satırından geliyor, manifestten değil:
        // ekranda "envanter ne diyor" ile "baytlara bakınca ne çıktı" yan yana
        // durmadan windows-1254 bir satırın doğru çözülüp çözülmediği görülemiyor.
        return Results.Ok(EventRawResponse.From(lookup, found.EncodingDetected));
    }

    private static bool TryBuild(EventSearchRequest request, out EventQuery query, out string error)
    {
        query = null!;
        error = string.Empty;

        var to = request.To ?? DateTimeOffset.UtcNow;
        var from = request.From ?? to - DefaultWindow;

        if (from >= to)
        {
            error = "'from' değeri 'to' değerinden küçük olmalı.";
            return false;
        }

        if (request.Limit is < 1 or > MaxLimit)
        {
            error = string.Create(CultureInfo.InvariantCulture, $"'limit' 1 ile {MaxLimit} arasında olmalı.");
            return false;
        }

        var filters = new List<FieldFilter>(request.Filters.Count);
        foreach (var filter in request.Filters)
        {
            if (!TryParseOperator(filter.Op, out var op))
            {
                error = $"Bilinmeyen operatör: '{filter.Op}'.";
                return false;
            }

            if (filter.Values.Count == 0)
            {
                error = $"'{filter.Field}' filtresi değersiz.";
                return false;
            }

            filters.Add(new FieldFilter(filter.Field, op, filter.Values));
        }

        var statuses = new List<ParseStatus>(request.ParseStatuses.Count);
        foreach (var status in request.ParseStatuses)
        {
            if (!Enum.TryParse<ParseStatus>(status, ignoreCase: true, out var parsed))
            {
                error = $"Bilinmeyen parse_status: '{status}'.";
                return false;
            }

            statuses.Add(parsed);
        }

        EventCursor? cursor = null;
        if (request.AfterTimestamp is { } ts && request.AfterEventId is { } eventId)
        {
            cursor = new EventCursor(ts, eventId);
        }
        else if (request.AfterTimestamp is not null || request.AfterEventId is not null)
        {
            // Yarım imleç sessizce baştan başlatırdı; kullanıcı sayfaladığını
            // sanarken aynı sayfayı döner.
            error = "'after_timestamp' ve 'after_event_id' birlikte verilmeli.";
            return false;
        }

        query = new EventQuery
        {
            From = from,
            To = to,
            FullText = request.FullText,
            Filters = filters,
            OwnerGroups = request.OwnerGroups,
            SourceIds = request.SourceIds,
            ParseStatuses = statuses,
            After = cursor,
            Limit = request.Limit,
            Ascending = request.Ascending,
        };

        return true;
    }

    /// <summary>
    /// Operatör adları <b>beyaz listeden</b> çözülüyor. <c>Enum.TryParse</c> ile
    /// açmak, ileride eklenen bir operatörü istemeden API yüzeyine taşırdı.
    /// </summary>
    private static bool TryParseOperator(string op, out FilterOperator parsed)
    {
        (var found, parsed) = op switch
        {
            "eq" => (true, FilterOperator.Equals),
            "ne" => (true, FilterOperator.NotEquals),
            "in" => (true, FilterOperator.In),
            "gt" => (true, FilterOperator.GreaterThan),
            "lt" => (true, FilterOperator.LessThan),
            "contains" => (true, FilterOperator.Contains),
            "startswith" => (true, FilterOperator.StartsWith),
            _ => (false, default),
        };

        return found;
    }
}
