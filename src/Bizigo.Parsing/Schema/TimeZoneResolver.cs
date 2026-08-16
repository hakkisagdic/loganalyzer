using System.Collections.Concurrent;
using System.Globalization;

namespace Bizigo.Parsing.Schema;

/// <summary>
/// Saat dilimi çözümü. Ağ cihazları yerel saatle log basar ve saat dilimini
/// çoğu zaman yazmaz; <c>default_timezone</c> bu yüzden şemada zorunlu bir kavram.
///
/// <para>
/// <b>IANA kimliği tek biçim değil.</b> Gerçek cihaz çıktısı saat dilimini
/// sayısal ofset olarak da yazıyor — FortiGate <c>tz="-0500"</c>, RFC5424
/// damgaları <c>+03:00</c>, UTC için <c>Z</c>. Bunlar <c>FindSystemTimeZoneById</c>
/// ile çözülemez ve yalnızca IANA denenirse <c>timezone_field</c> pratikte
/// işlevsiz kalır (T08 geri beslemesi, madde 1).
/// </para>
/// </summary>
public static class TimeZoneResolver
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string id) => Resolve(id) is not null;

    public static TimeZoneInfo? Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Cache.GetOrAdd(id, static key =>
            TryResolveOffset(key.Trim(), out var offsetZone) ? offsetZone : ResolveIana(key));
    }

    private static TimeZoneInfo? ResolveIana(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Z</c>, <c>±HHmm</c>, <c>±HH:mm</c>, <c>±HH</c> biçimlerini sabit ofsetli
    /// bir dilime çevirir.
    ///
    /// <para>
    /// Sabit ofset yaz saatini bilmez — ama cihaz zaten o anki gerçek ofseti
    /// yazdığı için doğru olan bu. IANA kimliğine çevirmeye çalışmak (aynı ofseti
    /// paylaşan onlarca bölgeden birini seçmek) tahmin olurdu.
    /// </para>
    /// </summary>
    private static bool TryResolveOffset(string value, out TimeZoneInfo? zone)
    {
        zone = null;

        if (value.Length == 0)
        {
            return false;
        }

        if (value is "Z" or "z" or "UTC" or "GMT")
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        var sign = value[0];
        if (sign is not ('+' or '-'))
        {
            return false;
        }

        var digits = value[1..].Replace(":", string.Empty, StringComparison.Ordinal);

        int hours;
        var minutes = 0;

        switch (digits.Length)
        {
            case 2:
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                {
                    return false;
                }

                break;

            case 4:
                if (!int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out hours)
                    || !int.TryParse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
                {
                    return false;
                }

                break;

            default:
                return false;
        }

        if (hours > 14 || minutes > 59)
        {
            return false;
        }

        var offset = new TimeSpan(hours, minutes, 0);
        if (sign == '-')
        {
            offset = offset.Negate();
        }

        zone = offset == TimeSpan.Zero
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.CreateCustomTimeZone(
                string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{hours:D2}:{minutes:D2}"),
                offset,
                displayName: null,
                standardDisplayName: null);

        return true;
    }
}
