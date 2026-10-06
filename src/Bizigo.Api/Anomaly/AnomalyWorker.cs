using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Api.Anomaly;

public sealed class AnomalyWorkerOptions
{
    public const string SectionName = "Anomaly:Worker";
    public bool Enabled { get; set; } = true;
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class AnomalyWorker(
    IDbContextFactory<ControlPlaneDbContext> dbFactory,
    IServiceScopeFactory scopes,
    RcaAdmission admission,
    TimeProvider clock,
    ILogger<AnomalyWorker> logger,
    AnomalyWorkerOptions? options = null) : BackgroundService
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly RcaAdmission _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger<AnomalyWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly AnomalyWorkerOptions _options = options ?? new AnomalyWorkerOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AnomalyWorker is disabled in configuration.");
            return;
        }

        // S29: Recover orphaned running jobs from prior run
        await RecoverOrphanedRunsAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTurnAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in AnomalyWorker execution turn.");
            }

            try
            {
                await Task.Delay(_options.CheckInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task RecoverOrphanedRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stuckRuns = await db.AnomalyRuns
            .Where(r => r.Status == AnomalyRunStatuses.Running)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stuckRuns.Count != 0)
        {
            var now = _clock.GetUtcNow();
            foreach (var run in stuckRuns)
            {
                run.Status = AnomalyRunStatuses.Failed;
                run.Reason = "unrecovered_restart";
                run.CompletedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RunTurnAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Fetch enabled policies
        var policies = await db.AnomalyPolicies
            .Where(p => p.State == AnomalyPolicyStates.Enabled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        var jobId = $"job-{Guid.NewGuid():N}";

        await using var scope = _scopes.CreateAsyncScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<IAnomalyEvaluator>();

        foreach (var policy in policies)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await EvaluatePolicyAsync(db, evaluator, policy, now, jobId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EvaluatePolicyAsync(
        ControlPlaneDbContext db,
        IAnomalyEvaluator evaluator,
        AnomalyPolicyEntity policy,
        DateTimeOffset now,
        string jobId,
        CancellationToken cancellationToken)
    {
        int cadence = policy.CadenceSeconds > 0 ? policy.CadenceSeconds : policy.EventWindowSeconds;
        if (cadence <= 0) cadence = 300;

        long currentSec = now.ToUnixTimeSeconds();
        long alignedEndSec = (currentSec / cadence) * cadence;
        var windowEnd = DateTimeOffset.FromUnixTimeSeconds(alignedEndSec);
        var windowStart = windowEnd.AddSeconds(-policy.EventWindowSeconds);

        // Check if window is in the future or not yet completed
        if (windowEnd > now) return;

        // Check if already evaluated
        if (policy.LastEvaluatedWindowStart.HasValue && policy.LastEvaluatedWindowStart.Value >= windowStart)
        {
            return;
        }

        // S24: Handle skipped gaps
        if (policy.LastEvaluatedWindowStart.HasValue)
        {
            var gapStart = policy.LastEvaluatedWindowStart.Value.AddSeconds(cadence);
            while (gapStart < windowStart)
            {
                var gapRun = new AnomalyRunEntity
                {
                    Id = Guid.NewGuid().ToString("N"),
                    PolicyId = policy.Id,
                    OwnerGroup = policy.OwnerGroup,
                    WindowStart = gapStart,
                    WindowEnd = gapStart.AddSeconds(policy.EventWindowSeconds),
                    Status = AnomalyRunStatuses.NoSignal,
                    Reason = "skipped_gap",
                    EvaluatedPolicyVersion = policy.Version,
                    WorkerJobId = jobId,
                    CreatedAt = now,
                    CompletedAt = now
                };

                // Deduplicate check
                bool gapExists = await db.AnomalyRuns.AnyAsync(
                    r => r.PolicyId == policy.Id && r.OwnerGroup == policy.OwnerGroup && r.WindowStart == gapStart,
                    cancellationToken).ConfigureAwait(false);

                if (!gapExists)
                {
                    db.AnomalyRuns.Add(gapRun);
                }

                gapStart = gapStart.AddSeconds(cadence);
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // S06, S07, S23: Deduplicate via DB constraint / existence check
        bool exists = await db.AnomalyRuns.AnyAsync(
            r => r.PolicyId == policy.Id && r.OwnerGroup == policy.OwnerGroup && r.WindowStart == windowStart,
            cancellationToken).ConfigureAwait(false);

        if (exists)
        {
            policy.LastEvaluatedWindowStart = windowStart;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Insert Running record
        var run = new AnomalyRunEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            PolicyId = policy.Id,
            OwnerGroup = policy.OwnerGroup,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Status = AnomalyRunStatuses.Running,
            EvaluatedPolicyVersion = policy.Version,
            WorkerJobId = jobId,
            CreatedAt = now
        };

        db.AnomalyRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another worker inserted concurrently (S07)
            return;
        }

        AnomalyEvaluationResult result;
        try
        {
            result = await evaluator.EvaluateAsync(policy, windowStart, windowEnd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new AnomalyEvaluationResult(AnomalyRunStatuses.Failed, $"evaluator_error:{ex.GetType().Name}", null, null, null);
        }

        run.ObservedValue = result.ObservedValue;
        run.BaselineValue = result.BaselineValue;
        run.Deviation = result.Deviation;
        run.CompletedAt = _clock.GetUtcNow();

        if (result.Status == AnomalyRunStatuses.Triggered)
        {
            // S25: Admission + quota check
            var admissionReq = RcaTriggerSources.FromAnomaly(policy.Id, policy.OwnerGroup, windowStart, windowEnd);
            var admissionRes = await _admission.AdmitAsync(admissionReq, cancellationToken).ConfigureAwait(false);

            if (admissionRes.Accepted)
            {
                run.Status = AnomalyRunStatuses.Triggered;
                run.Reason = null;
                run.RcaRunId = admissionRes.Run.Id;
            }
            else
            {
                run.Status = AnomalyRunStatuses.Suppressed;
                run.Reason = admissionRes.Rejection == RcaRejectionReason.QuotaExceeded
                    ? "quota"
                    : admissionRes.Rejection.ToString().ToLowerInvariant();
            }
        }
        else
        {
            run.Status = result.Status;
            run.Reason = result.Reason;
        }

        policy.LastEvaluatedWindowStart = windowStart;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
