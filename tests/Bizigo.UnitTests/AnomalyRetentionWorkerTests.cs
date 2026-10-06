using Bizigo.Api.Anomaly;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bizigo.UnitTests;

public sealed class AnomalyRetentionWorkerTests : IDisposable
{
    private readonly InMemoryControlPlaneFactory _factory = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RetentionWorker_PurgesOnlyUnlinkedRunsOlderThan90Days()
    {
        var now = _clock.GetUtcNow();
        var olderThan90Days = now.AddDays(-91);
        var recent = now.AddDays(-30);

        await using (var db = _factory.CreateDbContext())
        {
            // 1. Expired and unlinked -> should be purged
            db.AnomalyRuns.Add(new AnomalyRunEntity
            {
                Id = "run-expired-unlinked",
                PolicyId = "pol-1",
                OwnerGroup = "core",
                WindowStart = olderThan90Days.AddMinutes(-5),
                WindowEnd = olderThan90Days,
                Status = AnomalyRunStatuses.NoSignal,
                EvaluatedPolicyVersion = 1,
                WorkerJobId = "job-1",
                RcaRunId = null,
                CreatedAt = olderThan90Days,
                CompletedAt = olderThan90Days
            });

            // 2. Expired but LINKED to RCA -> MUST BE RETAINED (S28)
            db.AnomalyRuns.Add(new AnomalyRunEntity
            {
                Id = "run-expired-linked",
                PolicyId = "pol-1",
                OwnerGroup = "core",
                WindowStart = olderThan90Days.AddMinutes(-5),
                WindowEnd = olderThan90Days,
                Status = AnomalyRunStatuses.Triggered,
                EvaluatedPolicyVersion = 1,
                WorkerJobId = "job-2",
                RcaRunId = Guid.NewGuid(),
                CreatedAt = olderThan90Days,
                CompletedAt = olderThan90Days
            });

            // 3. Recent unlinked (< 90 days) -> should be retained
            db.AnomalyRuns.Add(new AnomalyRunEntity
            {
                Id = "run-recent-unlinked",
                PolicyId = "pol-1",
                OwnerGroup = "core",
                WindowStart = recent.AddMinutes(-5),
                WindowEnd = recent,
                Status = AnomalyRunStatuses.NoSignal,
                EvaluatedPolicyVersion = 1,
                WorkerJobId = "job-3",
                RcaRunId = null,
                CreatedAt = recent,
                CompletedAt = recent
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var worker = new AnomalyRetentionWorker(
            _factory,
            _clock,
            NullLogger<AnomalyRetentionWorker>.Instance,
            new AnomalyRetentionWorkerOptions { RetentionDays = 90 });

        int purgedCount = await worker.PurgeExpiredRunsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, purgedCount);

        await using (var db = _factory.CreateDbContext())
        {
            var remaining = await db.AnomalyRuns.Select(r => r.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Contains("run-expired-linked", remaining);
            Assert.Contains("run-recent-unlinked", remaining);
            Assert.DoesNotContain("run-expired-unlinked", remaining);

            // Verify audit log entry
            var audit = await db.AuditLog.SingleOrDefaultAsync(a => a.Action == "anomaly.runs.retention_purged", TestContext.Current.CancellationToken);
            Assert.NotNull(audit);
            Assert.Equal(1, audit.RowCount);
        }
    }
}
