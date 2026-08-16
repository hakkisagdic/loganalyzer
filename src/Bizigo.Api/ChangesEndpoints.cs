using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Api;

public sealed record ChangeWriteRequest
{
    public required string OwnerGroup { get; init; }
    public required string TargetKind { get; init; }
    public required string TargetId { get; init; }
    public required string ChangeKind { get; init; }

    public DateTimeOffset? Timestamp { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public string Source { get; init; } = "api";
    public string ExternalRef { get; init; } = string.Empty;
}

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

        group.MapGet("/", SearchAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("SearchChanges");

        group.MapPost("/", WriteAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("WriteChange");

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

        return Results.Ok(new { count = changes.Count, changes });
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
                error = $"Bilinmeyen targetKind: '{request.TargetKind}'.",
                allowed = Enum.GetNames<ChangeTargetKind>(),
            });
        }

        if (string.IsNullOrWhiteSpace(request.OwnerGroup)
            || string.IsNullOrWhiteSpace(request.TargetId)
            || string.IsNullOrWhiteSpace(request.ChangeKind))
        {
            return Results.BadRequest(new { error = "'ownerGroup', 'targetId' ve 'changeKind' zorunlu." });
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

        return Results.Created($"/v1/changes/{change.ChangeId}", new { change_id = change.ChangeId });
    }
}
