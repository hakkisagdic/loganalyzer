using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Bizigo.IntegrationTests;

[Collection(DevStackCollection.Name)]
public sealed class TelemetryQueryIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task Trace_detail_and_summary_are_bounded_continuable_and_scoped()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("many", "A"); await f.SourceAsync("sentinel", "B");
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var request = TelemetryDbFixture.Traces("many", f.Now, 1001);
        // The producer, not the reader's grouping implementation, defines 1001 groups.
        for (var i = 0; i < request.ResourceSpans[0].ScopeSpans[0].Spans.Count; i++)
        {
            var original = request.ResourceSpans[0];
            var resource = new OpenTelemetry.Proto.Trace.V1.ResourceSpans
            {
                Resource = original.Resource.Clone(), SchemaUrl = original.SchemaUrl,
                ScopeSpans = { new OpenTelemetry.Proto.Trace.V1.ScopeSpans
                {
                    Scope = original.ScopeSpans[0].Scope.Clone(), SchemaUrl = original.ScopeSpans[0].SchemaUrl,
                    Spans = { original.ScopeSpans[0].Spans[i].Clone() },
                } },
            };
            resource.Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "service-" + i;
            if (i == 0) continue;
            request.ResourceSpans.Add(resource);
        }
        // Keep one original leaf; the other resources each hold exactly one.
        var first = request.ResourceSpans[0].ScopeSpans[0].Spans[0].Clone();
        request.ResourceSpans[0].ScopeSpans[0].Spans.Clear(); request.ResourceSpans[0].ScopeSpans[0].Spans.Add(first);
        request.ResourceSpans[0].Resource.Attributes.Single(a => a.Key == "service.name").Value.StringValue = "service-0";
        var envelope = await f.EmitAsync(ingest, request, TelemetrySignal.Traces);
        await f.EmitAsync(ingest, TelemetryDbFixture.Traces("sentinel", f.Now), TelemetrySignal.Traces);
        var scope = AccessScope.ForGroups("bounded-" + Guid.NewGuid().ToString("N"), ["A"]);
        var query = f.Window(TelemetrySignal.Traces) with { TraceId = "00112233445566778899aabbccddeeff", Limit = 1000 };
        var ids = new HashSet<string>(StringComparer.Ordinal); var pages = 0;
        TelemetryPage page;
        do
        {
            page = await f.Query.GetTraceAsync(query, scope, Ct); pages++;
            Assert.Equal(TelemetryResultStatus.Data, page.Status); Assert.InRange(page.Records.Count, 1, 1000);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, RawSignalCodec.Json).Length <= TelemetryQuery.MaxBytes);
            foreach (var row in page.Records) { Assert.Equal("A", row.Owner.OwnerGroup); Assert.True(ids.Add(row.LogicalId)); }
            Assert.Equal(page.Partial, page.Cursor is not null);
            if (pages == 1)
            {
                Assert.True(page.Partial);
                await Assert.ThrowsAsync<ArgumentException>(() => f.Query.GetTraceAsync(query with { Cursor = page.Cursor }, AccessScope.ForGroups("B", ["B"]), Ct));
                await Assert.ThrowsAsync<ArgumentException>(() => f.Query.GetTraceAsync(query with { Cursor = page.Cursor, Name = "different" }, scope, Ct));
            }
            query = query with { Cursor = page.Cursor };
            Assert.InRange(pages, 1, 20);
        } while (page.Partial);
        Assert.Equal(1001, ids.Count);
        Assert.Equal(envelope.AcceptedKeys.Select(k => envelope.EnvelopeId.ToString("N") + "/" + k).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
        var groups = new HashSet<string>(StringComparer.Ordinal); var summaryQuery = query with { Cursor = null };
        TelemetrySummaryPage summary; var summaryPages = 0;
        do
        {
            summary = await f.Query.SummarizeTelemetryAsync(summaryQuery, scope, Ct); summaryPages++;
            Assert.Equal(TelemetryResultStatus.Data, summary.Status); Assert.InRange(summary.Groups.Count, 1, 1000);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(summary, RawSignalCodec.Json).Length <= TelemetryQuery.MaxBytes);
            foreach (var group in summary.Groups) { Assert.Equal(1, group.Count); Assert.True(groups.Add(group.Last.ServiceName)); }
            Assert.Equal(summary.Partial, summary.Cursor is not null);
            summaryQuery = summaryQuery with { Cursor = summary.Cursor }; Assert.InRange(summaryPages, 1, 20);
        } while (summary.Partial);
        Assert.Equal(Enumerable.Range(0, 1001).Select(i => "service-" + i).Order(StringComparer.Ordinal), groups.Order(StringComparer.Ordinal));
        Assert.Equal(1001, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        TelemetryDbFixture.Evidence("bounded-query", new { pages, summaryPages, leaves = ids.Count, groups = groups.Count });
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Mixed_name_type_unit_temporality_summary_uses_independent_grouping_oracle()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("mixed", "A");
        var request = TelemetryDbFixture.Metrics("mixed", f.Now);
        var metrics = request.ResourceMetrics[0].ScopeMetrics[0].Metrics;
        var cumulative = metrics[1].Clone(); cumulative.Sum.AggregationTemporality = AggregationTemporality.Cumulative; metrics.Add(cumulative);
        var otherUnit = metrics[0].Clone(); otherUnit.Unit = "seconds"; metrics.Add(otherUnit);
        var otherAttribute = metrics[0].Clone(); otherAttribute.Gauge.DataPoints[0].Attributes.Add(new KeyValue { Key = "series", Value = new AnyValue { IntValue = 1 } }); metrics.Add(otherAttribute);
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        await f.EmitAsync(ingest, request, TelemetrySignal.Metrics);
        await f.EmitAsync(ingest, request, TelemetrySignal.Metrics);
        var scope = AccessScope.ForGroups("mixed", ["A"]);
        var summary = await f.Query.SummarizeTelemetryAsync(f.Window(TelemetrySignal.Metrics), scope, Ct);
        Assert.Equal(TelemetryResultStatus.Data, summary.Status); Assert.Equal(8, summary.Groups.Count);
        Assert.All(summary.Groups, g => { Assert.Equal(2, g.Count); Assert.Equal(f.Now, g.FirstNano); Assert.Equal(f.Now, g.LastNano); });
        // Independent sender tuples distinguish all eight series without reusing SeriesKey.
        var expected = metrics.Select(m => (m.DataCase.ToString(), m.Unit, Temporality: m.Sum?.AggregationTemporality.ToString() ?? m.Histogram?.AggregationTemporality.ToString() ?? m.ExponentialHistogram?.AggregationTemporality.ToString() ?? "Unspecified",
            Extra: m.Gauge?.DataPoints[0].Attributes.Any(a => a.Key == "series") == true)).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        var actual = summary.Groups.Select(g =>
        {
            var m = Bizigo.Ingest.Otlp.OtlpJsonCodec.Parse<Metric>(System.Text.Encoding.UTF8.GetBytes(g.Last.Metric!.Value.GetRawText()), Metric.Descriptor);
            return (m.DataCase.ToString(), m.Unit, Temporality: m.Sum?.AggregationTemporality.ToString() ?? m.Histogram?.AggregationTemporality.ToString() ?? m.ExponentialHistogram?.AggregationTemporality.ToString() ?? "Unspecified",
                Extra: m.Gauge?.DataPoints[0].Attributes.Any(a => a.Key == "series") == true);
        }).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Four_MiB_budget_continues_and_single_oversize_fails_visibly()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("large", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var request = TelemetryDbFixture.Traces("large", f.Now, 6);
        foreach (var span in request.ResourceSpans[0].ScopeSpans[0].Spans) span.Attributes[0].Value.StringValue = new string('x', 800000);
        await f.EmitAsync(ingest, request, TelemetrySignal.Traces);
        var scope = AccessScope.ForGroups("large", ["A"]); var query = f.Window(TelemetrySignal.Traces, "large");
        var first = await f.Query.SearchTelemetryAsync(query, scope, Ct);
        Assert.True(first.Partial); Assert.InRange(first.Records.Count, 1, 5);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first, RawSignalCodec.Json).Length <= TelemetryQuery.MaxBytes);
        var second = await f.Query.SearchTelemetryAsync(query with { Cursor = first.Cursor }, scope, Ct);
        Assert.False(second.Partial); Assert.Equal(6, first.Records.Count + second.Records.Count);
        var giant = TelemetryDbFixture.Traces("large", f.Now + 100);
        giant.ResourceSpans[0].ScopeSpans[0].Spans[0].Attributes[0].Value.StringValue = new string('z', TelemetryQuery.MaxBytes);
        await f.EmitAsync(ingest, giant, TelemetrySignal.Traces);
        var failed = await f.Query.SearchTelemetryAsync(query with { FromNano = f.Now + 100 }, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Failed, failed.Status); Assert.Equal("ResultTooLarge", failed.Error); Assert.Empty(failed.Records);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Failed_empty_never_fed_and_all_query_audit_outcomes_are_distinct()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("audit", "A"); using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var envelope = await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("audit", f.Now, false), TelemetrySignal.Metrics);
        var subject = "audit-" + Guid.NewGuid().ToString("N"); var scope = AccessScope.ForGroups(subject, ["A"]);
        var query = f.Window(TelemetrySignal.Metrics, "audit");
        Assert.Equal(TelemetryResultStatus.Data, (await f.Query.SearchTelemetryAsync(query, scope, Ct)).Status);
        Assert.Equal(TelemetryResultStatus.Empty, (await f.Query.SearchTelemetryAsync(query with { Name = "absent-secret" }, scope, Ct)).Status);
        Assert.Equal(TelemetryResultStatus.NeverFed, (await f.Query.SearchTelemetryAsync(query with { ResourceId = "never" }, scope, Ct)).Status);
        await f.Query.GetMetricPointAsync(envelope.EnvelopeId.ToString("N") + "/" + envelope.AcceptedKeys[0], scope, Ct);
        await f.Query.CountTelemetryAsync(query, scope, Ct); await f.Query.CountOutOfScopeTelemetryAsync(query, scope, Ct);
        await f.Query.SummarizeTelemetryAsync(query, scope, Ct); await f.Query.GetTelemetryFeedAsync(TelemetrySignal.Metrics, "audit", scope, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Query.SearchTelemetryAsync(query with { Limit = 0 }, scope, Ct));
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Query.SearchTelemetryAsync(query, scope, cancelled.Token));
        }
        // Real server errors, not fake Result objects: hide the table in this isolated database.
        await f.SqlAsync("RENAME TABLE metric_points TO unavailable_metric_points");
        try
        {
            Assert.Equal(TelemetryResultStatus.Failed, (await f.Query.SearchTelemetryAsync(query, scope, Ct)).Status);
            var outside = await f.Query.CountOutOfScopeTelemetryAsync(query, scope, Ct);
            Assert.Equal(TelemetryResultStatus.Failed, outside.Status); Assert.Null(outside.Count);
        }
        finally { await f.SqlAsync("RENAME TABLE unavailable_metric_points TO metric_points"); }
        var audit = await f.Db.AuditLog.AsNoTracking().Where(r => r.Subject == subject).OrderBy(r => r.Id).ToArrayAsync(Ct);
        Assert.Equal(12, audit.Length); Assert.Equal(4, audit.Count(a => !a.Succeeded));
        Assert.Contains(audit, a => a.Details.Contains("cancelled", StringComparison.Ordinal));
        Assert.All(audit, a => { Assert.DoesNotContain("absent-secret", a.Details, StringComparison.Ordinal); Assert.True(a.DurationMs >= 0); });
        TelemetryDbFixture.Evidence("query-audit", audit);
    }
}
