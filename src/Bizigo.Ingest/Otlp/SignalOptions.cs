namespace Bizigo.Ingest.Otlp;

public sealed class SignalOptions
{
    public const string SectionName = "Ingest:Signals";
    /// <summary>Defaults to a separate telemetry child of Ingest:Wal:Directory.</summary>
    public string Directory { get; set; } = string.Empty;
    public int ChannelCapacity { get; set; } = 128;
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Captured at admission; null follows the typed trace retention.</summary>
    public int? ObservedRetentionDays { get; set; }
}

public interface ISignalCheckpoints
{
    Task ReachAsync(string checkpoint, CancellationToken token);
}

public sealed class SignalCheckpoints : ISignalCheckpoints
{
    public Task ReachAsync(string checkpoint, CancellationToken token) => Task.CompletedTask;
}
