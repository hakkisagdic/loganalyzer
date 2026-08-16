using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Storage.Raw;

/// <param name="Record">Bulunan ham kayıt.</param>
/// <param name="ObjectKey">İçinde bulunduğu arşiv nesnesi — teşhis için.</param>
/// <param name="ObjectsScanned">Kaç nesne açıldığı; maliyetin ölçüsü.</param>
public sealed record RawLookup(RawRecord Record, string ObjectKey, int ObjectsScanned);

/// <summary>
/// <c>event_id</c> → ham kayıt (K29, T10 <c>/v1/events/{id}/raw</c>).
///
/// <para>
/// Olay satırındaki <c>raw_ref</c> bir bayt konumu değil, arşiv <b>ön eki</b>:
/// ingest boru hattı ile yükleyici bağımsız çalıştığı için satır yazılırken
/// nesne henüz yoktu. Arama bu yüzden iki adımlı: manifest'ten ön eke ve zaman
/// aralığına uyan nesneler bulunuyor, sonra içleri taranıyor.
/// </para>
///
/// <para>
/// Maliyeti dürüstçe: nesne açılıyor ve satırları geziliyor. Bu, insan tetikli ve
/// seyrek bir işlem — replay zaten nesnenin tamamını okuduğu için ona ek yük
/// getirmiyor. Alternatif bir indeks tablosu O(1) verirdi ama yükleyicinin ve
/// replay'in bakması gereken ikinci bir gerçek kaynak doğururdu.
/// </para>
/// </summary>
public sealed class RawEventLocator(
    IDbContextFactory<ControlPlaneDbContext> factory,
    IRawObjectStore store)
{
    /// <summary>
    /// Tek bir olay için açılacak en fazla nesne sayısı. Sınır olmasaydı bozuk
    /// bir <c>raw_ref</c> bütün arşivi taratabilirdi.
    /// </summary>
    public int MaxObjectsToScan { get; init; } = 8;

    public async Task<RawLookup?> FindAsync(
        LogEvent logEvent,
        AccessScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(scope);

        // Kapsam kontrolü İNDİRMEDEN önce: grup olayın kendisinde taşınıyor ve
        // nesne anahtarında da var; ağ trafiği oluşmadan karar veriliyor.
        if (!scope.Allows(logEvent.OwnerGroup))
        {
            throw new RawAccessDeniedException(logEvent.RawRef);
        }

        if (string.IsNullOrWhiteSpace(logEvent.RawRef))
        {
            return null;
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Ön ek saat hassasiyetinde; olay o saatin nesnelerinden birinde.
        // `ts` aralığı ikinci bir daraltma: aynı saatte birden çok nesne olabilir.
        var candidates = await db.RawManifest
            .AsNoTracking()
            .Where(m => m.ObjectKey.StartsWith(logEvent.RawRef)
                && m.State != RawObjectState.Missing
                && m.TsFrom <= logEvent.Timestamp
                && m.TsTo >= logEvent.Timestamp)
            .OrderBy(m => m.TsFrom)
            .Take(MaxObjectsToScan)
            .Select(m => m.ObjectKey)
            .ToListAsync(cancellationToken);

        var reader = new RawReader(store);
        var scanned = 0;

        foreach (var objectKey in candidates)
        {
            scanned++;

            var records = await reader.ReadObjectAsync(objectKey, scope, cancellationToken);
            var match = records.FirstOrDefault(r => r.EventId == logEvent.EventId);

            if (match is not null)
            {
                return new RawLookup(match, objectKey, scanned);
            }
        }

        // Bulunamadı: nesne henüz yüklenmemiş olabilir (yükleyici geride), ya da
        // manifest ile arşiv ayrışmış olabilir. İkisi de sessiz kalmamalı —
        // çağıran 404 ile birlikte kaç nesne tarandığını görüyor.
        return null;
    }
}
