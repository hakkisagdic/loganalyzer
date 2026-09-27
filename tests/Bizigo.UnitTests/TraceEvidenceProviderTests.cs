using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;

namespace Bizigo.UnitTests;

public sealed class TraceEvidenceProviderTests
{
    private static TelemetryRecord Span(int id, string service, int status, int parent = 0, int link = 0, int trace = 1)
    {
        string Id(int v) => v.ToString("x16", CultureInfo.InvariantCulture);
        var traceId = trace.ToString("x32", CultureInfo.InvariantCulture);
        var span = JsonSerializer.SerializeToElement(new
        {
            traceId, spanId = Id(id), parentSpanId = parent == 0 ? "" : Id(parent),
            links = link == 0 ? Array.Empty<object>() : new object[] { new { traceId, spanId = Id(link) } },
        });
        return MetricEvidenceProviderTests.Point("0", 1) with
        {
            Signal = TelemetrySignal.Traces, TraceId = traceId, SpanId = Id(id), ServiceName = service,
            Status = status, Span = span, Metric = null, LogicalId = traceId + "/" + Id(id),
        };
    }
    private static TelemetryRecord[] Fixture() =>
    [
        Span(1, "frontend", 1), Span(2, "orders", 2, 1), Span(3, "db", 2, 2),
        Span(4, "cache", 2, 1), Span(5, "db", 0, 2), Span(6, "worker", 2, link: 3),
        Span(7, "orphan", 2, 26), Span(8, "orders", 2, 2), Span(9, "db", 2, trace: 2),
    ];

    [Fact]
    public void Trace_fixed_edges_paths_and_error_status()
    {
        var graph = TraceRelationships.Analyze(Fixture());
        Assert.Equal([3, 8], graph.ErrorEdges.Select(e => int.Parse(e.ChildId, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        Assert.All(graph.ErrorEdges, e => Assert.EndsWith("2", e.ParentId, StringComparison.Ordinal));
        Assert.Equal(2, graph.ErrorPaths.Count);
        Assert.Equal(3, graph.Services.Count);
        Assert.Equal(2, Assert.Single(graph.Services, s => s.FromService == "orders" && s.ToService == "db").Support.Count);
        Assert.Single(graph.LinkedObservations); Assert.Equal(1, graph.SelfRelations);
        Assert.Equal(["MissingParent"], graph.Reasons);
        Assert.Equal(JsonSerializer.Serialize(graph), JsonSerializer.Serialize(TraceRelationships.Analyze(Fixture().Reverse().ToArray())));
    }

    [Fact]
    public void Trace_conflict_cycle_and_scope()
    {
        var duplicate = Fixture()[1] with { LogicalId = "duplicate" };
        Assert.Equal(2, TraceRelationships.Analyze([.. Fixture(), duplicate]).ErrorEdges.Count);
        var conflict = duplicate with { ServiceName = "other" };
        var broken = TraceRelationships.Analyze([.. Fixture(), conflict]);
        Assert.Contains("ConflictingSpanId", broken.Reasons); Assert.Empty(broken.ErrorEdges);
        var cycle = TraceRelationships.Analyze([Span(10, "x", 2, 11), Span(11, "y", 2, 10)]);
        Assert.Contains("Cycle", cycle.Reasons); Assert.Empty(cycle.ErrorPaths); Assert.Empty(cycle.Services);
        var scoped = TraceRelationships.Analyze(Fixture().Where(r => !r.SpanId.EndsWith("3", StringComparison.Ordinal)).ToArray());
        Assert.Single(scoped.ErrorEdges); Assert.Contains("UnresolvedReference", scoped.Reasons);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void Trace_numeric_budget_boundaries(int budget, bool exceeded)
    {
        // Six parent references plus one link, including the unresolved parent.
        var graph = TraceRelationships.Analyze(Fixture(), budget);
        Assert.Equal(exceeded, graph.Reasons.Contains("BudgetExceeded", StringComparer.Ordinal));
    }
}
