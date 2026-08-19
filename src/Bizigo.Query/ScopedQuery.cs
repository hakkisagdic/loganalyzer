using System.Diagnostics;
using System.Globalization;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Query;

/// <summary>
/// <see cref="IScopedQuery"/> uygulaması. İki iş yapıyor:
/// kapsamı SQL'e çeviriyor ve her çağrıyı denetim günlüğüne yazıyor.
/// </summary>
public sealed class ScopedQuery(
    EventReader events,
    ChangeEventReader changes,
    EventWriter writer,
    ControlPlaneDbContext controlPlane,
    IAuditSink audit) : IScopedQuery
{
    public async Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var watch = Stopwatch.StartNew();

        // Filtre bellekte: envanter onlarca satır, SQL'e kapsam çevirmek burada
        // kazanç değil karmaşıklık olurdu. Önemli olan filtrenin BU sınıfta
        // olması — uç katmanında değil.
        var all = await controlPlane.Sources.AsNoTracking().ToListAsync(cancellationToken);

        var visible = all
            .Where(s => scope.Allows(s.OwnerGroup))
            .OrderBy(s => s.SourceId, StringComparer.Ordinal)
            .Select(s => new SourceSummary(
                s.SourceId, s.OwnerGroup, s.PeerAddress, s.Hostname,
                s.Vendor, s.Product, s.ParserId, s.Encoding, s.SourceClass,
                s.Enabled, !string.IsNullOrWhiteSpace(s.ParserId), s.CreatedAt))
            .ToArray();

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "sources.search", "sources",
            Describe(scope, ScopePredicate.From(scope)), string.Empty,
            visible.Length, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return visible;
    }

    private readonly EventReader _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ChangeEventReader _changes = changes ?? throw new ArgumentNullException(nameof(changes));
    private readonly EventWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly IAuditSink _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task WriteChangeAsync(
        ChangeEvent change,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.Allows(change.OwnerGroup))
        {
            await _audit.RecordAsync(new AuditRecord(
                scope.Subject, "changes.write", "change_events",
                Describe(scope, ScopePredicate.From(scope)), change.OwnerGroup,
                0, 0, false), cancellationToken);

            throw new UnauthorizedAccessException(
                $"'{change.OwnerGroup}' grubuna yazma yetkisi yok.");
        }

        var watch = Stopwatch.StartNew();
        await _writer.WriteChangeEventsAsync([change], cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "changes.write", "change_events",
            Describe(scope, ScopePredicate.From(scope)), change.ChangeKind,
            1, (int)watch.ElapsedMilliseconds, true), cancellationToken);
    }

    public async Task<EventPage> SearchEventsAsync(
        EventQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        var predicate = ScopePredicate.From(scope, query.OwnerGroups);
        var watch = Stopwatch.StartNew();

        var page = await _events.SearchAsync(query, predicate, cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "events.search", "events",
            Describe(scope, predicate), Describe(query),
            page.Events.Count, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return page;
    }

    public async Task<LogEvent?> GetEventAsync(
        Guid eventId,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var predicate = ScopePredicate.From(scope);
        var watch = Stopwatch.StartNew();

        var result = await _events.GetByIdAsync(eventId, predicate, cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "events.get", "events",
            Describe(scope, predicate), eventId.ToString(),
            result is null ? 0 : 1, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return result;
    }

    public Task<long> CountEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        return _events.CountAsync(query, ScopePredicate.From(scope, query.OwnerGroups), cancellationToken);
    }

    public Task<long> CountOutOfScopeEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        return _events.CountOutOfScopeAsync(query, ScopePredicate.From(scope), cancellationToken);
    }

    public async Task<IReadOnlyList<ChangeEvent>> SearchChangesAsync(
        ChangeQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        var predicate = ScopePredicate.From(scope, query.OwnerGroups);
        var watch = Stopwatch.StartNew();

        var result = await _changes.SearchAsync(query, predicate, cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "changes.search", "change_events",
            Describe(scope, predicate), $"{query.From:O}..{query.To:O}",
            result.Count, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return result;
    }

    public async Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(scope);

        var predicate = ScopePredicate.From(scope, window.OwnerGroups);
        var watch = Stopwatch.StartNew();

        var rows = await _events.GetSourceActivityAsync(window, predicate, cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "sources.activity", "events",
            Describe(scope, predicate), $"{window.From:O}..{window.To:O}",
            rows.Count, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return rows;
    }

    public async Task<IReadOnlyList<HistogramBucket>> GetEventHistogramAsync(
        EventHistogramQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        var predicate = ScopePredicate.From(scope, query.OwnerGroups);
        var watch = Stopwatch.StartNew();

        var rows = await _events.GetHistogramAsync(query, predicate, cancellationToken);

        await _audit.RecordAsync(new AuditRecord(
            scope.Subject, "events.histogram", "events",
            Describe(scope, predicate), $"{query.From:O}..{query.To:O} bucket={query.BucketSeconds}",
            rows.Count, (int)watch.ElapsedMilliseconds, true), cancellationToken);

        return rows;
    }

    public Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsUnrestricted)
        {
            return Task.FromResult(true);
        }

        if (scope.IsEmpty)
        {
            return Task.FromResult(false);
        }

        // Nesne anahtarı: raw/{owner_group}/{yyyy}/{MM}/{dd}/{hh}/{source_class}/{ulid}.ndjson.zst
        // Grubu yola koymanın sebebi tam olarak bu kontrol (F1 §7.1).
        var group = ExtractOwnerGroup(objectKey);
        return Task.FromResult(group is not null && scope.OwnerGroups.Contains(group));
    }

    internal static string? ExtractOwnerGroup(string objectKey)
    {
        var parts = objectKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[0], "raw", StringComparison.Ordinal)
            ? parts[1]
            : null;
    }

    private static string Describe(AccessScope scope, ScopePredicate predicate)
    {
        if (predicate.DeniesEverything)
        {
            return "denied";
        }

        return predicate.IsUnrestricted
            ? $"unrestricted({scope.Subject})"
            : string.Join(",", predicate.Groups);
    }

    private static string Describe(EventQuery query)
    {
        var filters = query.Filters.Count == 0
            ? "-"
            : string.Join(";", query.Filters.Select(f => $"{f.Field}{f.Operator}{string.Join('|', f.Values)}"));

        return string.Create(CultureInfo.InvariantCulture,
            $"{query.From:O}..{query.To:O} ft={query.FullText ?? "-"} filters={filters} limit={query.Limit}");
    }
}

public sealed record AuditRecord(
    string Subject,
    string Action,
    string Resource,
    string Scope,
    string Details,
    long RowCount,
    int DurationMs,
    bool Succeeded);

public interface IAuditSink
{
    Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Denetim kaydını kontrol düzlemine yazar.</summary>
public sealed class ControlPlaneAuditSink(ControlPlaneDbContext db) : IAuditSink
{
    private readonly ControlPlaneDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        _db.AuditLog.Add(new AuditLogEntity
        {
            Subject = Truncate(record.Subject, 256),
            Action = Truncate(record.Action, 64),
            Resource = Truncate(record.Resource, 64),
            Scope = Truncate(record.Scope, 1024),
            Details = Truncate(record.Details, 4096),
            RowCount = record.RowCount,
            DurationMs = record.DurationMs,
            Succeeded = record.Succeeded,
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Testler ve CLI için; denetim kaydı tutmaz.</summary>
public sealed class NullAuditSink : IAuditSink
{
    public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
