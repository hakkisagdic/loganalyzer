using Bizigo.Api.Anomaly;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Rca;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bizigo.UnitTests;

public sealed class AnomalyWorkerTests : IDisposable
{
    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _factory.Dispose();

    private sealed class DenyQuotaGate : IRcaQuotaGate
    {
        public ValueTask<RcaRejectionReason> CheckAsync(RcaTriggerRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(RcaRejectionReason.QuotaExceeded);
    }

    private sealed class FakeAnomalyEvaluator : IAnomalyEvaluator
    {
        public AnomalyEvaluationResult Result { get; set; } = new(AnomalyRunStatuses.NoSignal, "within_threshold", 10, 10, 1.0);

        public Task<AnomalyEvaluationResult> EvaluateAsync(
            AnomalyPolicyEntity policy,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private (AnomalyWorker worker, FakeAnomalyEvaluator evaluator) CreateWorker(IRcaQuotaGate? quota = null)
    {
        var fakeEvaluator = new FakeAnomalyEvaluator();
        var services = new ServiceCollection();
        services.AddScoped<IAnomalyEvaluator>(_ => fakeEvaluator);
        var provider = services.BuildServiceProvider();

        var admission = new RcaAdmission(
            _factory,
            quota ?? new AlwaysAllowQuotaGate(),
            NullLogger<RcaAdmission>.Instance,
            _clock);

        var worker = new AnomalyWorker(
            _factory,
            provider.GetRequiredService<IServiceScopeFactory>(),
            admission,
            _clock,
            NullLogger<AnomalyWorker>.Instance);

        return (worker, fakeEvaluator);
    }

    [Fact]
    public async Task RecoverOrphanedRuns_MarksStuckRunningJobsAsFailed()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyRuns.Add(new AnomalyRunEntity
            {
                Id = "run-stuck-1",
                PolicyId = "pol-1",
                OwnerGroup = "core",
                WindowStart = _clock.GetUtcNow().AddMinutes(-10),
                WindowEnd = _clock.GetUtcNow().AddMinutes(-5),
                Status = AnomalyRunStatuses.Running,
                EvaluatedPolicyVersion = 1,
                WorkerJobId = "job-old",
                CreatedAt = _clock.GetUtcNow().AddMinutes(-10)
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, _) = CreateWorker();
        await worker.RecoverOrphanedRunsAsync(TestContext.Current.CancellationToken);

        await using (var db = _factory.CreateDbContext())
        {
            var run = await db.AnomalyRuns.FindAsync(["run-stuck-1"], TestContext.Current.CancellationToken);
            Assert.NotNull(run);
            Assert.Equal(AnomalyRunStatuses.Failed, run.Status);
            Assert.Equal("unrecovered_restart", run.Reason);
            Assert.NotNull(run.CompletedAt);
        }
    }

    [Fact]
    public async Task RunTurn_BackfillsSkippedGapsWithNoSignal()
    {
        var now = _clock.GetUtcNow();
        var initialWindowStart = now.AddSeconds(-240); // 4 cadences ago

        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-gap-1",
                OwnerGroup = "core",
                Name = "Gap Policy",
                Signal = AnomalySignals.LogEventCount,
                Target = "app-log",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 1,
                LastEvaluatedWindowStart = initialWindowStart
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, _) = CreateWorker();
        await worker.RunTurnAsync(TestContext.Current.CancellationToken);

        await using (var db = _factory.CreateDbContext())
        {
            var runs = await db.AnomalyRuns
                .Where(r => r.PolicyId == "pol-gap-1")
                .OrderBy(r => r.WindowStart)
                .ToListAsync(TestContext.Current.CancellationToken);

            // Should have 3 runs: 2 backfilled gaps with reason 'skipped_gap' + 1 current run
            Assert.True(runs.Count >= 3);
            var gaps = runs.Where(r => r.Reason == "skipped_gap").ToList();
            Assert.True(gaps.Count >= 2);
            Assert.All(gaps, g => Assert.Equal(AnomalyRunStatuses.NoSignal, g.Status));
        }
    }

