using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Evidence;

/// <summary>
/// Kanıt paketlerinin kalıcı deposu (T36).
///
/// <para>
/// <b>Tek yazan taraf.</b> Varlıktaki sorgulanabilir üst veri (pencere, hash,
/// kapsam dışı sayı) belgenin içindekinin kopyası; ikisini iki ayrı yerden
/// yazmak, bir gün ayrışmalarının ve bunun hiçbir yerde görünmemesinin en kolay
/// yolu olurdu. <see cref="ToEntity"/> ikisini de aynı pakete bakarak
/// dolduruyor ve bir test eşitliği tutuyor.
/// </para>
/// </summary>
public sealed class EvidenceBundleStore(IDbContextFactory<ControlPlaneDbContext> factory)
{
    /// <summary>
    /// Bugünkü kodun okuyabildiği en eski paket sürümü.
    ///
    /// <para>
    /// Ayrı bir sabit olması bilinçli: <c>CurrentSchemaVersion</c> "ne
    /// yazıyoruz", bu ise "ne okuyabiliyoruz". İkisini tek sayıya bağlamak,
    /// sürümü artıran ilk kişinin bütün geçmiş paketleri okunamaz yapmasına ve
    /// bunu fark etmemesine yol açardı.
    /// </para>
    /// </summary>
    public const int MinReadableSchemaVersion = 1;

    public async Task<EvidenceBundle> SaveAsync(
        EvidenceBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        db.EvidenceBundles.Add(ToEntity(bundle));
        await db.SaveChangesAsync(cancellationToken);

        return bundle;
    }

    /// <summary>
    /// Paketi geri okur. Okunamayan bir sürüm <b>istisna fırlatıyor</b>, boş
    /// dönmüyor: "paket yok" ile "paket var ama okuyamıyoruz" farklı şeyler ve
    /// ikincisini birincisi gibi göstermek, F4'ün karşılaştırmasını sessizce
    /// eksik kümeye indirger.
    /// </summary>
    public async Task<EvidenceBundle?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var entity = await db.EvidenceBundles
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        if (entity.SchemaVersion < MinReadableSchemaVersion
            || entity.SchemaVersion > EvidenceBundle.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Kanıt paketi {id} sürüm {entity.SchemaVersion} ile yazılmış; " +
                $"bugünkü kod {MinReadableSchemaVersion}..{EvidenceBundle.CurrentSchemaVersion} okuyor.");
        }

        return BundleSerializer.Deserialize(entity.Payload);
    }

    /// <summary>
    /// Son paketler — JSON <b>açılmadan</b>. Liste ekranı (T37) her satır için
    /// belgeyi çözmek zorunda kalmamalı; üst verinin kolon olarak durmasının
    /// tamamı bu.
    /// </summary>
    public async Task<IReadOnlyList<EvidenceBundleSummary>> ListRecentAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        var rows = await db.EvidenceBundles
            .AsNoTracking()
            .OrderByDescending(b => b.GatheredAt)
            .ThenByDescending(b => b.Id)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(b => new EvidenceBundleSummary(
                b.Id,
                b.GatheredAt,
                b.WindowFrom,
                b.WindowTo,
                b.ContentHash,
                b.OutOfScopeCount,
                b.IsPartial,
                b.SchemaVersion))
            .ToListAsync(cancellationToken);

        return rows;
    }

    internal static EvidenceBundleEntity ToEntity(EvidenceBundle bundle) => new()
    {
        Id = bundle.Id,
        GatheredAt = bundle.GatheredAt,
        SchemaVersion = bundle.SchemaVersion,
        ContentHash = bundle.ContentHash,
        WindowFrom = bundle.Window.From,
        WindowTo = bundle.Window.To,
        BaselineFrom = bundle.Window.BaselineFrom,
        BaselineTo = bundle.Window.BaselineTo,
        OutOfScopeCount = bundle.OutOfScopeCount,
        IsPartial = bundle.IsPartial,
        Payload = BundleSerializer.Serialize(bundle),
    };
}

/// <summary>Liste satırı — belge açılmadan okunabilen üst veri.</summary>
public sealed record EvidenceBundleSummary(
    Guid Id,
    DateTimeOffset GatheredAt,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    string ContentHash,
    long OutOfScopeCount,
    bool IsPartial,
    int SchemaVersion);
