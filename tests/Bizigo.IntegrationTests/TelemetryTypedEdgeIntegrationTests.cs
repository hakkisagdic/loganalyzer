using System.Text;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Metrics.V1;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only sender oracle across two resources/scopes, all metric
/// types, nonfinite doubles and exact integer/optional-field boundaries.</summary>
[Collection(DevStackCollection.Name)]
public sealed class TelemetryTypedEdgeIntegrationTests(DevStackFixture stack)
{
    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_resources_scopes_and_nonfinite_optional_points_match_sender(bool json)
    {
        var token = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, token);
        await f.SourceAsync("typed-one", "A"); await f.SourceAsync("typed-two", "A");
        var request = TelemetryDbFixture.Metrics("typed-one", f.Now);
        var scope = request.ResourceMetrics[0].ScopeMetrics[0];
        foreach (var metric in scope.Metrics)
        {
            if (metric.Gauge is not null) metric.Gauge.DataPoints.Add(metric.Gauge.DataPoints[0].Clone());
            if (metric.Sum is not null) metric.Sum.DataPoints.Add(metric.Sum.DataPoints[0].Clone());
            if (metric.Histogram is not null)
            {
                var absent = metric.Histogram.DataPoints[0].Clone(); absent.ClearMin(); absent.ClearMax(); absent.ClearSum(); metric.Histogram.DataPoints.Add(absent);
            }
            if (metric.ExponentialHistogram is not null) metric.ExponentialHistogram.DataPoints.Add(metric.ExponentialHistogram.DataPoints[0].Clone());
            if (metric.Summary is not null) metric.Summary.DataPoints.Add(metric.Summary.DataPoints[0].Clone());
        }
        var gauge = scope.Metrics[0].Gauge.DataPoints;
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        { var point = gauge[0].Clone(); point.AsDouble = value; gauge.Add(point); }
        foreach (var value in new[] { long.MinValue, long.MaxValue, 9007199254740991L })
        { var point = gauge[0].Clone(); point.AsInt = value; gauge.Add(point); }
        var scopeTwo = scope.Clone(); scopeTwo.Scope.Name = "second-sdk";
        request.ResourceMetrics[0].ScopeMetrics.Add(scopeTwo);
        var resourceTwo = request.ResourceMetrics[0].Clone();
        resourceTwo.Resource.Attributes.Single(a => a.Key == "bizigo.source_key").Value.StringValue = "typed-two";
        request.ResourceMetrics.Add(resourceTwo);
        using var ingest = f.Open(); await ingest.RecoverAsync(token);
        await f.EmitAsync(ingest, request, TelemetrySignal.Metrics, json);
        var page = await f.Query.SearchTelemetryAsync(f.Window(TelemetrySignal.Metrics) with { Limit = 1000 }, AccessScope.ForGroups("typed-edge", ["A"]), token);
        Assert.Equal(TelemetryResultStatus.Data, page.Status); Assert.False(page.Partial); Assert.Equal(64, page.Records.Count);
        foreach (var row in page.Records)
        {
            var indexes = row.Owner.LeafKey.Split('/').Skip(1).Select(int.Parse).ToArray();
            var resource = request.ResourceMetrics[indexes[0]];
            var senderScope = resource.ScopeMetrics[indexes[1]];
            var expected = senderScope.Metrics[indexes[2]].Clone();
            var index = indexes[3];
            if (expected.Gauge is not null) Keep(expected.Gauge.DataPoints, index);
            if (expected.Sum is not null) Keep(expected.Sum.DataPoints, index);
            if (expected.Histogram is not null) Keep(expected.Histogram.DataPoints, index);
            if (expected.ExponentialHistogram is not null) Keep(expected.ExponentialHistogram.DataPoints, index);
            if (expected.Summary is not null) Keep(expected.Summary.DataPoints, index);
            Assert.Equal(expected, OtlpJsonCodec.Parse<Metric>(Encoding.UTF8.GetBytes(row.Metric!.Value.GetRawText()), Metric.Descriptor));
            Assert.Equal(resource.Resource, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Resource.V1.Resource>(Encoding.UTF8.GetBytes(row.Resource.GetRawText()), OpenTelemetry.Proto.Resource.V1.Resource.Descriptor));
            Assert.Equal(senderScope.Scope, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Common.V1.InstrumentationScope>(Encoding.UTF8.GetBytes(row.Scope.GetRawText()), OpenTelemetry.Proto.Common.V1.InstrumentationScope.Descriptor));
        }
    }

    private static void Keep<T>(RepeatedField<T> values, int index)
    {
        for (var i = values.Count - 1; i >= 0; i--) if (i != index) values.RemoveAt(i);
    }
}
