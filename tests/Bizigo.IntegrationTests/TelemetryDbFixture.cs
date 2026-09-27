using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Wal;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Bizigo.Storage.Raw;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Bizigo.IntegrationTests;

internal sealed class TelemetryDbFixture : IAsyncDisposable
{
    private readonly DevStackFixture stack;
    public ClickHouseContext Storage { get; }
    public IDbContextFactory<ControlPlaneDbContext> Factory { get; }
    public ControlPlaneDbContext Db { get; }
    public HistoricalTelemetryOwners Owners { get; }
    public TelemetryWriter Writer { get; }
    public TelemetryReader Reader { get; }
    public IScopedQuery Query { get; }
    public FakeTimeProvider Clock { get; }
    public ulong Now => checked((ulong)(Clock.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "telemetry-db-" + Guid.NewGuid().ToString("N"));
    public S3RawObjectStore Objects { get; }
    public RawStoreOptions RawOptions { get; }

    private TelemetryDbFixture(DevStackFixture stack, ClickHouseContext storage, IDbContextFactory<ControlPlaneDbContext> factory)
    {
        this.stack = stack; Storage = storage; Factory = factory; Db = factory.CreateDbContext();
        Owners = new(factory); Writer = new(Storage, Owners);
        Clock = new(DateTimeOffset.UtcNow); Reader = new(Storage, Clock);
        Query = new ScopedQuery(new(Storage), new(Storage), new(Storage), new(Storage), Db, new ControlPlaneAuditSink(Factory), Reader);
        RawOptions = DevStackSetup.RawOptions(stack); RawOptions.SegmentRetention = TimeSpan.Zero;
        Objects = new(Options.Create(RawOptions));
    }

    public static async Task<TelemetryDbFixture> CreateAsync(DevStackFixture stack, CancellationToken token)
    {
        var factory = await DevStackSetup.ControlPlaneAsync(stack, token);
        return new(stack, await DevStackSetup.ClickHouseAsync(stack, token), factory);
    }

    public SignalIngest Open(string? root = null, ITelemetrySink? sink = null, ISignalCheckpoints? checkpoints = null) =>
        new(new(), new(Factory), Objects, Options.Create(new SignalOptions { Directory = root ?? Root }),
            Options.Create(new WalOptions { Directory = root ?? Root }), Options.Create(RawOptions),
            NullLogger<WriteAheadLog>.Instance, checkpoints, owners: Owners, bindings: Owners, sink: sink ?? Writer);

    public async Task<decimal> SourceAsync(string id, string owner, DateTimeOffset? at = null, string? hostname = null, bool enabled = true)
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = await Factory.CreateDbContextAsync(token);
        db.HistoryClock = new FakeTimeProvider(at ?? Clock.GetUtcNow().AddSeconds(-1));
        var result = await SourceOwnershipHistoryIntegrationTests.Upsert(db, new() { SourceId = id, OwnerGroup = owner, Hostname = hostname, Enabled = enabled });
        Assert.InRange(SourceOwnershipHistoryIntegrationTests.Status(result), 200, 201);
        return (await db.SourceOwnershipHistory.SingleAsync(h => h.SourceId == id && h.EffectiveToNano == null, token)).EffectiveFromNano;
    }

