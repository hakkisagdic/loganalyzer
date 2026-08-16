using Bizigo.Contracts;

namespace Bizigo.Query;

/// <summary>
/// <b>Kapsam zorlamasının tek kapısı</b> (K17, F1 §10.2).
///
/// REST uçları, CLI, replay okuma, F3'ün kanıt toplayıcısı ve F4'ün MCP sunucusu —
/// hepsi buradan geçer. ClickHouse row policy tercih edilmedi çünkü tek kapı
/// olması gereken yer bu: agent'lar ve MCP de aynı API'yi kullanacak.
///
/// Her metot <see cref="AccessScope"/> istiyor; kapsamsız çağrı yazılamıyor.
/// Mimari test (T02) ayrıca bu derlemenin dışından <c>ClickHouse.Driver</c>'a
/// referans verilmesini yasaklıyor, yani kimse kapıyı atlayamıyor.
/// </summary>
public interface IScopedQuery
{
    Task<EventPage> SearchEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    Task<LogEvent?> GetEventAsync(Guid eventId, AccessScope scope, CancellationToken cancellationToken = default);

    Task<long> CountEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kapsam dışında kaç eşleşme olduğunu sayar, içeriği döndürmez.
    /// RCA raporundaki "kapsamınız dışında N ilişkili olay var" satırının kaynağı
    /// (RCA özelliği §3.2) — bilgi sızdırmadan yanlış güveni engelliyor.
    /// </summary>
    Task<long> CountOutOfScopeEventsAsync(EventQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangeEvent>> SearchChangesAsync(ChangeQuery query, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ham arşiv nesnesinin bu kapsamdan okunabilir olup olmadığı.
    /// Ham okuma yolu da bu kapıdan geçmeli — yoksa kapsam ayrımı arka kapıdan
    /// delinir (F1 §6.1 madde 3).
    /// </summary>
    Task<bool> CanReadRawObjectAsync(string objectKey, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Değişiklik olayı yazar (T10, RCA'nın F3'teki en güçlü sinyali).
    ///
    /// <para>
    /// Yazma da bu kapıdan geçiyor: çağıran yalnızca <b>kendi kapsamındaki</b> bir
    /// gruba yazabilir. Aksi halde bir ekip başka bir ekibin zaman çizelgesine
    /// olay düşürebilirdi ve RCA yanlış kanıtla çalışırdı.
    /// </para>
    /// </summary>
    Task WriteChangeAsync(ChangeEvent change, AccessScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kapsam içindeki kaynak envanteri (T10).
    ///
    /// <para>
    /// Envanter de ClickHouse verisi kadar kapsamlı: bir ekip başka bir ekibin
    /// cihaz listesini görmemeli. Filtreyi uç katmanında elle uygulamak, kapsam
    /// zorlamasını <b>ikinci bir yere</b> koymak olurdu — K17'nin kaçındığı şey
    /// tam olarak bu.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<SourceSummary>> SearchSourcesAsync(AccessScope scope, CancellationToken cancellationToken = default);
}

/// <param name="IsKnownToDispatcher"><c>parser_id</c> bağlı mı — dispatcher kademe 1.</param>
public sealed record SourceSummary(
    string SourceId,
    string OwnerGroup,
    string? PeerAddress,
    string? Hostname,
    string Vendor,
    string Product,
    string? ParserId,
    string Encoding,
    string SourceClass,
    bool Enabled,
    bool IsKnownToDispatcher);
