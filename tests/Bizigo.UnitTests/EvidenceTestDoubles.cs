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
internal class RecordingScopedQuery : IScopedQuery
{
    public Func<TelemetryQuery, AccessScope, CancellationToken, Task<TelemetryPage>>? TelemetrySearch { get; set; }
    public Task<TelemetryPage> SearchTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        TelemetrySearch?.Invoke(query, scope, cancellationToken) ?? throw new NotSupportedException();
    public Task<TelemetryPage> GetMetricPointAsync(string logicalId, AccessScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TelemetryPage> GetTraceAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TelemetryCount> CountTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TelemetryCount> CountOutOfScopeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Func<TelemetryInputWindow, AccessScope, CancellationToken, Task<TelemetryCount>>? ExcludedTelemetryCount { get; init; }
    public Task<TelemetryCount> CountExcludedTelemetryInputsAsync(TelemetryInputWindow window, AccessScope scope, CancellationToken cancellationToken = default) =>
        ExcludedTelemetryCount?.Invoke(window, scope, cancellationToken) ?? throw new NotSupportedException();
    public Task<TelemetrySummaryPage> SummarizeTelemetryAsync(TelemetryQuery query, AccessScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Func<TelemetrySignal, string?, AccessScope, CancellationToken, Task<TelemetryCount>>? TelemetryFeed { get; set; }
    public Task<TelemetryCount> GetTelemetryFeedAsync(TelemetrySignal signal, string? resourceId, AccessScope scope, CancellationToken cancellationToken = default) =>
        TelemetryFeed?.Invoke(signal, resourceId, scope, cancellationToken) ?? throw new NotSupportedException();
    public List<TopologySourceNode> TopologySourceNodes { get; } = [];
    public Func<IReadOnlyList<string>, AccessScope, CancellationToken, Task<IReadOnlyList<TopologySourceNode>>>?
        TopologySourceNodesResponse { get; set; }
    public TopologyPathResult? TopologyPath { get; set; }
    public TopologyCommonAncestorResult? TopologyAncestor { get; set; }
    public Func<TopologyPathQuery, AccessScope, CancellationToken, Task<TopologyPathResult>>? TopologyPathResponse { get; set; }
    public Func<TopologyCommonAncestorQuery, AccessScope, CancellationToken, Task<TopologyCommonAncestorResult>>? TopologyAncestorResponse { get; set; }
    public Func<TopologyGroupedAncestorQuery, AccessScope, CancellationToken, Task<TopologyCommonAncestorResult>>?
        TopologyGroupedAncestorResponse { get; set; }
    public Func<string, decimal, AccessScope, CancellationToken, Task<TopologyEdgeDetail?>>? TopologyEdgeResponse { get; set; }
    public Func<string, decimal, decimal, decimal, AccessScope, CancellationToken, Task<TopologyEdgeDetail?>>?
        TopologyEdgeWindowResponse { get; set; }
    public Dictionary<string, TopologyEdgeDetail> TopologyEdges { get; } = new(StringComparer.Ordinal);
    public Task<IReadOnlyList<TopologySourceNode>> ResolveTopologySourceNodesAsync(IReadOnlyList<string> sourceIds, AccessScope scope,
        CancellationToken cancellationToken = default) => TopologySourceNodesResponse?.Invoke(sourceIds, scope, cancellationToken)
            ?? Task.FromResult<IReadOnlyList<TopologySourceNode>>(
                [.. TopologySourceNodes.Where(node => sourceIds.Contains(node.SourceId, StringComparer.Ordinal))]);
    public Task<TopologyPathResult> GetTopologyPathAsync(TopologyPathQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => TopologyPathResponse?.Invoke(query, scope, cancellationToken)
            ?? Task.FromResult(TopologyPath ?? throw new NotSupportedException());
    public Task<TopologyCommonAncestorResult> GetTopologyCommonAncestorAsync(TopologyCommonAncestorQuery query, AccessScope scope,
        CancellationToken cancellationToken = default) => TopologyAncestorResponse?.Invoke(query, scope, cancellationToken)
            ?? Task.FromResult(TopologyAncestor ?? throw new NotSupportedException());
    public Task<TopologyCommonAncestorResult> GetTopologyGroupedCommonAncestorAsync(TopologyGroupedAncestorQuery query,
        AccessScope scope, CancellationToken cancellationToken = default) =>
        TopologyGroupedAncestorResponse?.Invoke(query, scope, cancellationToken)
        ?? TopologyAncestorResponse?.Invoke(new(query.TargetGroups.Select(static group => group[0]).ToArray(),
                query.ReadClockUnixNano, query.FromUnixNano, query.ToUnixNano), scope, cancellationToken)
        ?? Task.FromResult(TopologyAncestor ?? throw new NotSupportedException());
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) => TopologyEdgeResponse?.Invoke(edgeId, readClockUnixNano, scope, cancellationToken)
            ?? Task.FromResult(TopologyEdges.TryGetValue(edgeId, out var detail) ? detail : null);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano, AccessScope scope,
        string? evidenceCursor, int evidencePageSize, CancellationToken cancellationToken = default) =>
        GetTopologyEdgeAsync(edgeId, readClockUnixNano, scope, cancellationToken);
    public Task<TopologyEdgeDetail?> GetTopologyEdgeAsync(string edgeId, decimal readClockUnixNano,
        decimal fromUnixNano, decimal toUnixNano, AccessScope scope,
        CancellationToken cancellationToken = default) =>
        TopologyEdgeWindowResponse?.Invoke(edgeId, readClockUnixNano, fromUnixNano, toUnixNano, scope,
            cancellationToken) ?? GetTopologyEdgeAsync(edgeId, readClockUnixNano, scope, cancellationToken);
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

    /// <summary>
    /// Sayım sorguları — T36'nın zaman güvenilirliği ölçümü <b>filtreli ve
    /// filtresiz</b> iki sayım yapıyor ve testin ikisini ayırt etmesi gerekiyor.
    /// </summary>
    public List<EventQuery> CountQueries { get; } = [];

    /// <summary>Sayımı sorguya göre üreten kanca; verilmezse olay sayısı dönüyor.</summary>
    public Func<EventQuery, long>? CountOverride { get; set; }

    public virtual Task<long> CountEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default)
    {
        CountQueries.Add(query);
        return Task.FromResult(CountOverride?.Invoke(query) ?? Events.Count);
    }

