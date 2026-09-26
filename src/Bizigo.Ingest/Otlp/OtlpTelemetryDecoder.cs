using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Bizigo.Ingest.Otlp;

/// <summary>One complete typed leaf with its original resource/scope context.</summary>
public sealed record TelemetryLeaf(string Key, Resource Resource, InstrumentationScope Scope,
    string ResourceSchemaUrl, string ScopeSchemaUrl, Metric? Metric, Span? Span);
public sealed record TelemetryDecode(IReadOnlyList<TelemetryLeaf> Accepted, int RejectedCount);

public sealed class OtlpTelemetryDecoder
{
    public static string ContentType(string? value) => (value ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

    public TelemetryDecode Decode(TelemetrySignal signal, ReadOnlyMemory<byte> bytes, string contentType)
    {
        try
        {
            if (contentType is not (OtlpLogsDecoder.JsonContentType or OtlpLogsDecoder.ProtobufContentType))
                throw new OtlpDecodeException("Unsupported Content-Type.");
            return signal switch
            {
                TelemetrySignal.Metrics => Metrics(contentType == OtlpLogsDecoder.JsonContentType
                    ? OtlpJsonCodec.Parse<ExportMetricsServiceRequest>(bytes.Span, ExportMetricsServiceRequest.Descriptor)
                    : ExportMetricsServiceRequest.Parser.ParseFrom(bytes.Span)),
                TelemetrySignal.Traces => Traces(contentType == OtlpLogsDecoder.JsonContentType
                    ? OtlpJsonCodec.Parse<ExportTraceServiceRequest>(bytes.Span, ExportTraceServiceRequest.Descriptor)
                    : ExportTraceServiceRequest.Parser.ParseFrom(bytes.Span)),
                _ => throw new OtlpDecodeException("Unsupported signal."),
            };
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException
                                   or JsonException or FormatException or DecoderFallbackException or OverflowException)
        { throw new OtlpDecodeException("Invalid OTLP export.", ex); }
    }

    public TelemetryDecode Replay(RawSignalEnvelope envelope)
    {
        envelope.Validate();
        var decoded = Decode(envelope.Signal, envelope.Payload, envelope.ContentType);
        if (decoded.RejectedCount != envelope.RejectedCount
            || !decoded.Accepted.Select(x => x.Key).SequenceEqual(envelope.AcceptedKeys, StringComparer.Ordinal))
            throw new InvalidDataException("Stored admission decision differs from decoder validation version.");
        return decoded;
    }

    private static TelemetryDecode Metrics(ExportMetricsServiceRequest request)
    {
        var accepted = new List<TelemetryLeaf>();
        var rejected = 0;
        for (var r = 0; r < request.ResourceMetrics.Count; r++)
        {
            var resource = request.ResourceMetrics[r];
            for (var s = 0; s < resource.ScopeMetrics.Count; s++)
            {
                var scope = resource.ScopeMetrics[s];
                for (var m = 0; m < scope.Metrics.Count; m++)
                {
                    var metric = scope.Metrics[m];
                    var count = Count(metric);
                    for (var p = 0; p < count; p++)
                    {
                        var single = OnePoint(metric, p);
                        if (string.IsNullOrWhiteSpace(metric.Name) || !Valid(single)) { rejected++; continue; }
                        accepted.Add(new(string.Create(CultureInfo.InvariantCulture, $"m/{r}/{s}/{m}/{p}"),
                            resource.Resource?.Clone() ?? new(), scope.Scope?.Clone() ?? new(),
                            resource.SchemaUrl, scope.SchemaUrl, single, null));
                    }
                }
            }
        }
        return new(accepted, rejected);
    }

    private static int Count(Metric m) => m.DataCase switch
    {
        Metric.DataOneofCase.Gauge => m.Gauge.DataPoints.Count,
        Metric.DataOneofCase.Sum => m.Sum.DataPoints.Count,
        Metric.DataOneofCase.Histogram => m.Histogram.DataPoints.Count,
        Metric.DataOneofCase.ExponentialHistogram => m.ExponentialHistogram.DataPoints.Count,
        Metric.DataOneofCase.Summary => m.Summary.DataPoints.Count,
        _ => 0,
    };

    private static Metric OnePoint(Metric original, int index)
    {
        var copy = original.Clone();
        switch (copy.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                copy.Gauge.DataPoints.Clear(); copy.Gauge.DataPoints.Add(original.Gauge.DataPoints[index].Clone()); break;
            case Metric.DataOneofCase.Sum:
                copy.Sum.DataPoints.Clear(); copy.Sum.DataPoints.Add(original.Sum.DataPoints[index].Clone()); break;
            case Metric.DataOneofCase.Histogram:
                copy.Histogram.DataPoints.Clear(); copy.Histogram.DataPoints.Add(original.Histogram.DataPoints[index].Clone()); break;
            case Metric.DataOneofCase.ExponentialHistogram:
                copy.ExponentialHistogram.DataPoints.Clear(); copy.ExponentialHistogram.DataPoints.Add(original.ExponentialHistogram.DataPoints[index].Clone()); break;
            case Metric.DataOneofCase.Summary:
                copy.Summary.DataPoints.Clear(); copy.Summary.DataPoints.Add(original.Summary.DataPoints[index].Clone()); break;
        }
        return copy;
    }

    private static bool Id(ByteString bytes, int size, bool optional = false) =>
        (optional && bytes.IsEmpty) || (bytes.Length == size && bytes.Span.ContainsAnyExcept((byte)0));

    private static bool Exemplars(IEnumerable<Exemplar> exemplars) => exemplars.All(e =>
        Id(e.TraceId, 16, true) && Id(e.SpanId, 8, true));

    private static bool Valid(Metric metric)
    {
        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge: return Exemplars(metric.Gauge.DataPoints[0].Exemplars);
            case Metric.DataOneofCase.Sum: return Exemplars(metric.Sum.DataPoints[0].Exemplars);
            case Metric.DataOneofCase.Histogram:
                var h = metric.Histogram.DataPoints[0];
                return ((h.BucketCounts.Count == 0 && h.ExplicitBounds.Count == 0)
                    || (h.BucketCounts.Count == h.ExplicitBounds.Count + 1 && SumEquals(h.BucketCounts, h.Count)))
                    && h.ExplicitBounds.Zip(h.ExplicitBounds.Skip(1)).All(x => x.First < x.Second)
                    && Exemplars(h.Exemplars);
            case Metric.DataOneofCase.ExponentialHistogram:
                var e = metric.ExponentialHistogram.DataPoints[0];
                return SumEquals((e.Positive?.BucketCounts ?? []).Concat(e.Negative?.BucketCounts ?? []).Append(e.ZeroCount), e.Count)
                    && Exemplars(e.Exemplars);
            default: return true;
        }
    }

