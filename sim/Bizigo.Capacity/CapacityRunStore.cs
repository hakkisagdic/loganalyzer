using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Capacity;

public interface ICapacityRunStore
{
    Task<string> SaveAttemptAsync(CapacityAttemptRecord record, CancellationToken cancellationToken);
    Task SaveDiscoveryAsync(CapacityDiscoveryResult result, CancellationToken cancellationToken);
}

public static class CapacityJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>Atomic visibility on the same filesystem; this is not a power-loss guarantee.</summary>
public sealed class CapacityRunStore(string directory, Action<string>? afterFlush = null) : ICapacityRunStore
{
    public Task<string> SaveAttemptAsync(CapacityAttemptRecord record, CancellationToken cancellationToken) =>
        WriteAsync(record.Attempt.RunId, record, cancellationToken);

    public async Task SaveDiscoveryAsync(CapacityDiscoveryResult result, CancellationToken cancellationToken)
    {
        if (result.CapacityEps is not null)
        {
            if (result.ConfirmingRunIds.Count != CapacityDiscovery.RequiredPasses
                || result.ConfirmingRunIds.Distinct(StringComparer.Ordinal).Count() != CapacityDiscovery.RequiredPasses)
                throw new InvalidDataException("Capacity requires three distinct persisted attempts.");
            foreach (var id in result.ConfirmingRunIds)
            {
                var reference = result.Attempts.Single(a => a.RunId == id);
                var record = JsonSerializer.Deserialize<CapacityAttemptRecord>(await File.ReadAllTextAsync(reference.Path, cancellationToken), CapacityJson.Options)
                    ?? throw new InvalidDataException("Missing persisted attempt.");
                if (record.Attempt.RunId != id || record.Decision.Verdict != CapacityVerdict.Pass
                    || record.Attempt.EventsPerSecond != result.CapacityEps)
                    throw new InvalidDataException("Capacity confirmation does not match persisted evidence.");
            }
        }
        await WriteAsync(result.DiscoveryId + "-discovery", result, cancellationToken);
    }

    public async Task<string> WriteAsync<T>(string id, T value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 160
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Run identifier must contain ASCII letters, digits, '-' or '_'.", nameof(id));
        Directory.CreateDirectory(directory);
        var final = Path.Combine(Path.GetFullPath(directory), id + ".json");
        var temporary = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, value, CapacityJson.Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            afterFlush?.Invoke(temporary);
            File.Move(temporary, final, overwrite: false);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