    public async Task<RawSignalEnvelope> EmitAsync(SignalIngest ingest, IMessage request, TelemetrySignal signal, bool json = false)
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = json ? JsonSerializer.SerializeToUtf8Bytes(OtlpJsonCodec.Format(request)) : request.ToByteArray();
        var admission = await ingest.AcceptAsync(signal, bytes, json ? "application/json" : "application/x-protobuf", token);
        Assert.Equal(200, admission.Status); Assert.Equal(0, admission.Rejected);
        var work = await ingest.Reader.ReadAsync(token);
        await ingest.ProcessAsync(work, token); ingest.CompleteWork();
        return work.Envelope;
    }

    public TelemetryQuery Window(TelemetrySignal signal, string? source = null) => new()
    { Signal = signal, FromNano = Now - 10000000000UL, ToNano = (decimal)Now + 10000000000UL, ResourceId = source };

    public async Task<string> SqlAsync(string sql, IReadOnlyDictionary<string, object>? parameters = null)
    {
        var database = new DbConnectionStringBuilder { ConnectionString = Storage.Options.ConnectionString }["Database"].ToString()!;
        using var http = new HttpClient { BaseAddress = new Uri(stack.ClickHouseHttpUrl), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Add("X-ClickHouse-User", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Key", "bizigo");
        http.DefaultRequestHeaders.Add("X-ClickHouse-Database", database);
        var args = parameters?.Select(p => "param_" + Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Parameter(p.Value)));
        using var content = new StringContent(sql, Encoding.UTF8, new MediaTypeHeaderValue("text/plain"));
        using var response = await http.PostAsync("/?" + string.Join('&', args ?? []), content, TestContext.Current.CancellationToken);
        var result = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, result);
        return result;
    }
    private static string Parameter(object value) => value is string[] array
        ? "[" + string.Join(',', array.Select(s => "'" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'")) + "]"
        : Convert.ToString(value, CultureInfo.InvariantCulture)!;

    public static void Evidence(string name, object value)
    {
        var root = Environment.GetEnvironmentVariable("BIZIGO_TELEMETRY_EVIDENCE") ?? "/tmp/bizigo-telemetry-evidence/integration-artifacts";
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync(); Objects.Dispose(); Storage.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    public static ExportMetricsServiceRequest Metrics(string source, ulong timestamp, bool all = true)
    {
        var point = new NumberDataPoint { AsInt = 9007199254740993, TimeUnixNano = timestamp, StartTimeUnixNano = timestamp - 1,
            Flags = 3, Attributes = { Attr("bytes", new() { BytesValue = ByteString.CopyFrom([0, 255]) }) },
            Exemplars = { new Exemplar { AsInt = long.MinValue, TimeUnixNano = timestamp, TraceId = TraceId, SpanId = SpanId,
                FilteredAttributes = { Attr("filter", new() { BoolValue = true }) } } } };
        var metrics = new List<Metric> { new() { Name = "same", Description = "typed", Unit = "bytes", Gauge = new() { DataPoints = { point } }, Metadata = { Attr("meta", new() { IntValue = long.MaxValue }) } } };
        if (all)
        {
            metrics.Add(new() { Name = "same", Unit = "count", Sum = new() { IsMonotonic = true, AggregationTemporality = AggregationTemporality.Delta, DataPoints = { point.Clone() } } });
            metrics.Add(new() { Name = "same", Unit = "ms", Histogram = new() { AggregationTemporality = AggregationTemporality.Cumulative,
                DataPoints = { new HistogramDataPoint { TimeUnixNano = timestamp, Count = 6, Sum = 11, Min = 0, Max = 8, Flags = 1,
                    ExplicitBounds = { 1, 3 }, BucketCounts = { 1UL, 2UL, 3UL }, Exemplars = { point.Exemplars[0].Clone() } } } } });
            metrics.Add(new() { Name = "same", Unit = "ms", ExponentialHistogram = new() { AggregationTemporality = AggregationTemporality.Delta,
                DataPoints = { new ExponentialHistogramDataPoint { TimeUnixNano = timestamp, Count = 10, Sum = 0, Min = -9, Max = 9, Scale = -2, ZeroCount = 1, ZeroThreshold = 0.001,
                    Positive = new() { Offset = -3, BucketCounts = { 2UL, 3UL } }, Negative = new() { Offset = 2, BucketCounts = { 4UL } }, Exemplars = { point.Exemplars[0].Clone() } } } } });
            metrics.Add(new() { Name = "same", Unit = "s", Summary = new() { DataPoints = { new SummaryDataPoint { TimeUnixNano = timestamp,
                Count = 4, Sum = 21, Flags = 1, QuantileValues = { new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .5, Value = 8 } } } } } });
        }
        return new() { ResourceMetrics = { new ResourceMetrics { Resource = Resource(source), SchemaUrl = "resource/schema",
            ScopeMetrics = { new ScopeMetrics { Scope = Scope(), SchemaUrl = "scope/schema", Metrics = { metrics } } } } } };
    }

    public static ExportTraceServiceRequest Traces(string source, ulong timestamp, int count = 1)
    {
        var spans = Enumerable.Range(0, count).Select(i => new Span
        {
            TraceId = TraceId, SpanId = ByteString.CopyFrom(Convert.FromHexString((i + 1).ToString("x16", CultureInfo.InvariantCulture))),
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("9999999999999999")), Name = "operation",
            StartTimeUnixNano = timestamp + (ulong)i, EndTimeUnixNano = timestamp + (ulong)i + 1,
            TraceState = "vendor=state", Kind = Span.Types.SpanKind.Server, Flags = 769,
            DroppedAttributesCount = 2, DroppedEventsCount = 3, DroppedLinksCount = 4,
            Status = new() { Code = Status.Types.StatusCode.Error, Message = "failure" },
            Attributes = { Attr("payload", new() { StringValue = "secret-for-" + source }) },
            Events = { new Span.Types.Event { Name = "event", TimeUnixNano = timestamp, DroppedAttributesCount = 7,
                Attributes = { Attr("event-int", new() { IntValue = long.MinValue }) } } },
            Links = { new Span.Types.Link { TraceId = TraceId, SpanId = SpanId, TraceState = "link=value", Flags = 257,
                DroppedAttributesCount = 8, Attributes = { Attr("link", new() { BoolValue = true }) } } },
        });
        return new() { ResourceSpans = { new ResourceSpans { Resource = Resource(source), SchemaUrl = "resource/schema",
            ScopeSpans = { new ScopeSpans { Scope = Scope(), SchemaUrl = "scope/schema", Spans = { spans } } } } } };
    }
    private static ByteString TraceId => ByteString.CopyFrom(Convert.FromHexString("00112233445566778899aabbccddeeff"));
    private static ByteString SpanId => ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef"));
    private static KeyValue Attr(string key, AnyValue value) => new() { Key = key, Value = value };
    private static Resource Resource(string source) => new() { DroppedAttributesCount = 9, Attributes =
    {
        Attr("bizigo.source_key", new() { StringValue = source }), Attr("service.name", new() { StringValue = "service-ö'" }),
        Attr("owner_group", new() { StringValue = "forged-admin" }),
        Attr("nested", new() { ArrayValue = new() { Values = { new AnyValue { BoolValue = true }, new AnyValue { IntValue = long.MaxValue },
            new AnyValue { KvlistValue = new() { Values = { Attr("inner", new() { DoubleValue = double.NaN }) } } } } } }),
    } };
    private static InstrumentationScope Scope() => new() { Name = "sdk", Version = "1.2", DroppedAttributesCount = 3,
        Attributes = { Attr("scope", new() { StringValue = "typed" }) } };
}
