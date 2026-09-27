using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Storage.ClickHouse;
using Google.Protobuf;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: real ClickHouse typed fidelity, immediate deduplication,
/// logical/physical TTL and the actual production query's index plan.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TelemetryStorageIntegrationTests(DevStackFixture stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory, Trait("Category", "Integration")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Complete_sender_typed_fields_roundtrip_and_replay_without_merge(bool json, bool traces)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("source", "A");
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        IMessage request = traces ? TelemetryDbFixture.Traces("source", f.Now, 3) : TelemetryDbFixture.Metrics("source", f.Now);
        var envelope = await f.EmitAsync(ingest, request, signal, json);
        await ingest.ReplayArchiveAsync(Ct); await ingest.ReplayArchiveAsync(Ct);
        var scope = AccessScope.ForGroups("typed-reader", ["A"]);
        var query = f.Window(signal, "source");
        var page = await f.Query.SearchTelemetryAsync(query, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Data, page.Status);
        Assert.Equal(traces ? 3 : 5, page.Records.Count);
        Assert.Equal(page.Records.Count, page.Records.Select(r => r.LogicalId).Distinct().Count());
        foreach (var row in page.Records)
        {
            var ordinal = int.Parse(row.Owner.LeafKey.Split('/')[3], CultureInfo.InvariantCulture);
            if (traces)
            {
                var producer = ((OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest)request).ResourceSpans[0];
                var expected = producer.ScopeSpans[0].Spans[ordinal];
                Assert.Equal(expected, OtlpJsonCodec.Parse<Span>(Encoding.UTF8.GetBytes(row.Span!.Value.GetRawText()), Span.Descriptor));
            }
            else
            {
                var producer = ((OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest)request).ResourceMetrics[0];
                Assert.Equal(producer.ScopeMetrics[0].Metrics[ordinal], OtlpJsonCodec.Parse<Metric>(Encoding.UTF8.GetBytes(row.Metric!.Value.GetRawText()), Metric.Descriptor));
                Assert.Equal(producer.Resource, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Resource.V1.Resource>(Encoding.UTF8.GetBytes(row.Resource.GetRawText()), OpenTelemetry.Proto.Resource.V1.Resource.Descriptor));
                Assert.Equal(producer.ScopeMetrics[0].Scope, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Common.V1.InstrumentationScope>(Encoding.UTF8.GetBytes(row.Scope.GetRawText()), OpenTelemetry.Proto.Common.V1.InstrumentationScope.Descriptor));
            }
            Assert.Equal(envelope.EnvelopeId, row.EnvelopeId); Assert.Equal("A", row.Owner.OwnerGroup);
        }
        Assert.Equal(page.Records.Count, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        Assert.Equal(0, (await f.Query.CountOutOfScopeTelemetryAsync(query, scope, Ct)).Count);
        Assert.Equal(traces ? 1 : 5, (await f.Query.SummarizeTelemetryAsync(query, scope, Ct)).Groups.Count);
        var again = await new ClickHouseMigrator(f.Storage).MigrateAsync(DevStackSetup.RepoPath("db/clickhouse"), Ct);
        Assert.Empty(again.Applied); Assert.Contains("0007_telemetry", again.AlreadyApplied);
        var metricSchema = await f.SqlAsync("SHOW CREATE TABLE metric_points");
        var traceSchema = await f.SqlAsync("SHOW CREATE TABLE trace_spans");
        Assert.Contains("ORDER BY (owner_group, metric_name, resource_id, ts, logical_id)", metricSchema, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (owner_group, service_name, start_time, trace_id, logical_id)", traceSchema, StringComparison.Ordinal);
        TelemetryDbFixture.Evidence("schema", new { metricSchema, traceSchema });
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_physical_rows_are_hidden_before_ttl_materialization(bool traces)
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("ttl", "A", f.Clock.GetUtcNow().AddDays(-100));
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        var table = traces ? "trace_spans" : "metric_points";
        await f.SqlAsync("SYSTEM STOP MERGES " + table);
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var expired = f.Now - 91 * 86400000000000UL;
        IMessage old = traces ? TelemetryDbFixture.Traces("ttl", expired) : TelemetryDbFixture.Metrics("ttl", expired, false);
        var oldEnvelope = await f.EmitAsync(ingest, old, signal);
        IMessage fresh = traces ? TelemetryDbFixture.Traces("ttl", f.Now) : TelemetryDbFixture.Metrics("ttl", f.Now, false);
        await f.EmitAsync(ingest, fresh, signal);
        Assert.Equal("2", (await f.SqlAsync("SELECT count() FROM " + table + " FINAL")).Trim());
        var scope = AccessScope.ForGroups("ttl-reader", ["A"]);
        var query = new TelemetryQuery { Signal = signal, ResourceId = "ttl", FromNano = expired - 1, ToNano = expired + 2 };
        var page = await f.Query.SearchTelemetryAsync(query, scope, Ct);
        Assert.Equal(TelemetryResultStatus.Empty, page.Status); Assert.Empty(page.Records);
        Assert.Equal(0, (await f.Query.CountTelemetryAsync(query, scope, Ct)).Count);
        Assert.Empty((await f.Query.SummarizeTelemetryAsync(query, scope, Ct)).Groups);
        Assert.Equal(0, (await f.Query.CountOutOfScopeTelemetryAsync(query, AccessScope.ForGroups("B", ["B"]), Ct)).Count);
        if (!traces) Assert.Empty((await f.Query.GetMetricPointAsync(oldEnvelope.EnvelopeId.ToString("N") + "/m/0/0/0/0", scope, Ct)).Records);
        else Assert.Empty((await f.Query.GetTraceAsync(query with { TraceId = "00112233445566778899aabbccddeeff" }, scope, Ct)).Records);
        Assert.Single((await f.Query.SearchTelemetryAsync(f.Window(signal, "ttl"), scope, Ct)).Records);
        await f.SqlAsync("SYSTEM START MERGES " + table);
        await f.SqlAsync("ALTER TABLE " + table + " MATERIALIZE TTL SETTINGS mutations_sync=2");
        Assert.Equal("1", (await f.SqlAsync("SELECT count() FROM " + table + " FINAL")).Trim());
        TelemetryDbFixture.Evidence("ttl-" + signal, new { physicalBefore = 2, physicalAfter = 1, logicalExpired = page.Status });
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Expired_archive_replay_does_not_resurrect_public_results()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        await f.SourceAsync("expired", "A", f.Clock.GetUtcNow().AddDays(-100));
        using var ingest = f.Open(); await ingest.RecoverAsync(Ct);
        var old = f.Now - 91 * 86400000000000UL;
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("expired", old, false), TelemetrySignal.Metrics);
        await ingest.SweepAsync(Ct); Assert.Empty(ingest.Wal.ListSealedSegments());
        await f.SqlAsync("TRUNCATE TABLE metric_points");
        await ingest.ReplayArchiveAsync(Ct);
        var result = await f.Query.SearchTelemetryAsync(new() { Signal = TelemetrySignal.Metrics, FromNano = old - 1, ToNano = old + 1 }, AccessScope.ForGroups("ttl", ["A"]), Ct);
        Assert.Equal(TelemetryResultStatus.Empty, result.Status); Assert.Empty(result.Records);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Production_query_plan_prunes_granules_with_scope_dedup_and_ttl()
    {
        await using var f = await TelemetryDbFixture.CreateAsync(stack, Ct);
        var wire = TelemetryDbFixture.Metrics("seed", f.Now, false).ToByteArray();
        var leaf = Assert.Single(new OtlpTelemetryDecoder().Decode(TelemetrySignal.Metrics, wire, "application/x-protobuf").Accepted);
        var records = new List<TelemetryRecord>(100000);
        // Synthetic bulk fixture for the index oracle; admission fidelity is
        // independently exercised above with sender-produced exports.
        foreach (var owner in new[] { "A", "B" })
        {
            var id = Guid.NewGuid(); var hash = RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(owner + id));
            var envelope = new RawSignalEnvelope(2, id, TelemetrySignal.Metrics, "application/x-protobuf", f.Clock.GetUtcNow(), RawSignalEnvelope.Hash(wire), wire, 1, [leaf.Key], 0)
            { OwnerBindingsSha256 = hash };
            var prototype = TelemetryMaterializer.Materialize(envelope, leaf, new(leaf.Key, "seed", owner, 1, f.Now, "known"));
            for (var i = 0; i < 50000; i++)
            {
                var key = "m/0/0/0/" + i.ToString(CultureInfo.InvariantCulture);
                var timestamp = f.Now - (ulong)(i % 1000) * 100;
                records.Add(prototype with { LogicalId = id.ToString("N") + "/" + key, Name = "metric-" + (i % 10), TimeUnixNano = timestamp,
                    Owner = prototype.Owner with { LeafKey = key, SourceId = "source-" + (i % 16), EventTimeUnixNano = timestamp } });
            }
        }
        await f.Writer.WriteAsync(records, Ct);
        var plans = new List<TelemetrySqlPlan>(); f.Reader.ObserveQuery = plans.Add;
        var query = new TelemetryQuery { Signal = TelemetrySignal.Metrics, Name = "metric-2", ResourceId = "source-2", FromNano = f.Now - 50000, ToNano = (decimal)f.Now + 1 };
        var expected = records.LongCount(r => r.Owner.OwnerGroup == "A" && r.Name == query.Name && r.Owner.SourceId == query.ResourceId && r.TimeUnixNano >= query.FromNano && r.TimeUnixNano < query.ToNano);
        Assert.True(expected > 0);
        var count = await f.Query.CountTelemetryAsync(query, AccessScope.ForGroups("plan-reader", ["A"]), Ct);
        Assert.Equal(expected, count.Count);
        var actual = Assert.Single(plans);
        Assert.Contains(" FINAL ", actual.Sql, StringComparison.Ordinal);
        Assert.Contains("scope_groups", actual.Sql, StringComparison.Ordinal);
        Assert.Contains("expires_nano", actual.Sql, StringComparison.Ordinal);
        var explain = await f.SqlAsync("EXPLAIN indexes=1 " + actual.Sql, actual.Parameters);
        var fractions = Regex.Matches(explain, @"Granules: (\d+)/(\d+)").Select(m =>
            (Selected: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Total: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();
        Assert.Contains(fractions, g => g.Selected > 0 && g.Selected < g.Total);
        TelemetryDbFixture.Evidence("production-explain", new { actual.Sql, actual.Parameters, explain, expected, actualCount = count.Count, rows = records.Count });
    }
}
