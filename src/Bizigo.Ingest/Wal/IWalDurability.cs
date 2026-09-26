namespace Bizigo.Ingest.Wal;

/// <summary>Explicit fault-injection seam; production always calls Flush(true).</summary>
public interface IWalDurability
{
    ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken);
}

public sealed class FileWalDurability : IWalDurability
{
    public ValueTask FlushAsync(FileStream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
        return ValueTask.CompletedTask;
    }
}
