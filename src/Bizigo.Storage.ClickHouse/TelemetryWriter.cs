using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using ClickHouse.Driver;

namespace Bizigo.Storage.ClickHouse;

/// <summary>Writes complete, versioned typed leaves. No acknowledgement/checkpoint belongs here.</summary>
public sealed class TelemetryWriter(ClickHouseContext context, ITelemetryBindingRegistry bindings) : ITelemetrySink
{
    private static readonly string[] Columns =
    [
        "owner_group", "metric_name", "resource_id", "ts", "logical_id", "envelope_id", "service_name",
        "kind", "unit", "temporality", "monotonic", "trace_id", "span_id", "status", "summary_key",
        "record_version", "record", "record_sha256", "expires_nano", "ttl_at", "ttl_supported",
    ];

    public async Task WriteAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        if (context.Options.TelemetryRetentionDays is < 1 or > 36500)
            throw new InvalidOperationException("Telemetry retention must be between 1 and 36500 days.");
        // Arbitration precedes every insert, including a second process replaying
        // restored archive metadata. Conflicting bindings never reach either table.
        foreach (var envelope in records.GroupBy(r => r.EnvelopeId))
        {
            if (envelope.Select(r => r.OwnerBindingSha256).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("Conflicting ownership decisions in one export.");
            await bindings.ClaimAsync(envelope.Key, envelope.First().OwnerBindingSha256, cancellationToken);
        }
        foreach (var signal in records.GroupBy(r => r.Signal))
        {
            var rows = signal.Select(ToRow).ToArray();
            var columns = Columns.Select(c => c == "ts" && signal.Key == TelemetrySignal.Traces ? "start_time" : c).ToArray();
            var written = await context.Client.InsertBinaryAsync(Table(signal.Key), columns, rows,
                new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, cancellationToken);
            if (written != rows.Length) throw new IOException("Incomplete telemetry database insert.");
        }
        if (records.Count == 0) return;
        var feeds = records.Select(r => (r.Owner.OwnerGroup, r.Owner.SourceId, r.Signal)).Distinct()
            .Select(f => new object[] { f.OwnerGroup, f.SourceId, (byte)f.Signal }).ToArray();
        var fed = await context.Client.InsertBinaryAsync("telemetry_feed_history", ["owner_group", "resource_id", "signal"],
            feeds, new InsertOptions { BatchSize = context.Options.BulkBatchSize, MaxDegreeOfParallelism = 1 }, cancellationToken);
        if (fed != feeds.Length) throw new IOException("Incomplete telemetry feed-history insert.");
    }

    internal static string Table(TelemetrySignal signal) => signal switch
    {
        TelemetrySignal.Metrics => "metric_points",
        TelemetrySignal.Traces => "trace_spans",
        _ => throw new ArgumentOutOfRangeException(nameof(signal)),
    };

    private object[] ToRow(TelemetryRecord record)
    {
        Validate(record);
        var json = JsonSerializer.Serialize(record, RawSignalCodec.Json);
        var expires = (decimal)record.TimeUnixNano + record.RetentionDays * 86400000000000m;
        // Physical cleanup may lag logical expiry, never lead it by rounding.
        var seconds = decimal.Ceiling(expires / 1000000000m);
        var ttlSupported = seconds <= uint.MaxValue;
        var ttl = DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(seconds, uint.MaxValue)).UtcDateTime;
        var summary = record.Signal == TelemetrySignal.Metrics ? record.SeriesKey
            : RawSignalEnvelope.Hash(JsonSerializer.SerializeToUtf8Bytes(new { record.ServiceName, record.Kind, record.Status }));
        return [record.Owner.OwnerGroup, record.Name, record.Owner.SourceId, record.TimeUnixNano, record.LogicalId,
            record.EnvelopeId, record.ServiceName, record.Kind, record.Unit, record.Temporality, (byte)(record.Monotonic ? 1 : 0),
            record.TraceId, record.SpanId, record.Status, summary, (ushort)record.Version, json,
            RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(json)), expires, ttl, (byte)(ttlSupported ? 1 : 0)];
    }

    public static void Validate(TelemetryRecord record)
    {
        if (record.Version != 1 || record.RetentionDays is < 1 or > 36500 || !Enum.IsDefined(record.Signal) || record.EnvelopeId == Guid.Empty || record.Owner is null
            || record.LogicalId != record.EnvelopeId.ToString("N") + "/" + record.Owner.LeafKey
            || record.TimeUnixNano != record.Owner.EventTimeUnixNano
            || string.IsNullOrWhiteSpace(record.Owner.OwnerGroup) || string.IsNullOrWhiteSpace(record.Owner.SourceId)
            || record.OwnerBindingSha256 is not { Length: 64 } || record.PayloadSha256 is not { Length: 64 }
            || record.Name is null || record.Kind is null || record.Unit is null || record.ServiceName is null
            || record.SeriesKey is not { Length: 64 } || record.Resource.ValueKind != JsonValueKind.Object || record.Scope.ValueKind != JsonValueKind.Object
            || (record.Signal == TelemetrySignal.Metrics && (record.Metric?.ValueKind != JsonValueKind.Object || record.Span is not null))
            || (record.Signal == TelemetrySignal.Traces && (record.Span?.ValueKind != JsonValueKind.Object || record.Metric is not null)))
            throw new InvalidDataException("Unsupported or corrupt typed telemetry record.");
    }
}