    public Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task WriteChangeAsync(ChangeEvent change, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Kapsam içindeki envanter — topoloji sağlayıcısının <b>paydası</b>
    /// (F5 · S1). Varsayılan boş: envanteri doldurmayan bir test, sağlayıcının
    /// <c>NeverFed</c> yolunu ölçüyor demektir ve bu bilerek böyle.
    /// </summary>
    public List<SourceSummary> Inventory { get; } = [];

    public Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceSummary>>([.. Inventory]);

    public virtual Task<IReadOnlyList<SourceActivityRow>> GetSourceActivityAsync(
        SourceActivityWindow window, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceActivityRow>>([]);

    public Task<IReadOnlyList<HistogramBucket>> GetEventHistogramAsync(
        EventHistogramQuery query, AccessScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HistogramBucket>>([]);

    // ---- F3 korelasyonları (T35) ------------------------------------------

    public List<CorrelationWindow> CorrelationWindows { get; } = [];

    public List<SignatureCount> FirstSeen { get; } = [];

    public List<SignatureVolume> Volumes { get; } = [];

    public List<FieldValueCount> LiftRows { get; } = [];

    public List<SourceOnset> Onsets { get; } = [];

    /// <summary>Ortak öznitelik sorgusuna geçirilen alanlar — izin listesi sınanıyor.</summary>
    public List<string> LiftFieldsAsked { get; } = [];

    /// <summary>Yayılmaya geçirilen önem eşiği.</summary>
    public byte SeverityAsked { get; private set; }

    public Task<IReadOnlyList<SignatureCount>> GetFirstSeenSignaturesAsync(
        CorrelationWindow window, AccessScope scope, int limit, CancellationToken cancellationToken = default)
    {
        CorrelationWindows.Add(window);
        return Task.FromResult<IReadOnlyList<SignatureCount>>([.. FirstSeen.Take(limit)]);
    }

    public Task<IReadOnlyList<SignatureVolume>> GetSignatureVolumeAsync(
        CorrelationWindow window, AccessScope scope, int limit, CancellationToken cancellationToken = default)
    {
        CorrelationWindows.Add(window);
        return Task.FromResult<IReadOnlyList<SignatureVolume>>([.. Volumes.Take(limit)]);
    }

    public Task<IReadOnlyList<FieldValueCount>> GetAttributeLiftAsync(
        CorrelationWindow window, AccessScope scope, IReadOnlyList<string> fields, int limitPerField,
        CancellationToken cancellationToken = default)
    {
        CorrelationWindows.Add(window);
        LiftFieldsAsked.AddRange(fields);
        return Task.FromResult<IReadOnlyList<FieldValueCount>>([.. LiftRows]);
    }

    public Task<IReadOnlyList<SourceOnset>> GetPropagationAsync(
        CorrelationWindow window, AccessScope scope, byte severityAtOrBelow, int limit,
        CancellationToken cancellationToken = default)
    {
        CorrelationWindows.Add(window);
        SeverityAsked = severityAtOrBelow;
        return Task.FromResult<IReadOnlyList<SourceOnset>>([.. Onsets.Take(limit)]);
    }

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
