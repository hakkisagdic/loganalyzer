using System.Text.Json;
using Bizigo.Api;

namespace Bizigo.UnitTests;

public sealed class TopologyResponseBudgetTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Exact_one_mebibyte_wire_body_fits()
    {
        var empty = Page("");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, Options).Length;
        var exact = Page(new string('x', TopologyResponseBudget.MaximumBytes - overhead));
        Assert.Equal(TopologyResponseBudget.MaximumBytes,
            JsonSerializer.SerializeToUtf8Bytes(exact, Options).Length);
        Assert.True(TopologyResponseBudget.Fits(exact, Options));
    }

    [Fact]
    public async Task One_oversized_record_is_422_not_silently_truncated()
    {
        var empty = Page("");
        var overhead = JsonSerializer.SerializeToUtf8Bytes(empty, Options).Length;
        var oversized = Page(new string('x', TopologyResponseBudget.MaximumBytes - overhead + 1));
        await Assert.ThrowsAsync<TopologyRecordTooLargeException>(() =>
            TopologyResponseBudget.FitPageAsync(1, _ => Task.FromResult(oversized), Options));
    }

    [Fact]
    public async Task Aggregate_overflow_retries_smaller_page_with_continuation()
    {
        var requestedSizes = new List<int>();
        var names = new[] { new string('a', 600_000), new string('b', 600_000) };
        var page = await TopologyResponseBudget.FitPageAsync(2, size =>
        {
            requestedSizes.Add(size);
            return Task.FromResult(new TopologyNodePageDto(names.Take(size).Select(Node).ToArray(),
                size < names.Length ? "signed-next-page" : null, size < names.Length, null, "7"));
        }, Options);

        Assert.Equal([2, 1], requestedSizes);
        Assert.Single(page.Nodes);
        Assert.True(page.Partial);
        Assert.Equal("signed-next-page", page.Cursor);
        Assert.True(TopologyResponseBudget.Fits(page, Options));
    }

    private static TopologyNodePageDto Page(string displayName) =>
        new([Node(displayName)], null, false, null, "7");

    private static TopologyNodeDto Node(string displayName) =>
        new("service:id", "service", displayName, "A", true, "1", "100", null);
}
