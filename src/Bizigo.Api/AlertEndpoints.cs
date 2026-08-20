using Bizigo.Alerting;
using Bizigo.Contracts;
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
/// Alarm kuralları, önizleme, tetiklenme geçmişi ve bakım pencereleri (T21, T23).
///
/// <para>
/// <b>Rol ayrımı:</b> okumak <c>read</c>, kural yazmak ve önizlemek
/// <c>author</c>. Kural yazmak, arka planda periyodik koşan bir sorgu yaratmak
/// demek — K16'nın "tek kötü kural" senaryosu tam olarak bu uçtan giriyor.
/// </para>
///
/// <para>
/// <b>Kapsam kontrolü burada değil</b>, <see cref="AlertRuleService"/>'te. Uç
/// yalnızca <c>AccessScope</c>'u geçiriyor: kapsam kararının uç başına
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
            .WithName("ListAlertRules")
            .Produces<AlertRuleListResponse>();

        group.MapGet("/rules/{id:guid}", GetRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("GetAlertRule")
            .Produces<AlertRuleDetailResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/rules", CreateRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateAlertRule")
            .Produces<AlertRuleResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapPut("/rules/{id:guid}", UpdateRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("UpdateAlertRule")
            .Produces<AlertRuleResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapDelete("/rules/{id:guid}", DeleteRuleAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("DeleteAlertRule")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        // Önizleme kural YAZMIYOR — o hâlde neden `read` değil `author`?
        // Çünkü geçmiş veriye karşı toplu sorgu koşturuyor ve maliyeti kural
        // yazmanınkiyle aynı sınıfta (K16). Ağır sorguyu ancak onu üretecek
        // kişinin çalıştırabilmesi doğru ayrım.
        group.MapPost("/rules/preview", PreviewAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("PreviewAlertRule")
            .Produces<AlertPreviewResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapGet("/triggers", ListTriggersAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListAlertTriggers")
            .Produces<AlertTriggerListResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/maintenance", ListWindowsAsync)
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("ListMaintenanceWindows")
            .Produces<MaintenanceWindowListResponse>();

        group.MapPost("/maintenance", CreateWindowAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("CreateMaintenanceWindow")
            .Produces<CreatedIdResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        group.MapDelete("/maintenance/{id:guid}", DeleteWindowAsync)
            .RequireAuthorization(BizigoAuthPolicies.Author)
            .WithName("DeleteMaintenanceWindow")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/stats", (AlertingStats stats) => Results.Ok(Describe(stats.Snapshot())))
            .RequireAuthorization(BizigoAuthPolicies.Read)
            .WithName("AlertingStats")
            .Produces<AlertingStatsResponse>();

        return routes;
    }

    private static async Task<IResult> ListRulesAsync(
        AlertRuleService rules,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var found = await rules.ListAsync(user.Scope, cancellationToken);
        return Results.Ok(new AlertRuleListResponse(found.Count, [.. found.Select(Describe)]));
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

        return Results.Ok(new AlertRuleDetailResponse(Describe(rule), [.. channels]));
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
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        var result = await rules.SaveAsync(id, input, user.Scope, cancellationToken);

        return result.Ok
            ? Results.Ok(Describe(result.Rule!))
            : Results.BadRequest(new ErrorResponse(result.Error));
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
    /// Kural önizlemesi — T23'ün taşıyıcı maddesi.
    ///
    /// <para>
    /// <b>Kaydedilmemiş</b> bir kuralı geçmiş veriye karşı koşturuyor: "bu kural
    /// son 24 saatte kaç kez tetiklenirdi". K16'daki elli kişilik kurumda
    /// gürültüyü kural üretime girmeden kesen tek mekanizma bu.
    /// </para>
    ///
    /// <para>
    /// <b>Yanıt eşikten bağımsız.</b> Ekran eşiği değiştirdiğinde sayıyı aynı
    /// veriden yeniden hesaplıyor ve yeni istek atmıyor; aksi hâlde kaydırıcıyı
    /// sürükleyen tek bir kullanıcı saniyede onlarca ağır sorgu üretir — yani
    /// önizleme, önlemeye çalıştığı sorunun kendisi olurdu.
    /// </para>
    /// </summary>
    private static async Task<IResult> PreviewAsync(
        AlertRuleRequest request,
        int? lookbackSeconds,
        AlertPreview preview,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        AlertRuleInput input;

        try
        {
            input = ToInput(request);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }

        var lookback = lookbackSeconds is > 0 ? TimeSpan.FromSeconds(lookbackSeconds.Value) : (TimeSpan?)null;

        var result = await preview.RunAsync(
            input, user.Scope, lookback, time.GetUtcNow(), cancellationToken);

        return Results.Ok(new AlertPreviewResponse(
            result.RuleType.ToString().ToLowerInvariant(),
            result.From,
            result.To,
            result.BucketSeconds,
            result.Threshold,
            result.FiringCount,
            result.Note,
            [.. result.Points.Select(p => new PreviewPointResponse(p.At, p.Count, p.Value))],
            [.. result.Sources.Select(s => new PreviewSourceResponse(
                s.SourceId, s.OwnerGroup, s.LastSeen, [.. s.Gaps]))]));
    }

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
            return Results.Ok(new AlertTriggerListResponse(0, []));
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.AlertTriggers
            .AsNoTracking()
            .Where(t => wanted.Contains(t.RuleId))
            .OrderByDescending(t => t.FiredAt)
            .Take(Math.Clamp(limit ?? 100, 1, 500))
            .ToListAsync(cancellationToken);

        var names = visible.ToDictionary(r => r.Id, r => r.Name);

        // "Gönderildi" ile "ulaştı" AYRI (T23 kabul kriteri). Teslim kayıtları
        // tetiklenmeyle birlikte dönüyor: ayrı bir uçtan çekilseydi ekran satır
        // başına bir istek atardı ve çoğu ekran bunu yapmayıp yalnızca
        // "tetiklendi"yi gösterirdi.
        var triggerIds = rows.Select(t => t.Id).ToArray();

        var deliveries = triggerIds.Length == 0
            ? []
            : await (from delivery in db.NotificationDeliveries.AsNoTracking()
                     join channel in db.NotificationChannels.AsNoTracking()
                         on delivery.ChannelId equals channel.Id into joined
                     from channel in joined.DefaultIfEmpty()
                     where triggerIds.Contains(delivery.TriggerId)
                     select new
                     {
                         delivery.TriggerId,
                         delivery.ChannelId,
                         delivery.State,
                         delivery.Attempts,
                         delivery.LastError,
                         delivery.DeliveredAt,
                         delivery.NextAttemptAt,
                         // Kanal silinmiş olabilir: teslim kaydı duruyor ve
                         // geçmişte oraya gidildiği bilgisi kaybolmamalı.
                         ChannelName = channel == null ? null : channel.Name,
                         ChannelType = channel == null ? (NotificationChannelType?)null : channel.ChannelType,
                     })
                .ToListAsync(cancellationToken);

        var byTrigger = deliveries.GroupBy(d => d.TriggerId).ToDictionary(g => g.Key, g => g.ToArray());

        var triggers = rows.Select(t => new AlertTriggerResponse(
            t.Id,
            t.RuleId,
            names.TryGetValue(t.RuleId, out var name) ? name : string.Empty,
            t.FiredAt,
            t.WindowFrom,
            t.WindowTo,
            t.Value,
            t.Threshold,
            t.SourceId,
            t.OwnerGroup,
            t.Summary,
            [.. (byTrigger.TryGetValue(t.Id, out var sent) ? sent : []).Select(d => new AlertDeliveryResponse(
                d.ChannelId,
                d.ChannelName ?? "(silinmiş kanal)",
                d.ChannelType?.ToString().ToLowerInvariant(),
                d.State.ToString().ToLowerInvariant(),
                d.Attempts,
                d.DeliveredAt,
                d.State == DeliveryState.Pending ? d.NextAttemptAt : null,

                // Redaksiyondan geçmiş hâli; gönderici gizli bilgiyi buraya
                // yazamıyor (T22 bekçisi).
                d.LastError))],
            t.State.ToString().ToLowerInvariant(),
            t.ClosedAt,

            // Boş dize yerine `null`: "kapatan yok" ile "kapatanın adı boş"
            // aynı şey değil ve ekranın ikisini ayırt edebilmesi gerekiyor.
            string.IsNullOrEmpty(t.ClosedBySubject) ? null : t.ClosedBySubject,
            t.ReviewId))
            .ToArray();

        return Results.Ok(new AlertTriggerListResponse(triggers.Length, triggers));
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

        return Results.Ok(new MaintenanceWindowListResponse(
            [.. all.Where(w => scope.Allows(w.OwnerGroup)).Select(w => new MaintenanceWindowResponse(
                w.Id, w.RuleId, w.OwnerGroup, w.StartsAt, w.EndsAt, w.Reason, w.CreatedBy))]));
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
            return Results.BadRequest(new ErrorResponse($"'{request.OwnerGroup}' grubu kapsamınızda değil."));
        }

        if (request.EndsAt <= request.StartsAt)
        {
            return Results.BadRequest(new ErrorResponse("Pencerenin bitişi başlangıcından sonra olmalı."));
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

        return Results.Ok(new CreatedIdResponse(window.Id));
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

    /// <summary>
    /// Operatörün kısa adı — istekte kabul edilenin <b>aynısı</b>.
    ///
    /// <para>
    /// Yanıtta enum adını (<c>Equals</c>) döndürüp istekte kısa adı
    /// (<c>eq</c>) beklemek, formun okuduğunu geri yazamaması demekti: kullanıcı
    /// bir kuralı açıp kaydettiğinde filtresi sessizce reddedilirdi.
    /// </para>
    /// </summary>
    internal static string ShortName(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "eq",
        FilterOperator.NotEquals => "ne",
        FilterOperator.In => "in",
        FilterOperator.GreaterThan => "gt",
        FilterOperator.LessThan => "lt",
        FilterOperator.Contains => "contains",
        FilterOperator.StartsWith => "startswith",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Bilinmeyen operatör."),
    };

    private static AlertRuleResponse Describe(AlertRuleEntity rule)
    {
        // Arama YAPILANDIRILMIŞ dönüyor, ham JSON olarak değil: formun onu geri
        // yazabilmesi gerekiyor ve JSON'u istemcide ayrıştırmak, şemadan üretilen
        // tiplerin sağladığı güvenceyi tam da en kırılgan alanda kaybetmek olurdu.
        var search = AlertSearchCodec.Deserialize(rule.SearchJson);

        return new AlertRuleResponse(
            rule.Id,
            rule.Name,
            rule.Description,
            rule.RuleType.ToString().ToLowerInvariant(),
            rule.OwnerGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            rule.WindowSeconds,
            rule.IntervalSeconds,
            rule.Threshold,
            ComparisonName(rule.Comparison),
            rule.SilenceSeconds,
            rule.RepeatIntervalSeconds,
            rule.Enabled,
            rule.NextRunAt,
            rule.LastRunAt,
            rule.LastFiredAt,
            rule.LastRunState.ToString().ToLowerInvariant(),
            rule.LastError,
            new AlertSearchResponse(
                search.FullText,
                [.. search.Filters.Select(f => new FieldFilterResponse(f.Field, ShortName(f.Operator), [.. f.Values]))],
                [.. search.SourceIds]));
    }

    /// <summary>Karşılaştırmanın kısa adı — istekte kabul edilenin aynısı.</summary>
    internal static string ComparisonName(AlertComparison comparison) => comparison switch
    {
        AlertComparison.GreaterThan => "gt",
        AlertComparison.GreaterThanOrEqual => "gte",
        AlertComparison.LessThan => "lt",
        AlertComparison.LessThanOrEqual => "lte",
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "Bilinmeyen karşılaştırma."),
    };

    private static AlertingStatsResponse Describe(AlertingSnapshot snapshot) => new(
        snapshot.Turns,
        snapshot.Evaluated,
        snapshot.Fired,
        snapshot.Suppressed,

        // Sıfırdan büyükse motor BİLMEDİĞİ bir şeyi "sessiz" sanmış olabilir.
        snapshot.TimedOut,
        snapshot.Failed,

        // T21 kabul kriteri burada ölçülüyor: kural sayısı arttığında bu sayı
        // doğrusal ötesi büyümemeli.
        snapshot.ScopedQueries,
        new AlertingNotificationStats(
            snapshot.NotificationsQueued,
            snapshot.NotificationsDelivered,
            snapshot.NotificationsRetried,
            snapshot.NotificationsAbandoned));
}