    private static bool SumEquals(IEnumerable<ulong> values, ulong expected)
    {
        ulong sum = 0;
        foreach (var value in values) { if (ulong.MaxValue - sum < value) return false; sum += value; }
        return sum == expected;
    }

    private static TelemetryDecode Traces(ExportTraceServiceRequest request)
    {
        var accepted = new List<TelemetryLeaf>();
        var rejected = 0;
        for (var r = 0; r < request.ResourceSpans.Count; r++)
        {
            var resource = request.ResourceSpans[r];
            for (var s = 0; s < resource.ScopeSpans.Count; s++)
            {
                var scope = resource.ScopeSpans[s];
                for (var p = 0; p < scope.Spans.Count; p++)
                {
                    var span = scope.Spans[p];
                    if (!Id(span.TraceId, 16) || !Id(span.SpanId, 8) || !Id(span.ParentSpanId, 8, true)
                        || span.EndTimeUnixNano < span.StartTimeUnixNano
                        || span.Links.Any(l => !Id(l.TraceId, 16) || !Id(l.SpanId, 8))) { rejected++; continue; }
                    accepted.Add(new(string.Create(CultureInfo.InvariantCulture, $"t/{r}/{s}/{p}"),
                        resource.Resource?.Clone() ?? new(), scope.Scope?.Clone() ?? new(),
                        resource.SchemaUrl, scope.SchemaUrl, null, span.Clone()));
                }
            }
        }
        return new(accepted, rejected);
    }
}
