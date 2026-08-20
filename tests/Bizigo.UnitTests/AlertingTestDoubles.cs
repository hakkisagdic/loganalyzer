using Bizigo.Alerting;
using Bizigo.Alerting.Notifications;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Bizigo.UnitTests;

/// <summary>Sahte olay: yalnızca kapsam ve filtre testleri için gereken alanlar.</summary>
internal sealed record FakeEvent(string OwnerGroup, string SourceId, DateTimeOffset Ts, string Action);

/// <summary>
/// <see cref="IScopedQuery"/> sahtesi.
///
/// <para>
/// <b>Kapsamı gerçekten uyguluyor</b> — bu kasıtlı. "Bir ekibin kuralı başka
/// ekibin olaylarını saymıyor" bekçisinin bir şey kanıtlaması, sahtenin kapsamı
/// yok sayması hâlinde mümkün olmazdı: test her durumda geçerdi. Sahte burada
/// deponun yerine geçiyor, çağrı kaydedicinin değil.
/// </para>
/// </summary>
internal sealed class FakeScopedQuery : IScopedQuery, IAlertQuerySource
{
    private int _countCalls;
    private int _activityCalls;
    private int _inventoryCalls;
    private int _histogramCalls;

    public List<FakeEvent> Events { get; } = [];

    public List<SourceSummary> Sources { get; } = [];

    /// <summary>Sayım çağrısında beklemek için: zaman aşımı bekçisi bunu kullanıyor.</summary>
    public Func<CancellationToken, Task>? BeforeCount { get; set; }

    public int CountCalls => Volatile.Read(ref _countCalls);
    public int ActivityCalls => Volatile.Read(ref _activityCalls);
    public int InventoryCalls => Volatile.Read(ref _inventoryCalls);
    public int HistogramCalls => Volatile.Read(ref _histogramCalls);

    public int TotalCalls => CountCalls + ActivityCalls + InventoryCalls + HistogramCalls;

    public AlertQueryLease Lease() => new(this, null);

    public async Task<long> CountEventsAsync(
        EventQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _countCalls);

        if (BeforeCount is { } hook)
        {
            await hook(cancellationToken);
        }

