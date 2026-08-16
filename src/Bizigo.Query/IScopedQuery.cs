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
}
