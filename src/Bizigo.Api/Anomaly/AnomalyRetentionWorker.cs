using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bizigo.Api.Anomaly;

public sealed class AnomalyRetentionWorkerOptions
{
    public const string SectionName = "Anomaly:Retention";
    public bool Enabled { get; set; } = true;
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
    public int RetentionDays { get; set; } = 90;
}

public sealed class AnomalyRetentionWorker(
    IDbContextFactory<ControlPlaneDbContext> dbFactory,
    TimeProvider clock,
    ILogger<AnomalyRetentionWorker> logger,
    AnomalyRetentionWorkerOptions? options = null) : BackgroundService
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger<AnomalyRetentionWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly AnomalyRetentionWorkerOptions _options = options ?? new AnomalyRetentionWorkerOptions();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("AnomalyRetentionWorker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredRunsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing AnomalyRetentionWorker.");
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

    public async Task<int> PurgeExpiredRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var cutoff = now.AddDays(-_options.RetentionDays);

        // S27: Sadece RCA-bağsız terminal runlar 90 gün sonunda silinebilir. RCA bağlı olanlar korunur!
        var expiredUnlinked = await db.AnomalyRuns
            .Where(r => r.RcaRunId == null && r.CompletedAt != null && r.CompletedAt < cutoff)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (expiredUnlinked.Count == 0) return 0;

        db.AnomalyRuns.RemoveRange(expiredUnlinked);

        // Retention audit log
        db.AuditLog.Add(new AuditLogEntity
        {
            At = now,
            Subject = "system:anomaly-retention",
            Action = "anomaly.runs.retention_purged",
            Resource = "anomaly_runs",
            Details = $"Purged {expiredUnlinked.Count} unlinked anomaly runs older than {_options.RetentionDays} days.",
            RowCount = expiredUnlinked.Count,
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expiredUnlinked.Count;
    }
}