        return Events.Count(e =>
            scope.Allows(e.OwnerGroup)
            && e.Ts >= query.From
            && e.Ts < query.To
            && MatchesFilters(e, query));
    }

    private static bool MatchesFilters(FakeEvent candidate, EventQuery query)
    {
        if (query.SourceIds.Count > 0 && !query.SourceIds.Contains(candidate.SourceId, StringComparer.Ordinal))
        {
            return false;
        }

        return query.Filters.All(f => f.Field switch
        {
            "action" => f.Values.Contains(candidate.Action, StringComparer.Ordinal),
            "source_id" => f.Values.Contains(candidate.SourceId, StringComparer.Ordinal),
            _ => true,
        });
    }

    public Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _activityCalls);

        IReadOnlyList<SourceActivityRow> rows =
        [
            .. Events
                .Where(e => scope.Allows(e.OwnerGroup) && e.Ts >= window.From && e.Ts < window.To)
                .GroupBy(e => (e.OwnerGroup, e.SourceId))
                .Select(g => new SourceActivityRow(
                    g.Key.OwnerGroup,
                    g.Key.SourceId,
                    g.Max(e => e.Ts),
                    g.Max(e => e.Ts),
                    g.Count()))
        ];

        return Task.FromResult(rows);
    }

    /// <summary>
    /// Histogram sahtesi: gerçek sorgu gibi <b>yalnızca dolu kovaları</b>
    /// döndürüyor.
    ///
    /// <para>
    /// Boş kovaları da döndürseydi <c>AlertPreview.Densify</c>'ın varlık sebebi
    /// testte hiç görünmezdi — ve o kod tam da SQL'in boş kova döndürmediği için
    /// var.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<HistogramBucket>> GetEventHistogramAsync(
        EventHistogramQuery query,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _histogramCalls);

        var width = TimeSpan.FromSeconds(Math.Max(query.BucketSeconds, 1));

        IReadOnlyList<HistogramBucket> rows =
        [
            .. Events
                .Where(e => scope.Allows(e.OwnerGroup) && e.Ts >= query.From && e.Ts < query.To)
                .Where(e => query.SourceIds.Count == 0
                    || query.SourceIds.Contains(e.SourceId, StringComparer.Ordinal))
                .GroupBy(e => (
                    Start: Align(e.Ts, width),
                    Source: query.GroupBySource ? e.SourceId : string.Empty))
                .Select(g => new HistogramBucket(g.Key.Start, g.Key.Source, g.Count()))
                .OrderBy(b => b.Start)
        ];

        return Task.FromResult(rows);
    }

    /// <summary>ClickHouse'un <c>toStartOfInterval</c>'ı gibi epoch'a hizalıyor.</summary>
    private static DateTimeOffset Align(DateTimeOffset value, TimeSpan width)
    {
        var seconds = value.ToUnixTimeSeconds();
        var size = (long)width.TotalSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(seconds - (seconds % size));
    }

    public Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _inventoryCalls);

        IReadOnlyList<SourceSummary> visible = [.. Sources.Where(s => scope.Allows(s.OwnerGroup))];
        return Task.FromResult(visible);
    }

    public Task<LogEvent?> GetEventAsync(Guid eventId, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<LogEvent?>(null);

    public Task<long> CountOutOfScopeEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    public Task<IReadOnlyList<ChangeEvent>> SearchChangesAsync(ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChangeEvent>>([]);

    public Task<long> CountOutOfScopeChangesAsync(ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(0L);

    public Task<EventPage> SearchEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EventPage([], null, false));

    public Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Alarm motoru olay görünümlerini hiç okumuyor; boş liste, kapsam dışı
    /// olayın gerçek cevabıyla aynı (bilgi gizleme). Buradaki iddia yalnızca
    /// "alarm tarafı bu yüzeye dokunmuyor" — görünümlerin gerçekten
    /// <c>0003_ocsf_otel_views.sql</c>'den geldiği <c>ScopeNegativeTests</c>'te,
    /// canlı şemaya karşı sınanıyor.
    /// </summary>
    public Task<IReadOnlyList<EventFieldView>> GetEventViewAsync(
        Guid eventId,
        EventViewKind view,
        AccessScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EventFieldView>>([]);

    public Task WriteChangeAsync(ChangeEvent change, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public static SourceSummary Source(string sourceId, string ownerGroup, DateTimeOffset createdAt) =>
        new(sourceId, ownerGroup, null, null, "test", "test", "p", "utf-8", "default", true, true, createdAt);
}

/// <summary>
/// Bellek içi kontrol düzlemi.
///
/// <para>
/// <b>Neden InMemory ve neden SQLite değil:</b> SQLite ilişkisel olduğu için
/// önce o denendi, ama EF'in SQLite sağlayıcısı <c>DateTimeOffset</c>
/// karşılaştırmasını SQL'e çeviremiyor — ve zamanlayıcının tek sorgusu
/// ("vadesi gelmiş kurallar") tam olarak o. Geçirmenin yolu varlıkların zaman
/// tipini değiştirmekti, yani üretimi test sağlayıcısına uydurmak. Buradaki
/// testlerin iddiası zaten SQL değil <b>karar</b>: bakım penceresinde
/// tetiklenme yok, tekrar aralığı dolmadan ikinci tetiklenme yok, yirmi kural
/// iki sorgu. SQL doğruluğu entegrasyon testinin işi.
/// </para>
///
/// <para>
/// Her örnek kendi veritabanı adını alıyor: paylaşılan ad, paralel koşan
/// testlerin birbirinin satırlarını görmesi demekti.
/// </para>
/// </summary>
internal sealed class InMemoryControlPlaneFactory : IDbContextFactory<ControlPlaneDbContext>, IDisposable
{
    private readonly DbContextOptions<ControlPlaneDbContext> _options;

    public InMemoryControlPlaneFactory()
    {
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    public ControlPlaneDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        using var db = CreateDbContext();
        db.Database.EnsureDeleted();
    }
}

/// <summary>Gönderilen mesajları toplayan kanal sahtesi.</summary>
internal sealed class RecordingChannel(NotificationChannelType type) : INotificationChannel
{
    public NotificationChannelType Type { get; } = type;

    public List<NotificationMessage> Sent { get; } = [];

    public List<string> SeenSecrets { get; } = [];

    /// <summary>Sıradaki gönderimin sonucu. Boşsa teslim edildi sayılıyor.</summary>
    public Queue<ChannelResult> Results { get; } = new();

    public Task<ChannelResult> SendAsync(
        NotificationMessage message,
        ResolvedChannel channel,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        SeenSecrets.Add(channel.Secret);

        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ChannelResult.Delivered());
    }
}
