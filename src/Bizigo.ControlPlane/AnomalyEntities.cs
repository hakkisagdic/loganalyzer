using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Bizigo.ControlPlane;

/// <summary>
/// S4 Anomaly Sinyali kapalı kümesi (S19).
/// </summary>
public static class AnomalySignals
{
    public const string LogEventCount = "log_event_count";
    public const string MetricSeriesSum = "metric_series_sum";
    public const string TraceErrorRate = "trace_error_rate";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        LogEventCount,
        MetricSeriesSum,
        TraceErrorRate,
    };

    public static bool IsValid(string? signal) =>
        signal is not null && Supported.Contains(signal);
}

/// <summary>
/// Anomaly run terminal durumları (S04).
/// </summary>
public static class AnomalyRunStatuses
{
    public const string Running = "Running";
    public const string NoSignal = "NoSignal";
    public const string Triggered = "Triggered";
    public const string Suppressed = "Suppressed";
    public const string Failed = "Failed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Running,
        NoSignal,
        Triggered,
        Suppressed,
        Failed,
    };
}

/// <summary>
/// Anomaly policy durumu (S17).
/// </summary>
public static class AnomalyPolicyStates
{
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Enabled,
        Disabled,
    };
}

/// <summary>
/// S4 Anomaly Policy Entity.
/// </summary>
[Table("anomaly_policies", Schema = ControlPlaneDbContext.Schema)]
public sealed class AnomalyPolicyEntity
{
    [Key]
    [Column("id")]
    [MaxLength(64)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Column("owner_group")]
    [MaxLength(128)]
    public string OwnerGroup { get; set; } = string.Empty;

    [Column("name")]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [Column("signal")]
    [MaxLength(64)]
    public string Signal { get; set; } = string.Empty;

    [Column("target")]
    [MaxLength(256)]
    public string Target { get; set; } = string.Empty;

    [Column("event_window_seconds")]
    public int EventWindowSeconds { get; set; }

    [Column("baseline_window_seconds")]
    public int BaselineWindowSeconds { get; set; }

    [Column("sensitivity")]
    public double Sensitivity { get; set; }

    [Column("min_samples")]
    public int MinSamples { get; set; }

    [Column("zero_baseline_min_absolute")]
    public double? ZeroBaselineMinAbsolute { get; set; }

    [Column("state")]
    [MaxLength(32)]
    public string State { get; set; } = AnomalyPolicyStates.Enabled;

    [Column("version")]
    public int Version { get; set; } = 1;

    [Column("cadence_seconds")]
    public int CadenceSeconds { get; set; } = 300;

    [Column("last_evaluated_window_start")]
    public DateTimeOffset? LastEvaluatedWindowStart { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// S4 Anomaly Run Entity.
/// </summary>
[Table("anomaly_runs", Schema = ControlPlaneDbContext.Schema)]
public sealed class AnomalyRunEntity
{
    [Key]
    [Column("id")]
    [MaxLength(64)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Column("policy_id")]
    [MaxLength(64)]
    public string PolicyId { get; set; } = string.Empty;

    [Column("owner_group")]
    [MaxLength(128)]
    public string OwnerGroup { get; set; } = string.Empty;

    [Column("window_start")]
    public DateTimeOffset WindowStart { get; set; }

    [Column("window_end")]
    public DateTimeOffset WindowEnd { get; set; }

    [Column("status")]
    [MaxLength(32)]
    public string Status { get; set; } = AnomalyRunStatuses.Running;

    [Column("reason")]
    [MaxLength(128)]
    public string? Reason { get; set; }

    [Column("observed_value")]
    public double? ObservedValue { get; set; }

    [Column("baseline_value")]
    public double? BaselineValue { get; set; }

    [Column("deviation")]
    public double? Deviation { get; set; }

    [Column("rca_run_id")]
    public Guid? RcaRunId { get; set; }

    [Column("evaluated_policy_version")]
    public int EvaluatedPolicyVersion { get; set; }

    [Column("worker_job_id")]
    [MaxLength(128)]
    public string? WorkerJobId { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("completed_at")]
    public DateTimeOffset? CompletedAt { get; set; }
}
