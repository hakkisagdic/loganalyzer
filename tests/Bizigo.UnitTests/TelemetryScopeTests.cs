using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.UnitTests;

public sealed class TelemetryScopeTests
{
    [Fact]
    public async Task Save_changes_captures_create_transfer_alias_and_disable_history()
    {
        using var factory = new InMemoryControlPlaneFactory();
        var token = TestContext.Current.CancellationToken;
        for (var step = 1; step <= 4; step++)
        {
            await using var db = factory.CreateDbContext();
            db.HistoryClock = new TelemetryOwnershipAdmissionTests.FixedClock(DateTimeOffset.UnixEpoch.AddSeconds(step));
            if (step == 1) db.Sources.Add(new SourceEntity { SourceId = "dev", OwnerGroup = "A", Hostname = "old" });
            else
            {
                var source = await db.Sources.SingleAsync(token);
                if (step == 2) source.OwnerGroup = "B";
                if (step == 3) source.Hostname = "new";
                if (step == 4) source.Enabled = false;
            }
            await db.SaveChangesAsync(token);
        }
        var resolver = new HistoricalTelemetryOwners(factory);
        async Task<TelemetryOwnerBinding> At(ulong nano, string source) =>
            Assert.Single(await resolver.ResolveAsync([new("leaf", nano, [source])], token));
        Assert.Equal("_unassigned", (await At(999999999, "dev")).OwnerGroup);
        Assert.Equal("A", (await At(1999999999, "old")).OwnerGroup);
        Assert.Equal("B", (await At(2000000000, "old")).OwnerGroup);
        Assert.Equal("_unassigned", (await At(3000000000, "old")).OwnerGroup);
        Assert.Equal("B", (await At(3000000001, "new")).OwnerGroup);
        Assert.Equal("disabled", (await At(4000000000, "dev")).Reason);
        await using var check = factory.CreateDbContext();
        var intervals = await check.SourceOwnershipHistory.OrderBy(h => h.EffectiveFromNano).ToArrayAsync(token);
        Assert.Equal(4, intervals.Length);
        for (var i = 0; i < 3; i++) Assert.Equal(intervals[i + 1].EffectiveFromNano, intervals[i].EffectiveToNano);
        Assert.Null(intervals[^1].EffectiveToNano);
    }

    [Fact]
    public void Candidate_precedence_and_ambiguous_history_never_pick_a_winner()
    {
        SourceOwnershipHistoryEntity[] history =
        [new() { Revision = 1, SourceId = "one", Hostname = "shared", OwnerGroup = "A", Enabled = true },
         new() { Revision = 2, SourceId = "two", Hostname = "shared", OwnerGroup = "B", Enabled = true }];
        var known = HistoricalTelemetryOwners.Resolve(new("leaf", 1, ["one", "two"]), history);
        var unknown = HistoricalTelemetryOwners.Resolve(new("leaf", 1, ["shared", "one"]), history);
        Assert.Equal("A", known.OwnerGroup); Assert.Equal("ambiguous", unknown.Reason);
        Assert.Equal("_unassigned", unknown.OwnerGroup);
    }

    [Fact]
    public void Migration_snapshot_matches_authoritative_model()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=not_connected").UseSnakeCaseNamingConvention().Options;
        using var db = new ControlPlaneDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