    [Fact]
    public async Task RunTurn_DeduplicatesExecutionOnSameWindow()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-dedup-1",
                OwnerGroup = "core",
                Name = "Dedup Policy",
                Signal = AnomalySignals.LogEventCount,
                Target = "app-log",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 1
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, _) = CreateWorker();

        // Turn 1
        await worker.RunTurnAsync(TestContext.Current.CancellationToken);

        int countAfterFirst;
        await using (var db = _factory.CreateDbContext())
        {
            countAfterFirst = await db.AnomalyRuns.CountAsync(r => r.PolicyId == "pol-dedup-1", TestContext.Current.CancellationToken);
            Assert.Equal(1, countAfterFirst);
        }

        // Turn 2 immediately (clock has not advanced to next cadence boundary)
        await worker.RunTurnAsync(TestContext.Current.CancellationToken);

        await using (var db = _factory.CreateDbContext())
        {
            int countAfterSecond = await db.AnomalyRuns.CountAsync(r => r.PolicyId == "pol-dedup-1", TestContext.Current.CancellationToken);
            Assert.Equal(countAfterFirst, countAfterSecond);
        }
    }

    [Fact]
    public async Task RunTurn_TriggeredAnomaly_AdmitsRcaAndRecordsRcaRunId()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-trig-1",
                OwnerGroup = "core",
                Name = "Trigger Policy",
                Signal = AnomalySignals.LogEventCount,
                Target = "app-log",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 1
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, evaluator) = CreateWorker();
        evaluator.Result = new AnomalyEvaluationResult(
            AnomalyRunStatuses.Triggered,
            null,
            ObservedValue: 500,
            BaselineValue: 100,
            Deviation: 5.0);

        await worker.RunTurnAsync(TestContext.Current.CancellationToken);

        await using (var db = _factory.CreateDbContext())
        {
            var run = await db.AnomalyRuns.SingleAsync(r => r.PolicyId == "pol-trig-1", TestContext.Current.CancellationToken);
            Assert.Equal(AnomalyRunStatuses.Triggered, run.Status);
            Assert.Null(run.Reason);
            Assert.NotNull(run.RcaRunId);

            // Verify RCA run exists in DB
            var rcaRun = await db.RcaRuns.FindAsync([run.RcaRunId.Value], TestContext.Current.CancellationToken);
            Assert.NotNull(rcaRun);
            Assert.Equal(RcaTriggerSource.Anomaly, rcaRun.Source);
        }
    }

    [Fact]
    public async Task RunTurn_TriggeredAnomaly_WhenQuotaExceeded_SuppressesWithReasonQuota()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.AnomalyPolicies.Add(new AnomalyPolicyEntity
            {
                Id = "pol-supp-1",
                OwnerGroup = "core",
                Name = "Suppressed Policy",
                Signal = AnomalySignals.LogEventCount,
                Target = "app-log",
                EventWindowSeconds = 60,
                BaselineWindowSeconds = 300,
                Sensitivity = 2.0,
                MinSamples = 3,
                CadenceSeconds = 60,
                State = AnomalyPolicyStates.Enabled,
                Version = 1
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (worker, evaluator) = CreateWorker(new DenyQuotaGate());
        evaluator.Result = new AnomalyEvaluationResult(
            AnomalyRunStatuses.Triggered,
            null,
            ObservedValue: 500,
            BaselineValue: 100,
            Deviation: 5.0);

        await worker.RunTurnAsync(TestContext.Current.CancellationToken);

        await using (var db = _factory.CreateDbContext())
        {
            var run = await db.AnomalyRuns.SingleAsync(r => r.PolicyId == "pol-supp-1", TestContext.Current.CancellationToken);
            Assert.Equal(AnomalyRunStatuses.Suppressed, run.Status);
            Assert.Equal("quota", run.Reason);
            Assert.Null(run.RcaRunId);
        }
    }
}
