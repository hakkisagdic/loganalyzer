namespace Bizigo.Query;

/// <summary>
/// Authoritative server read clock for observed topology expiry. It is not a
/// caller query parameter: historical as-of cannot rewind evidence TTL.
/// Decimal nanoseconds let boundary tests and higher-resolution hosts retain
/// E−1/E/E+1 exactly, even though DateTimeOffset itself has 100 ns ticks.
/// </summary>
public interface ITopologyExpiryNanoClock
{
    decimal NowUnixNano();
}

public sealed class TimeProviderTopologyExpiryNanoClock(TimeProvider provider) : ITopologyExpiryNanoClock
{
    private readonly TimeProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public decimal NowUnixNano() => checked((decimal)_provider.GetUtcNow().UtcTicks * 100m
        - 62135596800000000000m);
}
