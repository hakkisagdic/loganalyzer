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
    public static IReadOnlyList<string> TelemetryCandidateOrder { get; } =
        Array.AsReadOnly(new[] { "bizigo.source_key", "service.instance.id", "host.id", "host.name", "service.name" });
    public HistoricalTelemetryOwners HistoricalOwners { get; } = new(factory);
    public HistoricalTopologyBindings HistoricalTopologyBindings { get; } = new(factory);
    private readonly IDbContextFactory<ControlPlaneDbContext> _factory = factory;
    private Dictionary<string, ResolvedSource> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private sealed record TelemetrySnapshot(Dictionary<string, ResolvedSource> Sources, HashSet<string> Ambiguous);
    private TelemetrySnapshot _telemetry = new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));

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
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, ResolvedSource value)
        {
            if (map.TryGetValue(key, out var prior) && prior.SourceId != value.SourceId) ambiguous.Add(key);
            map[key] = value;
        }

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
                Add(source.PeerAddress, resolved);
            }

            if (!string.IsNullOrWhiteSpace(source.Hostname))
            {
                Add(source.Hostname, resolved);
            }

            Add(source.SourceId, resolved);
        }

        Interlocked.Exchange(ref _snapshot, map);
        Interlocked.Exchange(ref _telemetry, new TelemetrySnapshot(map, ambiguous));
    }

    /// <summary>Ordered server-side candidates; ambiguous aliases never pick a winner.</summary>
    public ResolvedSource ResolveTelemetry(IEnumerable<string> candidates)
    {
        var snapshot = Volatile.Read(ref _telemetry);
        var keys = candidates.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var (source, _) = SelectTelemetry(keys, key => snapshot.Sources.TryGetValue(key, out var value)
            ? snapshot.Ambiguous.Contains(key) ? new[] { value, value } : [value] : [], _ => true);
        return source ?? new(keys.FirstOrDefault() ?? "_unknown", OwnerGroups.Unassigned, "default", "auto", null, false);
    }

    internal static (T? Source, string Reason) SelectTelemetry<T>(IEnumerable<string> candidates,
        Func<string, T[]> matches, Func<T, bool> enabled) where T : class
    {
        foreach (var key in candidates.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var found = matches(key);
            if (found.Length > 1) return (null, "ambiguous");
            if (found.Length == 1) return enabled(found[0]) ? (found[0], "known") : (null, "disabled");
        }
        return (null, "unknown");
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
