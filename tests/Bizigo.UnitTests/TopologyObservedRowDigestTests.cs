using Bizigo.Storage.ClickHouse;
using System.Reflection;

namespace Bizigo.UnitTests;

public sealed class TopologyObservedRowDigestTests
{
    [Fact]
    public void Utc_writer_and_unix_second_reader_have_identical_digest()
    {
        var writer = Row();
        var reader = (object?[])writer.Clone();
        reader[38] = new DateTimeOffset((DateTime)writer[38]!).ToUnixTimeSeconds();

        Assert.Equal(40, TopologyObservedRowDigest.FrozenColumns.Count);
        Assert.Equal("owner_group", TopologyObservedRowDigest.FrozenColumns[0]);
        Assert.Equal("ttl_supported", TopologyObservedRowDigest.FrozenColumns[39]);
        var frozenProjectorColumns = typeof(TopologyObservedProjector).GetField("EdgeColumns",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(frozenProjectorColumns);
        Assert.Equal(Assert.IsType<string[]>(frozenProjectorColumns.GetValue(null)),
            TopologyObservedRowDigest.FrozenColumns);
        Assert.Equal(TopologyObservedRowDigest.Compute(writer), TopologyObservedRowDigest.Compute(reader));
    }

    [Fact]
    public void Captured_expiry_and_occurrence_order_change_digest()
    {
        var original = Row();
        var expiryChanged = (object?[])original.Clone();
        expiryChanged[35] = (decimal)expiryChanged[35]! + 1;
        var reordered = (object?[])original.Clone();
        reordered[31] = new[] { "child-occurrence", "parent-occurrence" };

        var digest = TopologyObservedRowDigest.Compute(original);
        Assert.NotEqual(digest, TopologyObservedRowDigest.Compute(expiryChanged));
        Assert.NotEqual(digest, TopologyObservedRowDigest.Compute(reordered));
    }

    [Fact]
    public void Invalid_clock_float_or_fixed_identifier_fails_closed()
    {
        var localClock = Row();
        localClock[38] = DateTime.SpecifyKind((DateTime)localClock[38]!, DateTimeKind.Local);
        Assert.Throws<InvalidDataException>(() => TopologyObservedRowDigest.Compute(localClock));

        var nonFinite = Row();
        nonFinite[8] = float.NaN;
        Assert.Throws<InvalidDataException>(() => TopologyObservedRowDigest.Compute(nonFinite));

        var malformedId = Row();
        malformedId[9] = "EDGE-NAME";
        Assert.Throws<InvalidDataException>(() => TopologyObservedRowDigest.Compute(malformedId));
    }

    private static object?[] Row() =>
    [
        "A", "A", "B", "service:parent", "service:child", "depends_on", (byte)1,
        "observed", 0.5f, new string('a', 64), "trace-id", "parent-span", "child-span",
        new string('b', 64), new string('c', 64), new string('d', 64), new string('e', 64),
        "source-parent", "source-child", 3L, 4L, 5L, 6L, 7L, 8L,
        100UL, 101UL, 101UL, 101UL,
        new[] { "parent-occurrence" }, new[] { "child-occurrence" },
        new[] { "parent-occurrence", "child-occurrence" },
        1_000_000_000m, 1_000_000_001m, 1_000_000_002m, 1_000_000_000m,
        new string('f', 64), 1UL, DateTime.UnixEpoch.AddSeconds(1), (byte)1,
    ];
}
