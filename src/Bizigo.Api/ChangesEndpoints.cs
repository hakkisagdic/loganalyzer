using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Api;

/// <remarks>
/// <b>T25'te <c>snake_case</c>'e çevrildi</b> ve bu F1 yüzeyinde bilinçli bir
/// kırıcı değişiklik: yanıt tarafı <c>snake_case</c> olduğuna göre isteğin
/// <c>camelCase</c> kalması, tek bir ucun iki dil konuşması demekti. Uç henüz
/// yalnızca ürünün kendi ekranından çağrılıyor; dışarıdan bir tüketici doğmadan
/// düzeltmenin maliyeti sıfır.
/// </remarks>
public sealed record ChangeWriteRequest
{
    [JsonPropertyName("owner_group")]
    public required string OwnerGroup { get; init; }

    [JsonPropertyName("target_kind")]
    public required string TargetKind { get; init; }

    [JsonPropertyName("target_id")]
    public required string TargetId { get; init; }

    [JsonPropertyName("change_kind")]
    public required string ChangeKind { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("actor")]
    public string Actor { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("source")]
    public string Source { get; init; } = "api";

    [JsonPropertyName("external_ref")]
    public string ExternalRef { get; init; } = string.Empty;
}

/// <summary>
/// Değişiklik olayının <b>tel üzerindeki</b> şekli (T24/T25).
///
/// <para>
/// <see cref="ChangeEvent"/> doğrudan yayınlanmıyor. İki sebep, ikisi de
/// ölçüldü:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>Alan adları <c>snake_case</c>.</b> T15 olay uçlarını ve <c>/auth/me</c>'yi
/// öyle bıraktı; iki uç ailesinin iki dil konuşması, hangi ekranın hangisini
/// beklediğini hatırlamak zorunda kalmak demekti.
/// </item>
/// <item>
/// <b><c>target_kind</c> metin, sayı değil.</b> Enum'u olduğu gibi yayınlamak
/// sözleşmeye <c>3</c> yazıyor; enum'a bir gün ortadan bir değer eklendiğinde o
/// sayı sessizce kayar ve hiçbir şey kırılmaz — yalnızca yanlış olur. Ayrıca
/// istek tarafı zaten <c>"Config"</c> kabul ediyordu, yani aynı alan bir yöne
/// metin öbür yöne sayıydı.
/// </item>
/// </list>
/// </summary>
public sealed record ChangeResponse(
    [property: JsonPropertyName("change_id")] Guid ChangeId,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("target_kind")] string TargetKind,
    [property: JsonPropertyName("target_id")] string TargetId,
    [property: JsonPropertyName("change_kind")] string ChangeKind,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, string> Details,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("external_ref")] string ExternalRef)
{
    public static ChangeResponse From(ChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new ChangeResponse(
            change.ChangeId,
            change.Timestamp,
            change.OwnerGroup,
            change.TargetKind.ToString(),
            change.TargetId,
            change.ChangeKind,
            change.Actor,
            change.Summary,
            change.Details,
            change.Source,
            change.ExternalRef);
    }
}

public sealed record ChangeSearchResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("changes")] IReadOnlyList<ChangeResponse> Changes);

public sealed record ChangeWriteResponse(
    [property: JsonPropertyName("change_id")] Guid ChangeId);

/// <summary>
/// Değişiklik olayı uçları (T10).
///
/// <para>
/// <b>Küçük uç, büyük sonuç.</b> Log tek başına "ne oldu"yu söyler, "neden"i
/// çoğu zaman söylemez. RCA'nın (F3) kalitesindeki en büyük tek sıçrama "ne
/// değişti" verisinden geliyor — deploy, config değişikliği, bakım penceresi.
/// Uç F1'de hazır olmak zorunda çünkü <b>geçmiş birikmeye şimdi başlamalı</b>;
/// F3'te açılırsa özellik boş bir tabloyla doğar.
/// </para>
/// </summary>
public static class ChangesEndpoints
{
    public static IEndpointRouteBuilder MapChanges(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/changes").WithTags("changes");

        // Yanıt tipleri T24'ün değişiklik akışı ekranıyla birlikte geldi:
        // tüketicisi olmadan yazılan bir tip, hangi alanların sözleşmeye
        // girdiğini tahmin etmek olurdu (ProducesContractTests).
        group.MapGet("/", SearchAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("SearchChanges")
            .Produces<ChangeSearchResponse>();

        group.MapPost("/", WriteAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("WriteChange")
            .Produces<ChangeWriteResponse>(StatusCodes.Status201Created);

        return routes;
    }

    private static async Task<IResult> SearchAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? targetId,
        string? changeKind,
        int? limit,
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var scope = user.Scope;

        if (scope.IsEmpty)
        {
            return Results.Forbid();
        }

        var upper = to ?? DateTimeOffset.UtcNow;
        var lower = from ?? upper - TimeSpan.FromDays(7);

        if (lower >= upper)
        {
            return Results.BadRequest(new { error = "'from' değeri 'to' değerinden küçük olmalı." });
        }

        var changes = await query.SearchChangesAsync(
            new ChangeQuery
            {
                From = lower,
                To = upper,
                TargetIds = string.IsNullOrWhiteSpace(targetId) ? [] : [targetId],
                ChangeKinds = string.IsNullOrWhiteSpace(changeKind) ? [] : [changeKind],
                Limit = Math.Clamp(limit ?? 500, 1, 2000),
            },
            scope,
            cancellationToken);

        return Results.Ok(new ChangeSearchResponse(
            changes.Count, [.. changes.Select(ChangeResponse.From)]));
    }

    private static async Task<IResult> WriteAsync(
        ChangeWriteRequest request,
        IScopedQuery query,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ChangeTargetKind>(request.TargetKind, ignoreCase: true, out var targetKind))
        {
            return Results.BadRequest(new
            {
                error = $"Bilinmeyen target_kind: '{request.TargetKind}'.",
                allowed = Enum.GetNames<ChangeTargetKind>(),
            });
        }

        if (string.IsNullOrWhiteSpace(request.OwnerGroup)
            || string.IsNullOrWhiteSpace(request.TargetId)
            || string.IsNullOrWhiteSpace(request.ChangeKind))
        {
            return Results.BadRequest(new
            {
                error = "'owner_group', 'target_id' ve 'change_kind' zorunlu.",
            });
        }

        var change = new ChangeEvent
        {
            ChangeId = Guid.CreateVersion7(),
            Timestamp = request.Timestamp ?? DateTimeOffset.UtcNow,
            OwnerGroup = request.OwnerGroup,
            TargetKind = targetKind,
            TargetId = request.TargetId,
            ChangeKind = request.ChangeKind,
            Actor = request.Actor,
            Summary = request.Summary,
            Details = request.Details,
            Source = request.Source,
            ExternalRef = request.ExternalRef,
        };

        try
        {
            // Yazma da IScopedQuery'den geçiyor: çağıran yalnızca kendi
            // kapsamındaki bir gruba yazabilir. Aksi halde bir ekip başka bir
            // ekibin zaman çizelgesine olay düşürebilirdi.
            await query.WriteChangeAsync(change, user.Scope, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }

        return Results.Created(
            $"/v1/changes/{change.ChangeId}", new ChangeWriteResponse(change.ChangeId));
    }
}
