using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;

namespace Bizigo.Api;

// The read representation is separate from OTLP ingestion. Integer and nano
// values remain decimal strings all the way through the generated client.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TelemetryIntegerDto), "int")]
[JsonDerivedType(typeof(TelemetryDoubleDto), "double")]
public abstract record TelemetryNumberDto;
public sealed record TelemetryIntegerDto([property: JsonPropertyName("value")] string Value) : TelemetryNumberDto;
public sealed record TelemetryDoubleDto([property: JsonPropertyName("value"), JsonNumberHandling(JsonNumberHandling.Strict)] double? Value,
    [property: JsonPropertyName("special")] string? Special) : TelemetryNumberDto;
public sealed record TelemetryOptionalNumberDto([property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("value")] TelemetryNumberDto? Value);
public sealed record TelemetryAttributeDto([property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] TelemetryAnyValueDto Value);
public sealed record TelemetryAnyValueDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("number")] TelemetryNumberDto? Number = null,
    [property: JsonPropertyName("boolean")] bool? Boolean = null,
    [property: JsonPropertyName("array")] IReadOnlyList<TelemetryAnyValueDto>? Array = null,
    [property: JsonPropertyName("entries")] IReadOnlyList<TelemetryAttributeDto>? Entries = null);
public sealed record TelemetryResourceDto([property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("dropped_attributes_count")] string DroppedAttributesCount);
public sealed record TelemetryScopeDto([property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("dropped_attributes_count")] string DroppedAttributesCount);
public sealed record TelemetryExemplarDto(
    [property: JsonPropertyName("filtered_attributes")] IReadOnlyList<TelemetryAttributeDto> FilteredAttributes,
    [property: JsonPropertyName("time_unix_nano")] string TimeUnixNano,
    [property: JsonPropertyName("value")] TelemetryOptionalNumberDto Value,
    [property: JsonPropertyName("span_id")] string SpanId,
    [property: JsonPropertyName("trace_id")] string TraceId);
public sealed record TelemetryBucketsDto([property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("bucket_counts")] IReadOnlyList<string> BucketCounts);
public sealed record TelemetryQuantileDto([property: JsonPropertyName("quantile")] TelemetryNumberDto Quantile,
    [property: JsonPropertyName("value")] TelemetryNumberDto Value);
public sealed record TelemetryPointDto(
    [property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("start_time_unix_nano")] string StartTimeUnixNano,
    [property: JsonPropertyName("time_unix_nano")] string TimeUnixNano,
    [property: JsonPropertyName("value")] TelemetryOptionalNumberDto Value,
    [property: JsonPropertyName("count")] string? Count,
    [property: JsonPropertyName("sum")] TelemetryOptionalNumberDto Sum,
    [property: JsonPropertyName("min")] TelemetryOptionalNumberDto Min,
    [property: JsonPropertyName("max")] TelemetryOptionalNumberDto Max,
    [property: JsonPropertyName("bucket_counts")] IReadOnlyList<string> BucketCounts,
    [property: JsonPropertyName("explicit_bounds")] IReadOnlyList<TelemetryNumberDto> ExplicitBounds,
    [property: JsonPropertyName("scale")] int? Scale,
    [property: JsonPropertyName("zero_count")] string? ZeroCount,
    [property: JsonPropertyName("zero_threshold")] TelemetryOptionalNumberDto ZeroThreshold,
    [property: JsonPropertyName("positive")] TelemetryBucketsDto? Positive,
    [property: JsonPropertyName("negative")] TelemetryBucketsDto? Negative,
    [property: JsonPropertyName("quantile_values")] IReadOnlyList<TelemetryQuantileDto> QuantileValues,
    [property: JsonPropertyName("exemplars")] IReadOnlyList<TelemetryExemplarDto> Exemplars,
    [property: JsonPropertyName("flags")] string Flags);
public sealed record TelemetryMetricDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("aggregation_temporality")] int? AggregationTemporality,
    [property: JsonPropertyName("is_monotonic")] bool? IsMonotonic,
    [property: JsonPropertyName("metadata")] IReadOnlyList<TelemetryAttributeDto> Metadata,
    [property: JsonPropertyName("data_points")] IReadOnlyList<TelemetryPointDto> DataPoints);
public sealed record TelemetrySpanEventDto(
    [property: JsonPropertyName("time_unix_nano")] string TimeUnixNano,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("dropped_attributes_count")] string DroppedAttributesCount);
public sealed record TelemetrySpanLinkDto(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("span_id")] string SpanId,
    [property: JsonPropertyName("trace_state")] string TraceState,
    [property: JsonPropertyName("flags")] string Flags,
    [property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("dropped_attributes_count")] string DroppedAttributesCount);
