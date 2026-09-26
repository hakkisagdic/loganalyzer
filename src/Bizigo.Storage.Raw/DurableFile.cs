namespace Bizigo.Storage.Raw;

/// <summary>One atomic durable file; an existing final is never a partial write.</summary>
public static class DurableFile
{
    public static async Task WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token = default,
        Func<string, CancellationToken, Task>? checkpoint = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, token);
                stream.Flush(flushToDisk: true);
            }
            if (checkpoint is not null) await checkpoint("before-rename", token);
            File.Move(temporary, path, overwrite: true);
            if (checkpoint is not null) await checkpoint("after-rename", token);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
