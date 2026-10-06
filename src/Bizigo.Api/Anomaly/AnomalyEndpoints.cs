using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Api.Anomaly;

public sealed record CreateAnomalyPolicyRequest(
    string Name,
    string OwnerGroup,
    string Signal,
    string Target,
    int? EventWindowSeconds,
    int? BaselineWindowSeconds,
    double? Sensitivity,
    int? MinSamples,
    double? ZeroBaselineMinAbsolute,
    int? CadenceSeconds);

public sealed record UpdateAnomalyPolicyRequest(
    int ExpectedVersion,
    string? State,
    string? Name,
    double? Sensitivity,
    int? MinSamples,
    int? BaselineWindowSeconds,
    int? EventWindowSeconds,
    double? ZeroBaselineMinAbsolute);

public sealed record AnomalyPolicyResponse(
    string Id,
    string OwnerGroup,
    string Name,
    string Signal,
    string Target,
    int EventWindowSeconds,
    int BaselineWindowSeconds,
    double Sensitivity,
    int MinSamples,
    double? ZeroBaselineMinAbsolute,
    string State,
    int Version,
    int CadenceSeconds,
    DateTimeOffset? LastEvaluatedWindowStart,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AnomalyRunResponse(
    string Id,
    string PolicyId,
    string OwnerGroup,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    string Status,
    string? Reason,
    double? ObservedValue,
    double? BaselineValue,
    double? Deviation,
    Guid? RcaRunId,
    int EvaluatedPolicyVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public static class AnomalyEndpoints
{
    public static IEndpointRouteBuilder MapAnomalies(this IEndpointRouteBuilder routes)
    {
        // Register under both /api and /v1 for complete compatibility
        BindPolicyRoutes("/api/anomaly-policies", routes);
        BindPolicyRoutes("/v1/anomaly-policies", routes);

        BindRunRoutes("/api/anomaly-runs", routes);
        BindRunRoutes("/v1/anomaly-runs", routes);

        return routes;
    }

    private static void BindPolicyRoutes(string prefix, IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(prefix).WithTags("AnomalyPolicies");

        group.MapGet("/", ListPoliciesAsync)
            .WithName($"ListAnomalyPolicies_{prefix.Replace('/', '_')}")
            .WithSummary("List anomaly policies within caller's access scope")
            .Produces<IReadOnlyList<AnomalyPolicyResponse>>();

        group.MapGet("/{id}", GetPolicyAsync)
            .WithName($"GetAnomalyPolicy_{prefix.Replace('/', '_')}")
            .WithSummary("Get anomaly policy by ID")
            .Produces<AnomalyPolicyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreatePolicyAsync)
            .WithName($"CreateAnomalyPolicy_{prefix.Replace('/', '_')}")
            .WithSummary("Create a new anomaly policy")
            .Produces<AnomalyPolicyResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapPatch("/{id}", UpdatePolicyAsync)
            .WithName($"UpdateAnomalyPolicy_{prefix.Replace('/', '_')}")
            .WithSummary("Update an anomaly policy (enable/disable or parameter changes)")
            .Produces<AnomalyPolicyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem();

        // S17: Explicit DELETE 405 Method Not Allowed with Allow: GET, PATCH
        routes.MapDelete($"{prefix}/{{id}}", (HttpResponse response) =>
        {
            response.Headers.Allow = "GET, PATCH";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).ExcludeFromDescription();
    }

    private static void BindRunRoutes(string prefix, IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup(prefix).WithTags("AnomalyRuns");

        group.MapGet("/", ListRunsAsync)
            .WithName($"ListAnomalyRuns_{prefix.Replace('/', '_')}")
            .WithSummary("List anomaly runs within caller's access scope")
            .Produces<IReadOnlyList<AnomalyRunResponse>>();

        group.MapGet("/{id}", GetRunAsync)
            .WithName($"GetAnomalyRun_{prefix.Replace('/', '_')}")
            .WithSummary("Get anomaly run by ID")
            .Produces<AnomalyRunResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // S27: Explicit DELETE 405 Method Not Allowed with Allow: GET
        routes.MapDelete($"{prefix}/{{id}}", (HttpResponse response) =>
        {
            response.Headers.Allow = "GET";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).ExcludeFromDescription();
    }

    private static async Task<IResult> ListPoliciesAsync(
        ControlPlaneDbContext db,
        ICurrentUser user,
        string? state,
        string? signal,
        CancellationToken cancellationToken)
    {
        if (user.Scope.IsEmpty) return Results.Ok(Array.Empty<AnomalyPolicyResponse>());

        var query = db.AnomalyPolicies.AsNoTracking();

        if (!user.Scope.IsUnrestricted)
        {
            query = query.Where(p => user.Scope.OwnerGroups.Contains(p.OwnerGroup));
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            query = query.Where(p => p.State == state);
        }

        if (!string.IsNullOrWhiteSpace(signal))
        {
            query = query.Where(p => p.Signal == signal);
        }

        var items = await query.OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken);
        return Results.Ok(items.Select(ToResponse));
    }

    private static async Task<IResult> GetPolicyAsync(
        string id,
        ControlPlaneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var policy = await db.AnomalyPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null || !user.Scope.Allows(policy.OwnerGroup))
        {
            return Results.NotFound();
        }

        return Results.Ok(ToResponse(policy));
    }

    private static async Task<IResult> CreatePolicyAsync(
        CreateAnomalyPolicyRequest req,
        ControlPlaneDbContext db,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // S21: Validation - no magic fallbacks or default literals
        if (string.IsNullOrWhiteSpace(req.Name))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Name is required."] });

        if (string.IsNullOrWhiteSpace(req.OwnerGroup))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["owner_group"] = ["OwnerGroup is required."] });

        if (!user.Scope.Allows(req.OwnerGroup))
            return Results.Forbid();

        if (string.IsNullOrWhiteSpace(req.Signal) || !AnomalySignals.IsValid(req.Signal))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["signal"] = ["Signal must be one of: log_event_count, metric_series_sum, trace_error_rate."] });

        if (string.IsNullOrWhiteSpace(req.Target))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["Target is required."] });

        if (!req.EventWindowSeconds.HasValue || req.EventWindowSeconds.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["event_window_seconds"] = ["event_window_seconds must be strictly positive."] });

        if (!req.BaselineWindowSeconds.HasValue || req.BaselineWindowSeconds.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseline_window_seconds"] = ["baseline_window_seconds must be strictly positive."] });

        if (!req.Sensitivity.HasValue || req.Sensitivity.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sensitivity"] = ["sensitivity must be strictly positive."] });

        if (!req.MinSamples.HasValue || req.MinSamples.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["min_samples"] = ["min_samples must be strictly positive."] });

        int cadence = req.CadenceSeconds ?? req.EventWindowSeconds.Value;
        if (cadence <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["cadence_seconds"] = ["cadence_seconds must be positive."] });

        var now = clock.GetUtcNow();
        var entity = new AnomalyPolicyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = req.Name.Trim(),
            OwnerGroup = req.OwnerGroup.Trim(),
            Signal = req.Signal.Trim(),
            Target = req.Target.Trim(),
            EventWindowSeconds = req.EventWindowSeconds.Value,
            BaselineWindowSeconds = req.BaselineWindowSeconds.Value,
            Sensitivity = req.Sensitivity.Value,
            MinSamples = req.MinSamples.Value,
            ZeroBaselineMinAbsolute = req.ZeroBaselineMinAbsolute,
            CadenceSeconds = cadence,
            State = AnomalyPolicyStates.Enabled,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AnomalyPolicies.Add(entity);

        // Audit log
        db.AuditLog.Add(new AuditLogEntity
        {
            At = now,
            Subject = user.Scope.Subject,
            Action = "anomaly.policy.created",
            Resource = entity.Id,
            Scope = entity.OwnerGroup,
            Details = $"Created anomaly policy {entity.Name} on signal {entity.Signal}"
        });

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/anomaly-policies/{entity.Id}", ToResponse(entity));
    }

    private static async Task<IResult> UpdatePolicyAsync(
        string id,
        UpdateAnomalyPolicyRequest req,
        ControlPlaneDbContext db,
        ICurrentUser user,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var policy = await db.AnomalyPolicies.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null || !user.Scope.Allows(policy.OwnerGroup))
        {
            return Results.NotFound();
        }

        // S01, S09, S17: Optimistic concurrency check
        if (policy.Version != req.ExpectedVersion)
        {
            return Results.Conflict(new { error = "Version conflict. The policy was modified concurrently.", current_version = policy.Version });
        }

        if (req.State is not null && !AnomalyPolicyStates.All.Contains(req.State))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["State must be 'Enabled' or 'Disabled'."] });
        }

        if (req.Sensitivity.HasValue && req.Sensitivity.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sensitivity"] = ["sensitivity must be strictly positive."] });

        if (req.MinSamples.HasValue && req.MinSamples.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["min_samples"] = ["min_samples must be strictly positive."] });

        if (req.BaselineWindowSeconds.HasValue && req.BaselineWindowSeconds.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["baseline_window_seconds"] = ["baseline_window_seconds must be strictly positive."] });

        if (req.EventWindowSeconds.HasValue && req.EventWindowSeconds.Value <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["event_window_seconds"] = ["event_window_seconds must be strictly positive."] });

        var now = clock.GetUtcNow();
        var oldState = policy.State;

        if (!string.IsNullOrWhiteSpace(req.Name)) policy.Name = req.Name.Trim();
        if (req.State is not null) policy.State = req.State;
        if (req.Sensitivity.HasValue) policy.Sensitivity = req.Sensitivity.Value;
        if (req.MinSamples.HasValue) policy.MinSamples = req.MinSamples.Value;
        if (req.BaselineWindowSeconds.HasValue) policy.BaselineWindowSeconds = req.BaselineWindowSeconds.Value;
        if (req.EventWindowSeconds.HasValue) policy.EventWindowSeconds = req.EventWindowSeconds.Value;
        if (req.ZeroBaselineMinAbsolute.HasValue) policy.ZeroBaselineMinAbsolute = req.ZeroBaselineMinAbsolute.Value;

        policy.Version += 1;
        policy.UpdatedAt = now;

        string auditAction;
        if (oldState != policy.State && policy.State == AnomalyPolicyStates.Disabled)
            auditAction = "anomaly.policy.disabled";
        else if (oldState != policy.State && policy.State == AnomalyPolicyStates.Enabled)
            auditAction = "anomaly.policy.enabled";
        else
            auditAction = "anomaly.policy.updated";

        db.AuditLog.Add(new AuditLogEntity
        {
            At = now,
            Subject = user.Scope.Subject,
            Action = auditAction,
            Resource = policy.Id,
            Scope = policy.OwnerGroup,
            Details = $"Updated anomaly policy {policy.Id} to version {policy.Version}"
        });

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToResponse(policy));
    }

    private static async Task<IResult> ListRunsAsync(
        ControlPlaneDbContext db,
        ICurrentUser user,
        string? policyId,
        string? status,
        CancellationToken cancellationToken)
    {
        if (user.Scope.IsEmpty) return Results.Ok(Array.Empty<AnomalyRunResponse>());

        var query = db.AnomalyRuns.AsNoTracking();

        if (!user.Scope.IsUnrestricted)
        {
            query = query.Where(r => user.Scope.OwnerGroups.Contains(r.OwnerGroup));
        }

        if (!string.IsNullOrWhiteSpace(policyId))
        {
            query = query.Where(r => r.PolicyId == policyId);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(r => r.Status == status);
        }

        var items = await query.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(cancellationToken);
        return Results.Ok(items.Select(ToRunResponse));
    }

    private static async Task<IResult> GetRunAsync(
        string id,
        ControlPlaneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var run = await db.AnomalyRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (run is null || !user.Scope.Allows(run.OwnerGroup))
        {
            return Results.NotFound();
        }

        return Results.Ok(ToRunResponse(run));
    }

    private static AnomalyPolicyResponse ToResponse(AnomalyPolicyEntity p) =>
        new(p.Id, p.OwnerGroup, p.Name, p.Signal, p.Target, p.EventWindowSeconds,
            p.BaselineWindowSeconds, p.Sensitivity, p.MinSamples, p.ZeroBaselineMinAbsolute,
            p.State, p.Version, p.CadenceSeconds, p.LastEvaluatedWindowStart, p.CreatedAt, p.UpdatedAt);

    private static AnomalyRunResponse ToRunResponse(AnomalyRunEntity r) =>
        new(r.Id, r.PolicyId, r.OwnerGroup, r.WindowStart, r.WindowEnd, r.Status,
            r.Reason, r.ObservedValue, r.BaselineValue, r.Deviation, r.RcaRunId,
            r.EvaluatedPolicyVersion, r.CreatedAt, r.CompletedAt);
}
