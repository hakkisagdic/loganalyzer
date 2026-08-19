using System.Collections.Concurrent;
using Bizigo.Contracts;
using Bizigo.Query;

namespace Bizigo.Alerting;

/// <summary>
/// Bir değerlendirme <b>turunun</b> paylaşılan sorgu yüzeyi (T21).
///
/// <para>
/// <b>Bu sınıf, T21'in maliyet kabul kriterinin taşıyıcısı.</b> "Kural sayısı
/// arttığında ClickHouse'a atılan sorgu sayısı doğrusal ötesi büyümüyor" demek
/// yetmez; sessizlik kurallarında naif uygulama <b>kural × kaynak</b> kadar sorgu
/// atardı. Burada envanter ve kaynak etkinliği tur başına ve <b>kapsam başına bir
/// kez</b> çekiliyor: elli sessizlik kuralı aynı ekibe aitse ClickHouse iki sorgu
/// görüyor, yüz değil.
/// </para>
///
/// <para>
/// <b>İptal jetonu bilinçli olarak kuralın değil turun jetonu.</b> Paylaşılan bir
/// görevi ilk çağıranın jetonuyla başlatmak, o kural zaman aşımına uğradığında
/// aynı görevi bekleyen diğer kuralları da düşürürdü — bir kuralın yavaşlığı
/// komşularının sonucunu bozardı. Paylaşılan sorgular kendi zaman aşımlarını
/// taşıyor.
/// </para>
/// </summary>
public sealed class AlertEvaluationContext
{
    private readonly IAlertQuerySource _queries;
    private readonly AlertingStats _stats;
    private readonly CancellationToken _turnToken;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<SourceActivityRow>>>> _activity =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<SourceSummary>>>> _inventory =
        new(StringComparer.Ordinal);

    public AlertEvaluationContext(
        IAlertQuerySource queries,
        AlertingStats stats,
        DateTimeOffset now,
        TimeSpan silenceLookback,
        TimeSpan timeout,
        CancellationToken turnToken = default,
        TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _turnToken = turnToken;
        _timeout = timeout;

        Now = now;
        SilenceLookback = silenceLookback;
    }

    /// <summary>
    /// Turun "şimdi"si — tek bir değer.
    ///
    /// <para>
    /// Her kuralın kendi <c>DateTimeOffset.UtcNow</c>'unu okuması, aynı turdaki
    /// iki kuralın farklı pencerelere bakması demekti; bildirimdeki bağlantının
    /// hangi aralığı açacağı da o zaman kuralın değerlendirilme sırasına bağlı
    /// olurdu.
    /// </para>
    /// </summary>
    public DateTimeOffset Now { get; }

    public TimeSpan SilenceLookback { get; }

    /// <summary>
    /// Kapsamdaki kaynakların son görülme bilgisi. Aynı kapsam için turda
    /// <b>bir kez</b> sorgulanıyor.
    /// </summary>
    public Task<IReadOnlyList<SourceActivityRow>> ActivityAsync(AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _activity.GetOrAdd(
            Key(scope),
            _ => new Lazy<Task<IReadOnlyList<SourceActivityRow>>>(() => RunAsync((query, token) =>
                query.GetSourceActivityAsync(
                    new SourceActivityWindow { From = Now - SilenceLookback, To = Now },
                    scope,
                    token)))).Value;
    }

    /// <summary>Kapsamdaki kaynak envanteri. Aynı kapsam için turda bir kez.</summary>
    public Task<IReadOnlyList<SourceSummary>> InventoryAsync(AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _inventory.GetOrAdd(
            Key(scope),
            _ => new Lazy<Task<IReadOnlyList<SourceSummary>>>(() => RunAsync((query, token) =>
                query.SearchSourcesAsync(scope, token)))).Value;
    }

    /// <summary>
    /// Kurala özel sayım. <b>Önbelleklenmiyor</b> — filtreler kurala göre
    /// değişiyor ve iki kuralın aynı filtreyi paylaştığını varsaymak, birinin
    /// filtresi değiştiğinde diğerinin yanlış sayıyla tetiklenmesi demek olurdu.
    /// </summary>
    public async Task<long> CountAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken)
    {
        _stats.ScopedQuery();

        using var lease = _queries.Lease();
        return await lease.Query.CountEventsAsync(query, scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunAsync<T>(Func<IScopedQuery, CancellationToken, Task<T>> work)
    {
        _stats.ScopedQuery();

        using var budget = new CancellationTokenSource(_timeout, _time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_turnToken, budget.Token);

        using var lease = _queries.Lease();
        return await work(lease.Query, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Önbellek anahtarı. Sıralı birleştirme şart: aynı grup kümesi farklı sırada
    /// gelirse iki ayrı anahtar üretirdi ve paylaşım sessizce kaybolurdu — hata
    /// değil, sadece iki kat sorgu, yani fark edilmesi en zor türden bir gerileme.
    /// </summary>
    private static string Key(AccessScope scope) =>
        scope.IsUnrestricted
            ? "*"
            : string.Join(",", scope.OwnerGroups.Order(StringComparer.Ordinal));
}