public sealed record TelemetrySpanDto(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("span_id")] string SpanId,
    [property: JsonPropertyName("parent_span_id")] string ParentSpanId,
    [property: JsonPropertyName("trace_state")] string TraceState,
    [property: JsonPropertyName("flags")] string Flags,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("start_time_unix_nano")] string StartTimeUnixNano,
    [property: JsonPropertyName("end_time_unix_nano")] string EndTimeUnixNano,
    [property: JsonPropertyName("attributes")] IReadOnlyList<TelemetryAttributeDto> Attributes,
    [property: JsonPropertyName("dropped_attributes_count")] string DroppedAttributesCount,
    [property: JsonPropertyName("events")] IReadOnlyList<TelemetrySpanEventDto> Events,
    [property: JsonPropertyName("dropped_events_count")] string DroppedEventsCount,
    [property: JsonPropertyName("links")] IReadOnlyList<TelemetrySpanLinkDto> Links,
    [property: JsonPropertyName("dropped_links_count")] string DroppedLinksCount,
    [property: JsonPropertyName("status_code")] int StatusCode,
    [property: JsonPropertyName("status_message")] string StatusMessage);
public sealed record TelemetryRecordDto(
    [property: JsonPropertyName("logical_id")] string LogicalId,
    [property: JsonPropertyName("signal")] string Signal,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("owner_group")] string OwnerGroup,
    [property: JsonPropertyName("time_unix_nano")] string TimeUnixNano,
    [property: JsonPropertyName("resource_schema_url")] string ResourceSchemaUrl,
    [property: JsonPropertyName("scope_schema_url")] string ScopeSchemaUrl,
    [property: JsonPropertyName("resource")] TelemetryResourceDto Resource,
    [property: JsonPropertyName("scope")] TelemetryScopeDto Scope,
    [property: JsonPropertyName("metric")] TelemetryMetricDto? Metric,
    [property: JsonPropertyName("span")] TelemetrySpanDto? Span);
public sealed record TelemetryPageDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("records")] IReadOnlyList<TelemetryRecordDto> Records,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("reason")] string? Reason);
public sealed record TelemetryCountDto([property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("count")] string? Count,
    [property: JsonPropertyName("measured")] bool Measured,
    [property: JsonPropertyName("reason")] string? Reason);
public sealed record TelemetrySummaryDto([property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("count")] string Count,
    [property: JsonPropertyName("first_nano")] string FirstNano,
    [property: JsonPropertyName("last_nano")] string LastNano,
    [property: JsonPropertyName("last")] TelemetryRecordDto Last);
public sealed record TelemetrySummaryPageDto([property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("groups")] IReadOnlyList<TelemetrySummaryDto> Groups,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("reason")] string? Reason);

