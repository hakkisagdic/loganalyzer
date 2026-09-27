using System.Text.Json;
using Bizigo.Api;
using Bizigo.Contracts;
using Microsoft.AspNetCore.Http;

namespace Bizigo.UnitTests;

public sealed class TelemetryReadContractTests
{
    private static IQueryCollection Query(string query)
    {
        var context = new DefaultHttpContext(); context.Request.QueryString = new(query); return context.Request.Query;
    }
    private static readonly AccessScope Scope = AccessScope.ForGroups("reader", ["A"]);

    [Fact]
    public void Integral_nanos_and_cursor_preserve_exact_boundary_and_binding()
    {
        const string fields = "?from_nano=18446744073709551614&to_nano=18446744073709551616&limit=2";
        var parsed = TelemetryReadRequest.Parse(Query(fields), TelemetrySignal.Metrics, "list", Scope);
        Assert.Equal(18446744073709551616m, parsed.Query.ToNano);
        var cursor = Uri.EscapeDataString(parsed.Wrap("database-cursor")!);
        var repeated = TelemetryReadRequest.Parse(Query(fields + "&cursor=" + cursor), TelemetrySignal.Metrics, "list", Scope);
        Assert.Equal("database-cursor", repeated.Query.Cursor);
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query(fields + "&cursor=" + cursor), TelemetrySignal.Metrics, "summary", Scope));
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query(fields + "&cursor=" + cursor), TelemetrySignal.Traces, "list", Scope));
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query(fields + "&cursor=" + cursor), TelemetrySignal.Metrics, "list", AccessScope.ForGroups("reader", ["B"])));
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query(fields.Replace("limit=2", "limit=3", StringComparison.Ordinal) + "&cursor=" + cursor), TelemetrySignal.Metrics, "list", Scope));
    }

    [Theory]
    [InlineData("?from_nano=0")]
    [InlineData("?from_nano=-1&to_nano=2")]
    [InlineData("?from_nano=1.0&to_nano=2")]
    [InlineData("?from_nano=1e0&to_nano=2")]
    [InlineData("?from_nano=0&to_nano=18446744073709551617")]
    [InlineData("?from_nano=0&to_nano=2678400000000001")]
    [InlineData("?from_nano=1&to_nano=1")]
    [InlineData("?from_nano=0&to_nano=2&limit=0")]
    [InlineData("?from_nano=0&to_nano=2&limit=1001")]
    [InlineData("?from_nano=0&to_nano=2&sql=x")]
    [InlineData("?from_nano=0&to_nano=2&trace_id=00")]
    [InlineData("?from_nano=0&to_nano=2&kind=gauge")]
    [InlineData("?from_nano=0&to_nano=2&name=a&name=b")]
    [InlineData("?from_nano=0&to_nano=2&owner_groups=")]
    public void Invalid_parameters_never_reach_queries(string query) =>
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query(query), TelemetrySignal.Metrics, "list", Scope));

    [Theory]
    [InlineData("count", "limit=1")]
    [InlineData("feed", "cursor=x")]
    [InlineData("feed", "name=x")]
    [InlineData("point", "resource_id=x")]
    [InlineData("span", "limit=2")]
    public void Per_route_whitelist_is_enforced(string route, string extra) =>
        Assert.Throws<ArgumentException>(() => TelemetryReadRequest.Parse(Query("?from_nano=0&to_nano=2&" + extra), TelemetrySignal.Metrics, route, Scope));

    [Fact]
    public void Typed_wire_integer_exemplar_attribute_and_nanos_are_decimal_strings()
    {
        const string payload = """
        {"name":"exact","unit":"ms","gauge":{"dataPoints":[{"timeUnixNano":"18446744073709551615",
        "asInt":"9007199254740993","attributes":[{"key":"n","value":{"intValue":"9223372036854775807"}}],
        "exemplars":[{"timeUnixNano":"18446744073709551614","asInt":"9223372036854775807","traceId":"abcd","spanId":"ef"}]}]}}
        """;
        var row = MetricEvidenceProviderTests.Point("0", ulong.MaxValue) with { Metric = JsonSerializer.Deserialize<JsonElement>(payload) };
        var dto = TelemetryWire.Record(row);
        Export("integer", dto);
        var point = Assert.Single(dto.Metric!.DataPoints);
        Assert.Equal("9007199254740993", Assert.IsType<TelemetryIntegerDto>(point.Value.Value).Value);
        Assert.Equal("9223372036854775807", Assert.IsType<TelemetryIntegerDto>(Assert.Single(point.Attributes).Value.Number).Value);
        Assert.Equal("9223372036854775807", Assert.IsType<TelemetryIntegerDto>(Assert.Single(point.Exemplars).Value.Value).Value);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(dto));
        var p = wire.RootElement.GetProperty("metric").GetProperty("data_points")[0];
        Assert.Equal("int", p.GetProperty("value").GetProperty("value").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.String, p.GetProperty("value").GetProperty("value").GetProperty("value").ValueKind);
        Assert.Equal("18446744073709551615", p.GetProperty("time_unix_nano").GetString());
    }

    [Theory]
    [InlineData("NaN", "NaN")]
    [InlineData("Infinity", "+Infinity")]
    [InlineData("-Infinity", "-Infinity")]
    public void Typed_wire_nonfinite_and_optional_absent_are_explicit(string value, string special)
    {
        var metric = JsonSerializer.SerializeToElement(new { histogram = new { dataPoints = new[] { new { count = "18446744073709551615", sum = value, min = 0, bucketCounts = new[] { "18446744073709551615" } } } } });
        var dto = TelemetryWire.Record(MetricEvidenceProviderTests.Point("0", 1, "Histogram") with { Metric = metric });
        Export(value, dto);
        var point = Assert.Single(dto.Metric!.DataPoints);
        Assert.Equal("18446744073709551615", point.Count); Assert.Equal(["18446744073709551615"], point.BucketCounts);
        Assert.True(point.Sum.Present); var sum = Assert.IsType<TelemetryDoubleDto>(point.Sum.Value);
        Assert.Null(sum.Value); Assert.Equal(special, sum.Special);
        Assert.True(point.Min.Present); Assert.Equal(0, Assert.IsType<TelemetryDoubleDto>(point.Min.Value).Value);
        Assert.False(point.Max.Present); Assert.Null(point.Max.Value);
    }

    [Fact]
    public void Typed_span_parent_event_link_and_dropped_counts_survive_mapping()
    {
        const string json = """
        {"traceId":"11111111111111111111111111111111","spanId":"2222222222222222","parentSpanId":"3333333333333333",
        "traceState":"a=b","flags":257,"name":"request","kind":2,"startTimeUnixNano":"9007199254740993","endTimeUnixNano":"9007199254740994",
        "droppedAttributesCount":3,"droppedEventsCount":4,"droppedLinksCount":5,"status":{"code":2,"message":"failed"},
        "events":[{"timeUnixNano":"9007199254740993","name":"exception","droppedAttributesCount":7}],
        "links":[{"traceId":"44444444444444444444444444444444","spanId":"5555555555555555","traceState":"c=d","flags":1,"droppedAttributesCount":9}]}
        """;
        var dto = TelemetryWire.Record(MetricEvidenceProviderTests.Point("0", 9007199254740993) with
        { Metric = null, Span = JsonSerializer.Deserialize<JsonElement>(json), Signal = TelemetrySignal.Traces });
        Export("span", dto);
        Assert.Equal("3333333333333333", dto.Span!.ParentSpanId); Assert.Equal("9007199254740994", dto.Span.EndTimeUnixNano);
        Assert.Equal("9007199254740993", Assert.Single(dto.Span.Events).TimeUnixNano);
        Assert.Equal("c=d", Assert.Single(dto.Span.Links).TraceState); Assert.Equal("9", Assert.Single(dto.Span.Links).DroppedAttributesCount);
        Assert.Equal("257", dto.Span.Flags); Assert.Equal("5", dto.Span.DroppedLinksCount); Assert.Equal(2, dto.Span.StatusCode);
    }

    private static void Export(string name, TelemetryRecordDto dto)
    {
        var folder = Environment.GetEnvironmentVariable("BIZIGO_TELEMETRY_WIRE_EVIDENCE");
        if (folder is null) return;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name + ".json"), JsonSerializer.SerializeToUtf8Bytes(dto));
    }
}
