using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

/// <param name="SourceId">Envanterdeki kimlik; eşleşme yoksa gelen anahtarın kendisi.</param>
/// <param name="OwnerGroup">Kapsam grubu; eşleşme yoksa <see cref="OwnerGroups.Unassigned"/>.</param>
/// <param name="SourceClass">Nesne anahtarındaki sınıf bileşeni.</param>
/// <param name="Encoding">Kaynağın bildirilen kodlaması (<c>auto</c> ise tespit eder).</param>
/// <param name="ParserId">Envanterdeki parser bağı — dispatcher kademe 1 (F1 §4.2).</param>
/// <param name="IsKnown">Envanterde bulundu mu — sağlık uyarısının ölçüsü.</param>
public sealed record ResolvedSource(
    string SourceId,
    string OwnerGroup,
    string SourceClass,
    string Encoding,
    string? ParserId,
    bool IsKnown);

/// <summary>
/// Kaynak anahtarı (peer IP / hostname) → envanter kaydı.
///
/// <para>
/// <b>Neden T04'te:</b> <c>owner_group</c> arşiv nesne anahtarının parçası
/// (F1 §7.1) çünkü ham okuma da kapsam filtresinden geçmek zorunda. Nesne
/// anahtarı bir kez yazılıp değişmediği için çözümleme yüklemeden <b>önce</b>
/// olmak durumunda. Dispatcher (T06) aynı çözümleyiciyi sıcak yolda kullanacak.
/// </para>
///
/// <para>
/// Eşleşmeyen kaynak <b>reddedilmez</b>: <c>_unassigned</c>'a düşer ve
/// <see cref="ResolvedSource.IsKnown"/> ile işaretlenir. Veri kaybı, eksik
/// envanterden kötüdür (F1 §8).
/// </para>
/// </summary>
public sealed class SourceDirectory(IDbContextFactory<ControlPlaneDbContext> factory)
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _factory = factory;
    private Dictionary<string, ResolvedSource> _snapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Envanteri belleğe alır. Sıcak yolda veritabanına gitmemek için: envanter
    /// yüzlerce satır, olay akışı saniyede binlerce.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);

        var sources = await db.Sources
            .AsNoTracking()
            .Where(s => s.Enabled)
            .ToListAsync(cancellationToken);

        // Aynı kaynak hem IP hem hostname ile eşleşebilsin diye iki anahtar yazılıyor.
        var map = new Dictionary<string, ResolvedSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var resolved = new ResolvedSource(
                source.SourceId,
                source.OwnerGroup,
                source.SourceClass,
                source.Encoding,
                source.ParserId,
                IsKnown: true);

            if (!string.IsNullOrWhiteSpace(source.PeerAddress))
            {
                map[source.PeerAddress] = resolved;
            }

            if (!string.IsNullOrWhiteSpace(source.Hostname))
            {
                map[source.Hostname] = resolved;
            }

            map[source.SourceId] = resolved;
        }

        Interlocked.Exchange(ref _snapshot, map);
    }

    public ResolvedSource Resolve(string? sourceKey)
    {
        var snapshot = Volatile.Read(ref _snapshot);

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            if (snapshot.TryGetValue(sourceKey, out var match))
            {
                return match;
            }

            // "10.1.2.3:41022" biçimindeki peer adresinden portu atarak bir daha dene.
            var colon = sourceKey.LastIndexOf(':');
            if (colon > 0 && snapshot.TryGetValue(sourceKey[..colon], out var byHost))
            {
                return byHost;
            }
        }

        return new ResolvedSource(
            string.IsNullOrWhiteSpace(sourceKey) ? "_unknown" : sourceKey,
            OwnerGroups.Unassigned,
            "default",
            "auto",
            ParserId: null,
            IsKnown: false);
    }
}