public static class TelemetryWire
{
    private static JsonElement Field(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement element, string key) => Field(element, key).ValueKind == JsonValueKind.String ? Field(element, key).GetString()! : "";
    private static IEnumerable<JsonElement> Array(JsonElement element, string key) => Field(element, key).ValueKind == JsonValueKind.Array
        ? Field(element, key).EnumerateArray() : [];
    private static string Integer(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? "0"
        : BigInteger.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
    private static string Integer(JsonElement element, string key) => Integer(Field(element, key));
    private static int Small(JsonElement element, string key) => int.Parse(Integer(element, key), CultureInfo.InvariantCulture);
    private static TelemetryNumberDto Double(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.Undefined ? "0" : value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (text is "NaN" or "Infinity" or "+Infinity" or "-Infinity") return new TelemetryDoubleDto(null, text == "Infinity" ? "+Infinity" : text);
        var number = double.Parse(text, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new InvalidDataException("Invalid typed double.");
        return new TelemetryDoubleDto(number, null);
    }
    private static TelemetryOptionalNumberDto Optional(JsonElement element, string field) => Field(element, field).ValueKind == JsonValueKind.Undefined
        ? new(false, null) : new(true, Double(Field(element, field)));
    private static TelemetryOptionalNumberDto Value(JsonElement element) => Field(element, "asInt").ValueKind != JsonValueKind.Undefined
        ? new(true, new TelemetryIntegerDto(Integer(element, "asInt"))) : Optional(element, "asDouble");
    private static TelemetryAttributeDto Attribute(JsonElement element) => new(Text(element, "key"), Any(Field(element, "value")));
    private static IReadOnlyList<TelemetryAttributeDto> Attributes(JsonElement element, string key = "attributes") => Array(element, key).Select(Attribute).ToArray();
    private static TelemetryAnyValueDto Any(JsonElement value)
    {
        if (Field(value, "intValue").ValueKind != JsonValueKind.Undefined) return new("int", Number: new TelemetryIntegerDto(Integer(value, "intValue")));
        if (Field(value, "doubleValue").ValueKind != JsonValueKind.Undefined) return new("double", Number: Double(Field(value, "doubleValue")));
        if (Field(value, "boolValue").ValueKind != JsonValueKind.Undefined) return new("bool", Boolean: Field(value, "boolValue").GetBoolean());
        if (Field(value, "stringValue").ValueKind != JsonValueKind.Undefined) return new("string", Text: Text(value, "stringValue"));
        if (Field(value, "bytesValue").ValueKind != JsonValueKind.Undefined) return new("bytes", Text: Text(value, "bytesValue"));
        if (Field(value, "arrayValue").ValueKind != JsonValueKind.Undefined) return new("array", Array: Array(Field(value, "arrayValue"), "values").Select(Any).ToArray());
        if (Field(value, "kvlistValue").ValueKind != JsonValueKind.Undefined) return new("kvlist", Entries: Attributes(Field(value, "kvlistValue"), "values"));
        return new("empty");
    }
    private static TelemetryBucketsDto? Buckets(JsonElement point, string key) => Field(point, key).ValueKind == JsonValueKind.Undefined ? null
        : new(Small(Field(point, key), "offset"), Array(Field(point, key), "bucketCounts").Select(Integer).ToArray());
    private static TelemetryPointDto Point(JsonElement point, string kind) => new(
        Attributes(point), Integer(point, "startTimeUnixNano"), Integer(point, "timeUnixNano"), Value(point),
        kind is "Gauge" or "Sum" ? null : Integer(point, "count"), Optional(point, "sum"), Optional(point, "min"), Optional(point, "max"),
        Array(point, "bucketCounts").Select(Integer).ToArray(), Array(point, "explicitBounds").Select(Double).ToArray(),
        kind == "ExponentialHistogram" ? Small(point, "scale") : null, kind == "ExponentialHistogram" ? Integer(point, "zeroCount") : null,
        Optional(point, "zeroThreshold"), Buckets(point, "positive"), Buckets(point, "negative"),
        Array(point, "quantileValues").Select(q => new TelemetryQuantileDto(Double(Field(q, "quantile")), Double(Field(q, "value")))).ToArray(),
        Array(point, "exemplars").Select(e => new TelemetryExemplarDto(Attributes(e, "filteredAttributes"), Integer(e, "timeUnixNano"), Value(e), Text(e, "spanId"), Text(e, "traceId"))).ToArray(),
        Integer(point, "flags"));
    private static TelemetryMetricDto Metric(JsonElement metric, string kind)
    {
        var data = Field(metric, char.ToLowerInvariant(kind[0]) + kind[1..]);
        return new(Text(metric, "name"), Text(metric, "description"), Text(metric, "unit"), kind,
            kind is "Sum" or "Histogram" or "ExponentialHistogram" ? Small(data, "aggregationTemporality") : null,
            kind == "Sum" ? Field(data, "isMonotonic").ValueKind == JsonValueKind.True : null,
            Attributes(metric, "metadata"), Array(data, "dataPoints").Select(p => Point(p, kind)).ToArray());
    }
    private static TelemetrySpanDto Span(JsonElement span) => new(Text(span, "traceId"), Text(span, "spanId"), Text(span, "parentSpanId"),
        Text(span, "traceState"), Integer(span, "flags"), Text(span, "name"), Small(span, "kind"), Integer(span, "startTimeUnixNano"), Integer(span, "endTimeUnixNano"),
        Attributes(span), Integer(span, "droppedAttributesCount"),
        Array(span, "events").Select(e => new TelemetrySpanEventDto(Integer(e, "timeUnixNano"), Text(e, "name"), Attributes(e), Integer(e, "droppedAttributesCount"))).ToArray(),
        Integer(span, "droppedEventsCount"),
        Array(span, "links").Select(l => new TelemetrySpanLinkDto(Text(l, "traceId"), Text(l, "spanId"), Text(l, "traceState"), Integer(l, "flags"), Attributes(l), Integer(l, "droppedAttributesCount"))).ToArray(),
        Integer(span, "droppedLinksCount"), Small(Field(span, "status"), "code"), Text(Field(span, "status"), "message"));
    public static TelemetryRecordDto Record(TelemetryRecord row) => new(row.LogicalId, row.Signal.ToString(), row.Owner.SourceId, row.Owner.OwnerGroup,
        row.TimeUnixNano.ToString(CultureInfo.InvariantCulture), row.ResourceSchemaUrl, row.ScopeSchemaUrl,
        new(Attributes(row.Resource), Integer(row.Resource, "droppedAttributesCount")),
        new(Text(row.Scope, "name"), Text(row.Scope, "version"), Attributes(row.Scope), Integer(row.Scope, "droppedAttributesCount")),
        row.Metric is { } metric ? Metric(metric, row.Kind) : null, row.Span is { } span ? Span(span) : null);
}
