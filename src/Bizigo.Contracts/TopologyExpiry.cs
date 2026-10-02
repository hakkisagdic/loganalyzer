namespace Bizigo.Contracts;

/// <summary>
/// Bir observed edge occurrence'ının kabul anında yakaladığı süreler.
/// Sonraki retention ayarı bu değeri değiştiremez; replay aynı expiry'leri
/// yeniden kullanır.
/// </summary>
public sealed record CapturedTopologyExpiry(
    UInt128 ParentTraceRetentionNano,
    UInt128 ChildTraceRetentionNano,
    UInt128 ObservedRetentionNano,
    UInt128 ParentTraceExpiryUnixNano,
    UInt128 ChildTraceExpiryUnixNano,
    UInt128 ObservedExpiryUnixNano)
{
    public UInt128 EffectiveExpiryUnixNano => TopologyExpiry.Effective(
        ParentTraceExpiryUnixNano,
        ChildTraceExpiryUnixNano,
        ObservedExpiryUnixNano);
}

/// <summary>Topology TTL hesabının tek, taşmasız kapısı.</summary>
public static class TopologyExpiry
{
    public const ulong NanosecondsPerDay = 86_400_000_000_000;

    public static CapturedTopologyExpiry CaptureDays(
        decimal parentTraceTimeUnixNano,
        int parentCapturedDays,
        decimal childTraceTimeUnixNano,
        int childCapturedDays,
        int observedCapturedDays)
    {
        if (parentCapturedDays is < 1 or > 36500 || childCapturedDays is < 1 or > 36500
            || observedCapturedDays is < 1 or > 36500)
            throw new ArgumentOutOfRangeException(nameof(parentCapturedDays), "Captured retention must be 1..36500 days.");
        return Capture(parentTraceTimeUnixNano, (decimal)parentCapturedDays * NanosecondsPerDay,
            childTraceTimeUnixNano, (decimal)childCapturedDays * NanosecondsPerDay,
            childTraceTimeUnixNano, (decimal)observedCapturedDays * NanosecondsPerDay);
    }

    public static UInt128 CanonicalEffective(IEnumerable<CapturedTopologyExpiry> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        using var iterator = occurrences.GetEnumerator();
        if (!iterator.MoveNext()) throw new ArgumentException("At least one occurrence is required.", nameof(occurrences));
        var minimum = iterator.Current.EffectiveExpiryUnixNano;
        while (iterator.MoveNext()) minimum = UInt128.Min(minimum, iterator.Current.EffectiveExpiryUnixNano);
        return minimum;
    }

    public static CapturedTopologyExpiry Capture(
        decimal parentTraceTimeUnixNano,
        decimal parentTraceRetentionNano,
        decimal childTraceTimeUnixNano,
        decimal childTraceRetentionNano,
        decimal observedTimeUnixNano,
        decimal observedRetentionNano)
    {
        var parentTime = FromDecimal(parentTraceTimeUnixNano);
        var parentRetention = FromDecimal(parentTraceRetentionNano);
        var childTime = FromDecimal(childTraceTimeUnixNano);
        var childRetention = FromDecimal(childTraceRetentionNano);
        var observedTime = FromDecimal(observedTimeUnixNano);
        var observationRetention = FromDecimal(observedRetentionNano);

        return new(
            parentRetention,
            childRetention,
            observationRetention,
            AddSaturating(parentTime, parentRetention),
            AddSaturating(childTime, childRetention),
            AddSaturating(observedTime, observationRetention));
    }

    public static UInt128 Effective(
        UInt128 parentTraceExpiryUnixNano,
        UInt128 childTraceExpiryUnixNano,
        UInt128 observedExpiryUnixNano) =>
        UInt128.Min(parentTraceExpiryUnixNano, UInt128.Min(childTraceExpiryUnixNano, observedExpiryUnixNano));

    public static decimal Effective(decimal parentTraceExpiryUnixNano, decimal childTraceExpiryUnixNano, decimal observedExpiryUnixNano)
    {
        var effective = Effective(
            FromDecimal(parentTraceExpiryUnixNano),
            FromDecimal(childTraceExpiryUnixNano),
            FromDecimal(observedExpiryUnixNano));
        return ToDecimal(effective);
    }

    /// <summary>Expiry sınırı yarı açık: yalnızca <c>readClockNano &lt; expiry</c>.</summary>
    public static bool IsReadable(UInt128 readClockNano, UInt128 effectiveExpiryUnixNano) =>
        readClockNano < effectiveExpiryUnixNano;

    public static bool IsReadable(decimal readClockNano, decimal effectiveExpiryUnixNano) =>
        IsReadable(FromDecimal(readClockNano), FromDecimal(effectiveExpiryUnixNano));

    public static UInt128 FromDecimal(decimal value)
    {
        if (value < 0 || value != decimal.Truncate(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Nanosecond values must be non-negative integers.");
        }

        return (UInt128)value;
    }

    public static decimal ToDecimal(UInt128 value)
    {
        if (value > (UInt128)decimal.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Nanosecond value does not fit Decimal128 storage.");
        }

        return (decimal)value;
    }

    private static UInt128 AddSaturating(UInt128 left, UInt128 right) =>
        UInt128.MaxValue - left < right ? UInt128.MaxValue : left + right;
}
