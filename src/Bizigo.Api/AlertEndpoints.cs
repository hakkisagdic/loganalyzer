using Bizigo.Alerting;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api;

/// <param name="Filters">Olay filtreleri; sessizlik tipinde yok sayılıyor.</param>
public sealed record AlertRuleRequest
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;

    /// <summary><c>threshold</c> | <c>ratio</c> | <c>silence</c>.</summary>
    public string RuleType { get; init; } = "threshold";

    public IReadOnlyList<string> OwnerGroups { get; init; } = [];
    public string? FullText { get; init; }
    public IReadOnlyList<FieldFilterRequest> Filters { get; init; } = [];
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    public int WindowSeconds { get; init; } = 300;
    public int IntervalSeconds { get; init; } = 60;
    public double Threshold { get; init; }

    /// <summary><c>gt</c> | <c>gte</c> | <c>lt</c> | <c>lte</c>.</summary>
    public string Comparison { get; init; } = "gt";

    public int SilenceSeconds { get; init; } = 900;
    public int RepeatIntervalSeconds { get; init; } = 3600;
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<Guid> ChannelIds { get; init; } = [];
}

public sealed record MaintenanceWindowRequest(
    string OwnerGroup,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid? RuleId,
    string Reason);

/// <summary>
/// Alarm kuralları, tetiklenme geçmişi ve bakım pencereleri (T21).
///
/// <para>
/// <b>Rol ayrımı:</b> okumak <c>read</c>, kural yazmak <c>author</c>. Kural
/// yazmak, arka planda periyodik olarak koşan bir sorgu yaratmak demek — yani
/// K16'nın "tek kötü kural" senaryosu tam olarak bu uçtan giriyor. Bunun
/// <c>author</c> ile sınırlı olması, kural sayısını yönetilebilir tutan tek
/// yapısal önlem; geri kalanı <c>AlertingOptions</c>'taki sınırlar.
/// </para>
///
/// <para>
/// <b>Kapsam kontrolü burada değil</b>, <see cref="AlertRuleService"/>'te.
/// Uç yalnızca <c>AccessScope</c>'u geçiriyor: kapsam kararının uç başına
/// tekrarlanması, K17'nin kaçındığı dağılmanın ta kendisi olurdu.
/// </para>
/// </summary>
public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlerts(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/v1/alerts").WithTags("alerts");

        group.MapGet("/rules", ListRulesAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListAlertRules");

        group.MapGet("/rules/{id:guid}", GetRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("GetAlertRule");

        group.MapPost("/rules", CreateRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateAlertRule");

        group.MapPut("/rules/{id:guid}", UpdateRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("UpdateAlertRule");

        group.MapDelete("/rules/{id:guid}", DeleteRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("DeleteAlertRule");

        group.MapGet("/triggers", ListTriggersAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListAlertTriggers");

        group.MapGet("/maintenance", ListWindowsAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListMaintenanceWindows");

        group.MapPost("/maintenance", CreateWindowAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateMaintenanceWindow");

        group.MapDelete("/maintenance/{id:guid}", DeleteWindowAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("DeleteMaintenanceWindow");

        group.MapGet("/stats", (AlertingStats stats) => Results.Ok(Describe(stats.Snapshot())))
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("AlertingStats");

        return routes;
    }

    private static async Task<IResult> ListRulesAsync(
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var found = await rules.ListAsync(user.Scope, cancellationToken);
        return Results.Ok(new { count = found.Count, rules = found.Select(Describe).ToArray() });
    }

    private static async Task<IResult> GetRuleAsync(
        Guid id,
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var rule = await rules.GetAsync(id, user.Scope, cancellationToken);

        if (rule is null)
        {
            return Results.NotFound();
        }

        // Kanal bağlantıları yalnızca burada: düzenleme formu onları geri
        // yazmak zorunda, yoksa ilk kaydetmede kanallar sessizce silinir.
        var channels = await rules.GetChannelIdsAsync(id, cancellationToken);

        return Results.Ok(new { rule = Describe(rule), channel_ids = channels });
    }

    private static Task<IResult> CreateRuleAsync(
        AlertRuleRequest request,
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveRuleAsync(null, request, rules, user, cancellationToken);

    private static Task<IResult> UpdateRuleAsync(
        Guid id,
        AlertRuleRequest request,
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        SaveRuleAsync(id, request, rules, user, cancellationToken);

    private static async Task<IResult> SaveRuleAsync(
        Guid? id,
        AlertRuleRequest request,
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        AlertRuleInput input;

        try
        {
            input = ToInput(request);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        var result = await rules.SaveAsync(id, input, user.Scope, cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result.Rule!))
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> DeleteRuleAsync(
        Guid id,
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken) =>
        await rules.DeleteAsync(id, user.Scope, cancellationToken)
            ? Results.NoContent()
            : Results.NotFound();

    /// <summary>
    /// Tetiklenme geçmişi.
    ///
    /// <para>
    /// Kapsam filtresi <b>kural üzerinden</b> uygulanıyor: tetiklenme kaydının
    /// kendi <c>owner_group</c>'u var ama ona güvenmek, kuralı görme yetkisi
    /// olmayan birinin o kuralın sonuçlarını okuyabilmesi demekti.
    /// </para>
    /// </summary>
    private static async Task<IResult> ListTriggersAsync(
        Guid? ruleId,
        int? limit,
        AlertRuleService rules,
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var visible = await rules.ListAsync(user.Scope, cancellationToken);
        var visibleIds = visible.Select(r => r.Id).ToHashSet();

        if (ruleId is { } requested && !visibleIds.Contains(requested))
        {
            return Results.NotFound();
        }

        var wanted = ruleId is { } single ? [single] : visibleIds.ToArray();

        if (wanted.Length == 0)
        {
            return Results.Ok(new { count = 0, triggers = Array.Empty<object>() });
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.AlertTriggers
            .AsNoTracking()
            .Where(t => wanted.Contains(t.RuleId))
            .OrderByDescending(t => t.FiredAt)
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .ToListAsync(cancellationToken);

        var names = visible.ToDictionary(r => r.Id, r => r.Name);

        return Results.Ok(new
        {
            count = rows.Count,
            triggers = rows.Select(t => new
            {
                id = t.Id,
                rule_id = t.RuleId,
                rule_name = names.TryGetValue(t.RuleId, out var name) ? name : string.Empty,
                fired_at = t.FiredAt,
                window_from = t.WindowFrom,
                window_to = t.WindowTo,
                value = t.Value,
                threshold = t.Threshold,
                source_id = t.SourceId,
                owner_group = t.OwnerGroup,
                summary = t.Summary,
            }).ToArray(),
        });
    }

    private static async Task<IResult> ListWindowsAsync(
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var all = await db.MaintenanceWindows.AsNoTracking()
            .OrderByDescending(w => w.StartsAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var scope = user.Scope;

        return Results.Ok(new
        {
            windows = all.Where(w => scope.Allows(w.OwnerGroup)).Select(w => new
            {
                id = w.Id,
                rule_id = w.RuleId,
                owner_group = w.OwnerGroup,
                starts_at = w.StartsAt,
                ends_at = w.EndsAt,
                reason = w.Reason,
                created_by = w.CreatedBy,
            }).ToArray(),
        });
    }

    private static async Task<IResult> CreateWindowAsync(
        MaintenanceWindowRequest request,
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!user.Scope.Allows(request.OwnerGroup))
        {
            return Results.BadRequest(new { error = $"'{request.OwnerGroup}' grubu kapsamınızda değil." });
        }

        if (request.EndsAt <= request.StartsAt)
        {
            return Results.BadRequest(new { error = "Pencerenin bitişi başlangıcından sonra olmalı." });
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var window = new MaintenanceWindowEntity
        {
            OwnerGroup = request.OwnerGroup,
            RuleId = request.RuleId,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            Reason = request.Reason,
            CreatedBy = user.Scope.Subject,
            CreatedAt = time.GetUtcNow(),
        };

        db.MaintenanceWindows.Add(window);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new { id = window.Id });
    }

    private static async Task<IResult> DeleteWindowAsync(
        Guid id,
        IDbContextFactory<ControlPlaneDbContext> factory,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var window = await db.MaintenanceWindows.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (window is null || !user.Scope.Allows(window.OwnerGroup))
        {
            return Results.NotFound();
        }

        db.MaintenanceWindows.Remove(window);
        await db.SaveChangesAsync(cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// İstek modelini alan modeline çeviriyor. Bilinmeyen tip/operatör
    /// <b>reddediliyor</b> — sessizce varsayılana düşmek, kullanıcının yazdığı
    /// kuraldan başka bir kuralın koşması demek olurdu.
    /// </summary>
    internal static AlertRuleInput ToInput(AlertRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AlertRuleInput
        {
            Name = request.Name,
            Description = request.Description,
            RuleType = ParseType(request.RuleType),
            OwnerGroups = request.OwnerGroups,
            Search = new AlertSearch
            {
                FullText = request.FullText,
                Filters = [.. request.Filters.Select(EventsEndpoints.ToFilter)],
                SourceIds = request.SourceIds,
            },
            WindowSeconds = request.WindowSeconds,
            IntervalSeconds = request.IntervalSeconds,
            Threshold = request.Threshold,
            Comparison = ParseComparison(request.Comparison),
            SilenceSeconds = request.SilenceSeconds,
            RepeatIntervalSeconds = request.RepeatIntervalSeconds,
            Enabled = request.Enabled,
            ChannelIds = request.ChannelIds,
        };
    }

    private static AlertRuleType ParseType(string value) => value?.ToLowerInvariant() switch
    {
        "threshold" or "esik" => AlertRuleType.Threshold,
        "ratio" or "oran" => AlertRuleType.Ratio,
        "silence" or "sessizlik" => AlertRuleType.Silence,
        _ => throw new ArgumentException(
            $"Bilinmeyen kural tipi: '{value}'. Beklenen: threshold, ratio, silence.", nameof(value)),
    };

    private static AlertComparison ParseComparison(string value) => value?.ToLowerInvariant() switch
    {
        "gt" => AlertComparison.GreaterThan,
        "gte" => AlertComparison.GreaterThanOrEqual,
        "lt" => AlertComparison.LessThan,
        "lte" => AlertComparison.LessThanOrEqual,
        _ => throw new ArgumentException(
            $"Bilinmeyen karşılaştırma: '{value}'. Beklenen: gt, gte, lt, lte.", nameof(value)),
    };

    private static object Describe(AlertRuleEntity rule) => new
    {
        id = rule.Id,
        name = rule.Name,
        description = rule.Description,
        rule_type = rule.RuleType.ToString().ToLowerInvariant(),
        owner_groups = rule.OwnerGroups.Split(',', StringSplitOptions.RemoveEmptyEntries),
        window_seconds = rule.WindowSeconds,
        interval_seconds = rule.IntervalSeconds,
        threshold = rule.Threshold,
        comparison = rule.Comparison.ToString(),
        silence_seconds = rule.SilenceSeconds,
        repeat_interval_seconds = rule.RepeatIntervalSeconds,
        enabled = rule.Enabled,
        next_run_at = rule.NextRunAt,
        last_run_at = rule.LastRunAt,
        last_fired_at = rule.LastFiredAt,
        last_run_state = rule.LastRunState.ToString(),
        last_error = rule.LastError,
        search = rule.SearchJson,
    };

    private static object Describe(AlertingSnapshot snapshot) => new
    {
        turns = snapshot.Turns,
        evaluated = snapshot.Evaluated,
        fired = snapshot.Fired,
        suppressed = snapshot.Suppressed,

        // Sıfırdan büyükse motor BİLMEDİĞİ bir şeyi "sessiz" sanmış olabilir.
        timed_out = snapshot.TimedOut,
        failed = snapshot.Failed,

        // T21 kabul kriteri burada ölçülüyor: kural sayısı arttığında bu sayı
        // doğrusal ötesi büyümemeli.
        scoped_queries = snapshot.ScopedQueries,

        notifications = new
        {
            queued = snapshot.NotificationsQueued,
            delivered = snapshot.NotificationsDelivered,
            retried = snapshot.NotificationsRetried,
            abandoned = snapshot.NotificationsAbandoned,
        },
    };
}
