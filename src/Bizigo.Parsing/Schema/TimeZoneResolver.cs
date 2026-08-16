using System.Collections.Concurrent;

namespace Bizigo.Parsing.Schema;

/// <summary>
/// IANA saat dilimi çözümü. Ağ cihazları yerel saatle log basar ve saat dilimini
/// çoğu zaman yazmaz; <c>default_timezone</c> bu yüzden şemada zorunlu bir kavram.
/// </summary>
public static class TimeZoneResolver
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string id) => Resolve(id) is not null;

    public static TimeZoneInfo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Cache.GetOrAdd(id, static key =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        });
    }
}
