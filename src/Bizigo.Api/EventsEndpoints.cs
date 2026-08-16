using System.Globalization;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.Raw;

namespace Bizigo.Api;

/// <param name="Field">Filtrelenecek alan adı.</param>
/// <param name="Op">Operatör: <c>eq|ne|in|gt|lt|contains|startswith</c>.</param>
/// <param name="Values">Değerler. <c>in</c> dışında tek eleman beklenir.</param>
public sealed record FieldFilterRequest(string Field, string Op, IReadOnlyList<string> Values);

public sealed record EventSearchRequest
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? FullText { get; init; }
    public IReadOnlyList<FieldFilterRequest> Filters { get; init; } = [];
    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public IReadOnlyList<string> ParseStatuses { get; init; } = [];

    /// <summary>Keyset imleci: önceki sayfanın son <c>ts</c> + <c>event_id</c> değeri.</summary>
    public DateTimeOffset? AfterTimestamp { get; init; }
    public Guid? AfterEventId { get; init; }

    public int Limit { get; init; } = 200;
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

        group.MapPost("/search", SearchAsync).WithName("SearchEvents");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetEvent");
        group.MapGet("/{id:guid}/raw", GetRawAsync).WithName("GetEventRaw");

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

        return Results.Ok(new
        {
            events = page.Events,
            next = page.Next is null ? null : new
            {
                after_timestamp = page.Next.Timestamp,
                after_event_id = page.Next.EventId,
            },
            has_more = page.HasMore,
        });
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
        return found is null ? Results.NotFound() : Results.Ok(found);
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

        return Results.Ok(new
        {
            event_id = lookup.Record.EventId,
            object_key = lookup.ObjectKey,
            objects_scanned = lookup.ObjectsScanned,
            received_at = lookup.Record.ReceivedAt,
            source_key = lookup.Record.SourceKey,
            transport = new { proto = lookup.Record.TransportProto, peer = lookup.Record.TransportPeer },
            encoding_declared = lookup.Record.EncodingDeclared,
            // ORİJİNAL BAYTLAR. Metin olarak dönmek, kodlama tespiti yanlışsa
            // düzeltilecek olan şeyi bozmak olurdu (K4).
            raw_b64 = Convert.ToBase64String(lookup.Record.Body.Span),
        });
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
