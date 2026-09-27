using Bizigo.Contracts;
using Bizigo.Storage.ClickHouse;

namespace Bizigo.UnitTests;

public sealed class TelemetryQueryTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("detail")]
    [InlineData("summary")]
    public async Task Invalid_typed_storage_data_returns_failed_without_rows(string operation)
    {
        using var context = new ClickHouseContext(new());
        var reader = new TelemetryReader(context)
        {
            ObserveQuery = _ => throw new InvalidDataException("Fixture corrupt stored record"),
        };
        var query = new TelemetryQuery { Signal = TelemetrySignal.Metrics, FromNano = 1, ToNano = 2 };
        var scope = ScopePredicate.From(AccessScope.ForGroups("a", ["A"]));
        var token = TestContext.Current.CancellationToken;
        ITelemetryResult result = operation switch
        {
            "detail" => await reader.MetricDetailAsync("stored-point", scope, token),
            "summary" => await reader.SummaryAsync(query, scope, token),
            _ => await reader.SearchAsync(query, scope, token),
        };
        Assert.Equal(TelemetryResultStatus.Failed, result.Status);
        Assert.Equal(0, result.RowCount);
        Assert.False(result.Partial);
        Assert.Equal("InvalidTypedRecord", result switch
        {
            TelemetryPage page => page.Error,
            TelemetrySummaryPage summary => summary.Error,
            _ => null,
        });
    }

    [Theory]
    [InlineData(-1, 3, 100)]
    [InlineData(3, 3, 100)]
    [InlineData(4, 3, 100)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 1, 1001)]
    public void Invalid_window_and_limit_never_reach_database(long from, long to, int limit)
    {
        var query = new TelemetryQuery { Signal = TelemetrySignal.Metrics, FromNano = from, ToNano = to, Limit = limit };
        Assert.Throws<ArgumentException>(query.Validate);
    }

    [Fact]
    public void Full_unsigned_wire_boundary_is_representable_without_datetime_rounding()
    {
        var query = new TelemetryQuery { Signal = TelemetrySignal.Metrics, FromNano = ulong.MaxValue, ToNano = (decimal)ulong.MaxValue + 1 };
        query.Validate();
        Assert.Throws<ArgumentException>(() => (query with { FromNano = 1.1m }).Validate());
        Assert.Throws<ArgumentException>(() => (query with { FromNano = 0 }).Validate());
    }

    [Fact]
    public async Task Invalid_cursor_is_rejected_before_connection_and_cannot_change_scope()
    {
        using var context = new ClickHouseContext(new());
        var reader = new TelemetryReader(context);
        var attempts = 0; reader.ObserveQuery = _ => attempts++;
        var query = new TelemetryQuery { Signal = TelemetrySignal.Metrics, FromNano = 1, ToNano = 2, Cursor = "not-a-cursor" };
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SearchAsync(query, ScopePredicate.From(AccessScope.ForGroups("a", ["A"])), TestContext.Current.CancellationToken));
        Assert.Equal(0, attempts);
    }
}
