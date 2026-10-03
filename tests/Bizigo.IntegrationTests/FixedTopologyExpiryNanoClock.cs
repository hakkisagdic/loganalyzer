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
