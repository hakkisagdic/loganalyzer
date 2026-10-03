using Bizigo.Query;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Test-owned server clock. The production scoped gate obtains this from DI,
/// never from a request's asOf or a caller-supplied expiry field.
/// </summary>
internal sealed record FixedTopologyExpiryNanoClock(decimal Value) : ITopologyExpiryNanoClock
{
    public decimal NowUnixNano() => Value;
}

internal sealed class MutableTopologyExpiryNanoClock(decimal initial) : ITopologyExpiryNanoClock
{
    private readonly object gate = new();
    private decimal value = initial;

    public decimal NowUnixNano()
    { lock (gate) return value; }

    public void Set(decimal now)
    { lock (gate) value = now; }
}
