using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Bizigo.UnitTests;

public sealed class OtlpTelemetryDecoderTests
{
    private readonly OtlpTelemetryDecoder decoder = new();
    private static ByteString TraceId => ByteString.CopyFrom(Convert.FromHexString("00112233445566778899aabbccddeeff"));
    private static ByteString SpanId => ByteString.CopyFrom(Convert.FromHexString("0123456789abcdef"));
    private static KeyValue Attribute => new() { Key = "bytes", Value = new AnyValue { BytesValue = ByteString.CopyFrom([0, 255, 19]) } };
    private static Resource Resource => new() { Attributes = { Attribute }, DroppedAttributesCount = 7 };
    private static InstrumentationScope Scope => new() { Name = "sdk", Version = "1.2", Attributes = { Attribute }, DroppedAttributesCount = 9 };
    private static Exemplar Exemplar => new()
    {
        FilteredAttributes = { Attribute }, TimeUnixNano = ulong.MaxValue, AsInt = long.MinValue,
        TraceId = TraceId, SpanId = SpanId,
    };

    internal static ExportMetricsServiceRequest Metrics()
    {
        var point = new NumberDataPoint
        {
            Attributes = { Attribute }, StartTimeUnixNano = 1, TimeUnixNano = ulong.MaxValue,
            AsInt = long.MinValue, Flags = 3, Exemplars = { Exemplar },
        };
        var gauge = new Metric { Name = "gauge", Description = "all fields", Unit = "bytes", Metadata = { Attribute },
            Gauge = new Gauge { DataPoints = { point, new NumberDataPoint { AsInt = long.MaxValue },
                new NumberDataPoint { AsDouble = double.NaN }, new NumberDataPoint { AsDouble = double.PositiveInfinity },
                new NumberDataPoint { AsDouble = double.NegativeInfinity }, new NumberDataPoint { AsDouble = 0 } } } };
        var sum = new Metric { Name = "sum", Sum = new Sum { IsMonotonic = true,
            AggregationTemporality = AggregationTemporality.Delta, DataPoints = { point.Clone() } } };
        var histogram = new Metric { Name = "histogram", Histogram = new Histogram
        {
            AggregationTemporality = AggregationTemporality.Cumulative,
            DataPoints = { new HistogramDataPoint { Attributes = { Attribute }, StartTimeUnixNano = 2, TimeUnixNano = 3,
                Count = 6, Sum = 11, Min = 0, Max = 8, ExplicitBounds = { 1, 3 }, BucketCounts = { 1UL, 2UL, 3UL },
                Exemplars = { Exemplar }, Flags = 1 } },
        } };
        var exponential = new Metric { Name = "exponential", ExponentialHistogram = new ExponentialHistogram
        {
            AggregationTemporality = AggregationTemporality.Delta,
            DataPoints = { new ExponentialHistogramDataPoint { Attributes = { Attribute }, StartTimeUnixNano = 4, TimeUnixNano = 5,
                Count = 10, Sum = 0, Min = -9, Max = 9, Scale = -2, ZeroCount = 1, ZeroThreshold = 0.001,
                Positive = new ExponentialHistogramDataPoint.Types.Buckets { Offset = -3, BucketCounts = { 2UL, 3UL } },
                Negative = new ExponentialHistogramDataPoint.Types.Buckets { Offset = 2, BucketCounts = { 4UL } },
                Exemplars = { Exemplar }, Flags = 1 } },
        } };
        var summary = new Metric { Name = "summary", Summary = new Summary
        {
            DataPoints = { new SummaryDataPoint { Attributes = { Attribute }, StartTimeUnixNano = 6, TimeUnixNano = 7,
                Count = 4, Sum = 21, Flags = 1,
                QuantileValues = { new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0.5, Value = 8 } } } },
        } };
        return new ExportMetricsServiceRequest { ResourceMetrics =
        {
            new ResourceMetrics { Resource = Resource, SchemaUrl = "resource/1", ScopeMetrics =
            {
                new ScopeMetrics { Scope = Scope, SchemaUrl = "scope/1", Metrics = { gauge, sum } },
                new ScopeMetrics { Scope = Scope, SchemaUrl = "scope/2", Metrics = { histogram } },
            } },
            new ResourceMetrics { Resource = Resource, SchemaUrl = "resource/2", ScopeMetrics =
            { new ScopeMetrics { Scope = Scope, SchemaUrl = "scope/3", Metrics = { exponential, summary } } } },
        } };
    }

    internal static ExportTraceServiceRequest Traces()
    {
        var span = new OpenTelemetry.Proto.Trace.V1.Span
        {
            Name = "child", TraceId = TraceId, SpanId = SpanId,
            ParentSpanId = ByteString.CopyFrom(Convert.FromHexString("fedcba9876543210")),
            TraceState = "vendor=value", Flags = 769, Kind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind.Server,
            StartTimeUnixNano = 12345678901234567890UL, EndTimeUnixNano = ulong.MaxValue,
            Attributes = { Attribute }, DroppedAttributesCount = 11, DroppedEventsCount = 12, DroppedLinksCount = 13,
            Status = new Status { Code = Status.Types.StatusCode.Error, Message = "failure" },
            Events = { new OpenTelemetry.Proto.Trace.V1.Span.Types.Event
                { TimeUnixNano = 12345678901234567891UL, Name = "event", Attributes = { Attribute }, DroppedAttributesCount = 3 } },
            Links = { new OpenTelemetry.Proto.Trace.V1.Span.Types.Link
                { TraceId = TraceId, SpanId = SpanId, TraceState = "link=value", Flags = 257, Attributes = { Attribute }, DroppedAttributesCount = 4 } },
        };
        var parent = span.Clone(); parent.Name = "parent"; parent.SpanId = span.ParentSpanId; parent.ParentSpanId = ByteString.Empty;
        return new ExportTraceServiceRequest { ResourceSpans =
        {
            new ResourceSpans { Resource = Resource, SchemaUrl = "resource/trace1", ScopeSpans =
            { new ScopeSpans { Scope = Scope, SchemaUrl = "scope/trace1", Spans = { parent } },
              new ScopeSpans { Scope = Scope, SchemaUrl = "scope/trace2", Spans = { span } } } },
            new ResourceSpans { Resource = Resource, SchemaUrl = "resource/trace2", ScopeSpans =
            { new ScopeSpans { Scope = Scope, Spans = { span.Clone() } } } },
        } };
    }

    internal static byte[] Wire(IMessage message, bool json) => json
        ? JsonSerializer.SerializeToUtf8Bytes(OtlpJsonCodec.Format(message)) : message.ToByteArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void All_metric_fields_survive_typed_decode_and_replay(bool json)
    {
        var request = Metrics();
        var wire = Wire(request, json);
        var type = json ? "application/json" : "application/x-protobuf";
        var actual = decoder.Decode(TelemetrySignal.Metrics, wire, type);
        Assert.Equal(10, actual.Accepted.Count);
        Assert.Equal(0, actual.RejectedCount);
        var index = 0;
        foreach (var resource in request.ResourceMetrics)
        foreach (var scope in resource.ScopeMetrics)
        foreach (var metric in scope.Metrics)
        {
            var count = metric.Gauge?.DataPoints.Count ?? 1;
            for (var p = 0; p < count; p++)
            {
                var expected = metric.Clone();
                if (expected.Gauge is not null)
                { expected.Gauge.DataPoints.Clear(); expected.Gauge.DataPoints.Add(metric.Gauge!.DataPoints[p].Clone()); }
                var leaf = actual.Accepted[index++];
                Assert.Equal(expected, leaf.Metric);
                Assert.Equal(resource.Resource, leaf.Resource);
                Assert.Equal(scope.Scope, leaf.Scope);
                Assert.Equal(resource.SchemaUrl, leaf.ResourceSchemaUrl);
                Assert.Equal(scope.SchemaUrl, leaf.ScopeSchemaUrl);
            }
        }
        var envelope = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Metrics, type, DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(wire), wire, 1, actual.Accepted.Select(l => l.Key).ToArray(), 0);
        var replay = decoder.Replay(envelope);
        Assert.Equal(actual.Accepted.Select(l => l.Metric), replay.Accepted.Select(l => l.Metric));
        Assert.True(replay.Accepted[8].Metric!.ExponentialHistogram.DataPoints[0].HasSum);
        Assert.False(replay.Accepted[0].Metric!.Gauge.DataPoints[0].ValueCase == NumberDataPoint.ValueOneofCase.AsDouble);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trace_parent_events_links_and_context_survive_typed_decode(bool json)
    {
        var request = Traces();
        var actual = decoder.Decode(TelemetrySignal.Traces, Wire(request, json), json ? "application/json" : "application/x-protobuf");
        Assert.Equal(3, actual.Accepted.Count);
        Assert.Equal(0, actual.RejectedCount);
        var index = 0;
        foreach (var resource in request.ResourceSpans)
        foreach (var scope in resource.ScopeSpans)
        foreach (var span in scope.Spans)
        {
            var leaf = actual.Accepted[index++];
            Assert.Equal(span, leaf.Span);
            Assert.Equal(resource.Resource, leaf.Resource);
            Assert.Equal(scope.Scope, leaf.Scope);
            Assert.Equal(resource.SchemaUrl, leaf.ResourceSchemaUrl);
            Assert.Equal(scope.SchemaUrl, leaf.ScopeSchemaUrl);
        }
    }

    [Fact]
    public void Independent_json_wire_hex_bytes_numeric_enum_and_int64_forms()
    {
        const string wire = """
        {"unknown":true,"resourceSpans":[{"scopeSpans":[{"spans":[{
          "traceId":"00112233445566778899AABBCCDDEEFF","spanId":"0123456789ABCDEF",
          "parentSpanId":"fedcba9876543210","name":"child","kind":2,
          "startTimeUnixNano":123,"endTimeUnixNano":"456",
          "attributes":[{"key":"raw","value":{"bytesValue":"AP8T"}}],
          "links":[{"traceId":"00112233445566778899aabbccddeeff","spanId":"0123456789abcdef"}],
          "events":[{"name":"x","timeUnixNano":"234"}]
        }]}]}]}
        """;
        var leaf = Assert.Single(decoder.Decode(TelemetrySignal.Traces, Encoding.UTF8.GetBytes(wire), "application/json").Accepted);
        Assert.Equal(TraceId, leaf.Span!.TraceId);
        Assert.Equal(SpanId, leaf.Span.SpanId);
        Assert.Equal(ByteString.CopyFrom([0, 255, 19]), Assert.Single(leaf.Span.Attributes).Value.BytesValue);
        Assert.Equal(123UL, leaf.Span.StartTimeUnixNano);
        Assert.Equal(456UL, leaf.Span.EndTimeUnixNano);
        Assert.Equal(TraceId, Assert.Single(leaf.Span.Links).TraceId);
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Traces,
            Encoding.UTF8.GetBytes(wire.Replace("\"kind\":2", "\"kind\":\"SPAN_KIND_SERVER\"", StringComparison.Ordinal)), "application/json"));
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Traces,
            Encoding.UTF8.GetBytes(wire.Replace("00112233445566778899AABBCCDDEEFF", Convert.ToBase64String(TraceId.ToByteArray()), StringComparison.Ordinal)), "application/json"));
        Assert.Empty(decoder.Decode(TelemetrySignal.Traces,
            Encoding.UTF8.GetBytes(wire.Replace("resourceSpans", "resource_spans", StringComparison.Ordinal)), "application/json").Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_rejection_counts_leaves_across_resources_and_scopes(bool json)
    {
        var request = Traces();
        request.ResourceSpans[0].ScopeSpans[0].Spans[0].TraceId = ByteString.CopyFrom(new byte[16]);
        request.ResourceSpans[1].ScopeSpans[0].Spans[0].EndTimeUnixNano = 1;
        var actual = decoder.Decode(TelemetrySignal.Traces, Wire(request, json), json ? "application/json" : "application/x-protobuf");
        Assert.Equal(2, actual.RejectedCount);
        Assert.Equal("t/0/1/0", Assert.Single(actual.Accepted).Key);
        var metrics = Metrics();
        metrics.ResourceMetrics[0].ScopeMetrics[0].Metrics[0].Name = "";
        metrics.ResourceMetrics[0].ScopeMetrics[1].Metrics[0].Histogram.DataPoints[0].Count = 100;
        actual = decoder.Decode(TelemetrySignal.Metrics, Wire(metrics, json), json ? "application/json" : "application/x-protobuf");
        Assert.Equal(7, actual.RejectedCount);
        Assert.Equal(3, actual.Accepted.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"resourceMetrics\":{}}")]
    [InlineData("{\"resourceMetrics\":[false]}")]
    public void Malformed_json_is_protocol_rejection(string json) => Assert.Throws<OtlpDecodeException>(() =>
        decoder.Decode(TelemetrySignal.Metrics, Encoding.UTF8.GetBytes(json), "application/json"));

    [Fact]
    public void Histogram_distribution_is_optional_but_half_present_is_invalid()
    {
        var fixture = Metrics();
        var point = fixture.ResourceMetrics[0].ScopeMetrics[1].Metrics[0].Histogram.DataPoints[0];
        point.BucketCounts.Clear(); point.ExplicitBounds.Clear(); point.ClearSum();
        var decoded = decoder.Decode(TelemetrySignal.Metrics, fixture.ToByteArray(), "application/x-protobuf");
        Assert.Equal(10, decoded.Accepted.Count);
        Assert.False(decoded.Accepted[7].Metric!.Histogram.DataPoints[0].HasSum);
        point.ExplicitBounds.Add(1);
        decoded = decoder.Decode(TelemetrySignal.Metrics, fixture.ToByteArray(), "application/x-protobuf");
        Assert.Equal(1, decoded.RejectedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exactly_two_valid_three_rejected_spans_survive_replay(bool json)
    {
        var fixture = Traces();
        var group = fixture.ResourceSpans[1].ScopeSpans[0];
        group.Spans[0].EndTimeUnixNano = 1;
        group.Spans.Add(group.Spans[0].Clone());
        group.Spans.Add(group.Spans[0].Clone());
        var wire = Wire(fixture, json);
        var type = json ? "application/json" : "application/x-protobuf";
        var decoded = decoder.Decode(TelemetrySignal.Traces, wire, type);
        Assert.Equal(2, decoded.Accepted.Count);
        Assert.Equal(3, decoded.RejectedCount);
        var envelope = new RawSignalEnvelope(1, Guid.NewGuid(), TelemetrySignal.Traces, type, DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(wire), wire, 1, decoded.Accepted.Select(x => x.Key).ToArray(), 3);
        var replay = decoder.Replay(envelope);
        Assert.Equal(2, replay.Accepted.Count);
        Assert.Equal(3, replay.RejectedCount);
        Assert.Equal(decoded.Accepted.Select(x => x.Span), replay.Accepted.Select(x => x.Span));
    }

    [Fact]
    public void Malformed_protobuf_utf8_and_nested_identifiers_are_rejected()
    {
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Metrics, new byte[] { 0xff }, "application/x-protobuf"));
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Metrics, new byte[] { 0xff }, "application/json"));
        var fixture = OtlpJsonCodec.Format(Traces());
        var span = fixture["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!;
        span["links"]![0]!["traceId"] = "not hex";
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Traces, JsonSerializer.SerializeToUtf8Bytes(fixture), "application/json"));
        fixture = OtlpJsonCodec.Format(Traces());
        fixture["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]![0]!["spanId"] = 42;
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Traces, JsonSerializer.SerializeToUtf8Bytes(fixture), "application/json"));
        var metricFixture = OtlpJsonCodec.Format(Metrics());
        metricFixture["resourceMetrics"]![0]!["scopeMetrics"]![0]!["metrics"]![0]!["gauge"]!["dataPoints"]![0]!["exemplars"]![0]!["spanId"] = "abcd";
        Assert.Throws<OtlpDecodeException>(() => decoder.Decode(TelemetrySignal.Metrics, JsonSerializer.SerializeToUtf8Bytes(metricFixture), "application/json"));
    }
}
