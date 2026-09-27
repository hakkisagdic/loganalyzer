using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Bizigo.Ingest.Otlp;

public static class TelemetryMaterializer
{
    public static TelemetryOwnershipRequest Ownership(TelemetryLeaf leaf) => new(leaf.Key, Time(leaf),
        SourceDirectory.TelemetryCandidateOrder.Select(name => leaf.Resource.Attributes.FirstOrDefault(a => a.Key == name)?.Value)
            .Where(v => v?.ValueCase == AnyValue.ValueOneofCase.StringValue).Select(v => v!.StringValue).ToArray());

    public static ulong Time(TelemetryLeaf leaf) => leaf.Metric?.DataCase switch
    {
        Metric.DataOneofCase.Gauge => leaf.Metric.Gauge.DataPoints[0].TimeUnixNano,
        Metric.DataOneofCase.Sum => leaf.Metric.Sum.DataPoints[0].TimeUnixNano,
        Metric.DataOneofCase.Histogram => leaf.Metric.Histogram.DataPoints[0].TimeUnixNano,
        Metric.DataOneofCase.ExponentialHistogram => leaf.Metric.ExponentialHistogram.DataPoints[0].TimeUnixNano,
        Metric.DataOneofCase.Summary => leaf.Metric.Summary.DataPoints[0].TimeUnixNano,
        _ => leaf.Span?.StartTimeUnixNano ?? 0,
    };

    public static TelemetryRecord Materialize(RawSignalEnvelope envelope, TelemetryLeaf leaf, TelemetryOwnerBinding owner)
    {
        if (owner.LeafKey != leaf.Key || owner.EventTimeUnixNano != Time(leaf))
            throw new InvalidDataException("Owner decision does not match telemetry leaf/time.");
        var metric = leaf.Metric;
        var temporality = (int)(metric?.Sum?.AggregationTemporality ?? metric?.Histogram?.AggregationTemporality
            ?? metric?.ExponentialHistogram?.AggregationTemporality ?? 0);
        var attributes = metric?.DataCase switch
        {
            Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints[0].Attributes,
            Metric.DataOneofCase.Sum => metric.Sum.DataPoints[0].Attributes,
            Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints[0].Attributes,
            Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints[0].Attributes,
            Metric.DataOneofCase.Summary => metric.Summary.DataPoints[0].Attributes,
            _ => null,
        };
        var kind = metric?.DataCase.ToString() ?? leaf.Span!.Kind.ToString();
        var unit = metric?.Unit ?? string.Empty;
        var monotonic = metric?.Sum?.IsMonotonic ?? false;
        var series = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = metric?.Name ?? leaf.Span!.Name, kind, unit, temporality, monotonic,
            source = owner.SourceId, owner.OwnerGroup,
            scope = Convert.ToBase64String(leaf.Scope.ToByteArray()), leaf.ResourceSchemaUrl, leaf.ScopeSchemaUrl,
            attributes = attributes?.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => Convert.ToBase64String(a.ToByteArray())).ToArray(),
        });
        return new(1, envelope.EnvelopeId, envelope.EnvelopeId.ToString("N") + "/" + leaf.Key, envelope.Signal,
            envelope.PayloadSha256, envelope.OwnerBindingsSha256!, owner, Time(leaf), metric?.Name ?? leaf.Span!.Name,
            leaf.Resource.Attributes.FirstOrDefault(a => a.Key == "service.name")?.Value.StringValue ?? string.Empty,
            kind, unit, temporality, monotonic,
            leaf.Span is null ? string.Empty : Convert.ToHexStringLower(leaf.Span.TraceId.Span),
            leaf.Span is null ? string.Empty : Convert.ToHexStringLower(leaf.Span.SpanId.Span),
            (int)(leaf.Span?.Status?.Code ?? 0), RawSignalEnvelope.Hash(series),
            Json(leaf.Resource), Json(leaf.Scope), leaf.ResourceSchemaUrl, leaf.ScopeSchemaUrl,
            metric is null ? null : Json(metric), leaf.Span is null ? null : Json(leaf.Span)) { RetentionDays = envelope.RetentionDays };
    }

    private static JsonElement Json(IMessage value)
    {
        using var document = JsonDocument.Parse(OtlpJsonCodec.Format(value).ToJsonString());
        return document.RootElement.Clone();
    }
}
