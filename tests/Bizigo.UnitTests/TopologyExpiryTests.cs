using Bizigo.Contracts;

namespace Bizigo.UnitTests;

public sealed partial class TopologyIdentityTests
{
    [Theory]
    [InlineData(30, 90, 90, 30)]
    [InlineData(90, 30, 90, 30)]
    [InlineData(90, 90, 10, 10)]
    public void Effective_expiry_minimum(int parentDays, int childDays, int observedDays, int expectedDays)
    {
        var captured = TopologyExpiry.CaptureDays(0, parentDays, 0, childDays, observedDays);
        var expected = (UInt128)expectedDays * TopologyExpiry.NanosecondsPerDay;
        Assert.Equal(expected, captured.EffectiveExpiryUnixNano);
        Assert.True(TopologyExpiry.IsReadable(expected - 1, captured.EffectiveExpiryUnixNano));
        Assert.False(TopologyExpiry.IsReadable(expected, captured.EffectiveExpiryUnixNano));
        Assert.False(TopologyExpiry.IsReadable(expected + 1, captured.EffectiveExpiryUnixNano));
    }

    [Fact]
    public void Captured_retention_cannot_be_extended_by_new_configuration()
    {
        var old = TopologyExpiry.CaptureDays(100, 30, 200, 30, 30);
        var afterConfig365 = TopologyExpiry.CaptureDays(100, 365, 200, 365, 365);
        Assert.True(old.EffectiveExpiryUnixNano < afterConfig365.EffectiveExpiryUnixNano);
        Assert.Equal(old.EffectiveExpiryUnixNano, TopologyExpiry.CanonicalEffective([afterConfig365, old]));
        Assert.Equal(old.EffectiveExpiryUnixNano, TopologyExpiry.CanonicalEffective([old, afterConfig365]));
    }

    [Fact]
    public void Decimal_to_UInt128_math_saturates_without_overflow()
    {
        var captured = TopologyExpiry.Capture(decimal.MaxValue, decimal.MaxValue, decimal.MaxValue,
            decimal.MaxValue, decimal.MaxValue, decimal.MaxValue);
        Assert.Equal((UInt128)decimal.MaxValue * 2, captured.EffectiveExpiryUnixNano);
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyExpiry.ToDecimal(captured.EffectiveExpiryUnixNano));
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyExpiry.FromDecimal(1.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => TopologyExpiry.FromDecimal(-1));
    }
}
