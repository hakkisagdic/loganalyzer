using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Query;

namespace Bizigo.UnitTests;

/// <summary>
/// Kanıt testlerinin sahte <see cref="IScopedQuery"/>'si.
///
/// <para>
/// <c>AlertingTestDoubles.FakeScopedQuery</c>'den ayrı duruyor: o alarm
/// motorunun ihtiyaçlarına göre şekillenmiş ve her şeye boş dönüyor. Burada
/// sınanan şey sağlayıcıların <b>kapı üzerinden</b> ne sorduğu, dolayısıyla
/// çağrıların kendisi kaydediliyor.
/// </para>
/// </summary>
internal sealed class RecordingScopedQuery : IScopedQuery
{
    public List<EventQuery> EventQueries { get; } = [];

    public List<ChangeQuery> ChangeQueries { get; } = [];

    /// <summary>Kapsam <b>içindeki</b> olaylar — sağlayıcının görmesi gerekenler.</summary>
    public List<LogEvent> Events { get; } = [];

    public bool EventsHaveMore { get; set; }

    public long OutOfScopeEvents { get; set; }

    public long OutOfScopeChanges { get; set; }

    /// <summary>Pencere sorgusunun döndürecekleri.</summary>
    public List<ChangeEvent> Changes { get; } = [];

    /// <summary>
    /// "Hiç beslenmiş mi" yoklamasının döndürecekleri — pencere sorgusundan
    /// <b>ayrı</b> tutuluyor, çünkü ayırt edilen tam olarak bu iki sorunun
    /// farklı cevap verebilmesi.
    /// </summary>
    public List<ChangeEvent> EverChanges { get; } = [];

    public Task<IReadOnlyList<ChangeEvent>> SearchChangesAsync(
        ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default)
    {
        ChangeQueries.Add(query);

        // İkinci ve sonraki çağrılar "hiç beslenmiş mi" yoklaması.
        var source = ChangeQueries.Count == 1 ? Changes : EverChanges;

        return Task.FromResult<IReadOnlyList<ChangeEvent>>([.. source.Take(query.Limit)]);
    }

    public Task<long> CountOutOfScopeChangesAsync(
        ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(OutOfScopeChanges);

    public Task<EventPage> SearchEventsAsync(
        EventQuery query, AccessScope scope, CancellationToken cancellationToken = default)
    {
        EventQueries.Add(query);
        return Task.FromResult(new EventPage([.. Events], null, EventsHaveMore));
    }

    public Task<long> CountOutOfScopeEventsAsync(
        EventQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(OutOfScopeEvents);

    public Task<LogEvent?> GetEventAsync(Guid eventId, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<LogEvent?>(null);

    public Task<IReadOnlyList<EventFieldView>> GetEventViewAsync(
        Guid eventId, EventViewKind view, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<EventFieldView>>([]);

    public Task<long> CountEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult((long)Events.Count);

    public Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task WriteChangeAsync(ChangeEvent change, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceSummary>>([]);

    public Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceActivityRow>>([]);

    public Task<IReadOnlyList<HistogramBucket>> GetEventHistogramAsync(
        EventHistogramQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HistogramBucket>>([]);
}

/// <summary>
/// Testin kendi uydurduğu sağlayıcı. <b>Toplayıcı bunu tanımıyor</b> — "yeni bir
/// sağlayıcı eklendiğinde motor değişmiyor" kriteri tam olarak bununla
/// sınanıyor.
/// </summary>
internal sealed class StubProvider(
    string id,
    EvidenceKind kind,
    EvidenceStatus status = EvidenceStatus.Gathered,
    bool available = true) : IEvidenceProvider
{
    public string Id => id;

    public EvidenceKind Kind => kind;

    public bool IsAvailable => available;

    public int Calls { get; private set; }

    /// <summary>
    /// Token'ı <b>alıyor</b>: gerçek sağlayıcılar da onu aşağı geçirmek
    /// zorunda. Token'ı yok sayan bir sahte, toplayıcının uygulayamayacağı bir
    /// şeyi sınıyor olurdu — ve testi 30 saniye bekletirdi.
    /// </summary>
    public Func<CancellationToken, Task>? Before { get; init; }

    public async Task<EvidenceSlice> GatherAsync(
        RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken)
    {
        Calls++;

        if (Before is not null)
        {
            await Before(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new EvidenceSlice
        {
            ProviderId = id,
            Kind = kind,
            Status = status,
            Detail = status == EvidenceStatus.Gathered ? string.Empty : "test",
            Items = status == EvidenceStatus.Gathered
                ?
                [
                    new EvidenceItem(
                        $"{id}-1", id, kind, window.From, 1.0, "test kanıtı",
                        new Dictionary<string, string>(StringComparer.Ordinal))
                ]
                : [],
        };
    }
}

/// <summary>Her koşuda patlayan sağlayıcı — paketin ayakta kalması sınanıyor.</summary>
internal sealed class ThrowingProvider(string id, EvidenceKind kind) : IEvidenceProvider
{
    public string Id => id;

    public EvidenceKind Kind => kind;

    public bool IsAvailable => true;

    public Task<EvidenceSlice> GatherAsync(
        RcaWindow window, AccessScope scope, GatherBudget budget, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("ClickHouse'a ulaşılamadı.");
}
