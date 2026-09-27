using System.Text.Json;
using System.Text;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Storage.ClickHouse;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Bizigo.UnitTests;

public sealed class TelemetryStorageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Complete_typed_leaf_survives_materialization_and_record_serialization(bool json, bool traces)
    {
        var wire = traces ? OtlpTelemetryDecoderTests.Wire(OtlpTelemetryDecoderTests.Traces(), json)
            : OtlpTelemetryDecoderTests.Wire(OtlpTelemetryDecoderTests.Metrics(), json);
        var signal = traces ? TelemetrySignal.Traces : TelemetrySignal.Metrics;
        var contentType = json ? "application/json" : "application/x-protobuf";
        var decoded = new OtlpTelemetryDecoder().Decode(signal, wire, contentType);
        var envelope = new RawSignalEnvelope(2, Guid.NewGuid(), signal, contentType, DateTimeOffset.UtcNow,
            RawSignalEnvelope.Hash(wire), wire, 1, decoded.Accepted.Select(l => l.Key).ToArray(), 0)
        { OwnerBindings = decoded.Accepted.Select(l => new TelemetryOwnerBinding(l.Key, "source", "A", 1, TelemetryMaterializer.Time(l), "known")).ToArray() };
        envelope = envelope with { OwnerBindingsSha256 = envelope.ComputeOwnerBindingsHash() };
        envelope.Validate();
        for (var i = 0; i < decoded.Accepted.Count; i++)
        {
            var leaf = decoded.Accepted[i];
            var materialized = TelemetryMaterializer.Materialize(envelope, leaf, envelope.OwnerBindings![i]);
            var record = JsonSerializer.Deserialize<TelemetryRecord>(JsonSerializer.Serialize(materialized, RawSignalCodec.Json), RawSignalCodec.Json)!;
            TelemetryWriter.Validate(record);
            Assert.Equal(TelemetryMaterializer.Time(leaf), record.TimeUnixNano);
            Assert.Equal(envelope.EnvelopeId.ToString("N") + "/" + leaf.Key, record.LogicalId);
            if (traces) Assert.Equal(leaf.Span, OtlpJsonCodec.Parse<Span>(Encoding.UTF8.GetBytes(record.Span!.Value.GetRawText()), Span.Descriptor));
            else Assert.Equal(leaf.Metric, OtlpJsonCodec.Parse<Metric>(Encoding.UTF8.GetBytes(record.Metric!.Value.GetRawText()), Metric.Descriptor));
            Assert.Equal(leaf.Resource, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Resource.V1.Resource>(Encoding.UTF8.GetBytes(record.Resource.GetRawText()), OpenTelemetry.Proto.Resource.V1.Resource.Descriptor));
            Assert.Equal(leaf.Scope, OtlpJsonCodec.Parse<OpenTelemetry.Proto.Common.V1.InstrumentationScope>(Encoding.UTF8.GetBytes(record.Scope.GetRawText()), OpenTelemetry.Proto.Common.V1.InstrumentationScope.Descriptor));
            Assert.Equal(leaf.ResourceSchemaUrl, record.ResourceSchemaUrl);
            Assert.Equal(leaf.ScopeSchemaUrl, record.ScopeSchemaUrl);
            Assert.Throws<InvalidDataException>(() => TelemetryWriter.Validate(record with { Version = 99 }));
        }
        Assert.Throws<InvalidDataException>(() => (envelope with { OwnerBindings = null }).Validate());
        var altered = envelope.OwnerBindings!.ToArray(); altered[0] = altered[0] with { OwnerGroup = "B" };
        Assert.Throws<InvalidDataException>(() => (envelope with { OwnerBindings = altered }).Validate());
    }
}
