using System.Diagnostics;
using System.Net;
using Bizigo.Contracts;
using ClickHouse.Driver;

namespace Bizigo.Storage.ClickHouse;

public sealed record WriteResult(long RowsWritten, TimeSpan Duration);

/// <summary>
/// <c>events</c> ve <c>change_events</c> tablolarına toplu yazım.
/// RowBinary bulk insert kullanılıyor — satır satır INSERT bu hacimde çalışmaz.
///
/// Yazım yolunda kapsam filtresi <b>yoktur</b>: <c>owner_group</c> olayın kendisinde
/// taşınıyor ve kaynaktan (envanterden) atanıyor (F1 §8). Filtre okuma tarafının işi.
/// </summary>
public sealed class EventWriter(ClickHouseContext context)
{
    private static readonly string[] EventColumns =
    [
        "ts", "ingested_at", "time_source", "event_id", "owner_group", "source_id", "host", "vendor", "product",
        "parser_id", "parser_version", "parse_status", "parse_generation", "encoding_detected",
        "template_id", "signature_hash", "severity_num", "ocsf_class_uid", "ocsf_activity_id",
        "src_ip", "dst_ip", "src_port", "dst_port", "proto", "action", "outcome", "user_name",
        "attrs", "body", "raw_ref",
    ];

    private static readonly string[] ChangeColumns =
    [
        "ts", "change_id", "owner_group", "target_kind", "target_id", "change_kind",
        "actor", "summary", "details", "source", "external_ref",
    ];

    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    public Task<WriteResult> WriteEventsAsync(
        IReadOnlyCollection<LogEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return WriteAsync(_context.Options.EventsTable, EventColumns,
            events.Select(ToRow), events.Count, cancellationToken);
    }

    public Task<WriteResult> WriteChangeEventsAsync(
        IReadOnlyCollection<ChangeEvent> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return WriteAsync(_context.Options.ChangeEventsTable, ChangeColumns,
            changes.Select(ToRow), changes.Count, cancellationToken);
    }

    /// <summary>Replay (T11) gölge tabloya yazar; bu yüzden hedef tablo parametrik.</summary>
    public Task<WriteResult> WriteEventsToAsync(
        string destinationTable,
        IReadOnlyCollection<LogEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTable);
        ArgumentNullException.ThrowIfNull(events);
        return WriteAsync(destinationTable, EventColumns, events.Select(ToRow), events.Count, cancellationToken);
    }

    private async Task<WriteResult> WriteAsync(
        string table,
        string[] columns,
        IEnumerable<object[]> rows,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        if (expectedCount == 0)
        {
            return new WriteResult(0, TimeSpan.Zero);
        }

        var started = Stopwatch.StartNew();

        var written = await _context.Client.InsertBinaryAsync(
            table,
            columns,
            rows,
            new InsertOptions
            {
                BatchSize = _context.Options.BulkBatchSize,
                MaxDegreeOfParallelism = _context.Options.BulkParallelism,
            },
            cancellationToken);

        return new WriteResult(written, started.Elapsed);
    }

    private static object[] ToRow(LogEvent e) =>
    [
        e.Timestamp.UtcDateTime,
        e.IngestedAt.UtcDateTime,
        e.TimeSource,
        e.EventId,
        e.OwnerGroup,
        e.SourceId,
        e.Host,
        e.Vendor,
        e.Product,
        e.ParserId,
        e.ParserVersion,
        (byte)e.ParseStatus,
        e.ParseGeneration,
        e.EncodingDetected,
        e.TemplateId,
        e.SignatureHash,
        e.SeverityNum,
        e.OcsfClassUid,
        e.OcsfActivityId,
        ToV6(e.SrcIp),
        ToV6(e.DstIp),
        e.SrcPort,
        e.DstPort,
        e.Proto,
        e.Action,
        e.Outcome,
        e.UserName,
        e.Attrs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        e.Body,
        e.RawRef,
    ];

    private static object[] ToRow(ChangeEvent c) =>
    [
        c.Timestamp.UtcDateTime,
        c.ChangeId,
        c.OwnerGroup,
        (byte)c.TargetKind,
        c.TargetId,
        c.ChangeKind,
        c.Actor,
        c.Summary,
        c.Details.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
        c.Source,
        c.ExternalRef,
    ];

    /// <summary>
    /// IPv4 adresleri <c>::ffff:a.b.c.d</c> olarak tek bir <c>IPv6</c> kolonunda
    /// tutuluyor — iki ayrı kolon her sorguya bir OR koşulu eklerdi.
    /// </summary>
    public static IPAddress ToV6(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? address.MapToIPv6()
            : address;
    }
}
