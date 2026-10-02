using System.Text.Json;

namespace Bizigo.Api;

/// <summary>Retries one stable query page at a smaller item limit until its wire body fits.</summary>
public static class TopologyResponseBudget
{
    public const int MaximumBytes = 1_048_576;

    public static bool Fits<T>(T body, JsonSerializerOptions options, int maximumBytes = MaximumBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(body, options).Length <= maximumBytes;

    public static async Task<T> FitPageAsync<T>(int requestedItems, Func<int, Task<T>> buildPage,
        JsonSerializerOptions options, int maximumBytes = MaximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentNullException.ThrowIfNull(buildPage);
        for (var size = requestedItems; ; size = Math.Max(1, size / 2))
        {
            var body = await buildPage(size);
            if (Fits(body, options, maximumBytes)) return body;
            if (size == 1) throw new TopologyRecordTooLargeException();
        }
    }
}

public sealed class TopologyRecordTooLargeException : Exception { }
