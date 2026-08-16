using Bizigo.ControlPlane;
using Bizigo.Storage.Raw;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Uygulamayla <b>aynı</b> yapılandırmayı kullanan bağlam fabrikası. Ayrışırsa
/// göç bir yerde çalışıp başka yerde çalışmaz — snake_case dahil.
/// </summary>
public sealed class ControlPlaneFactory(string connectionString)
    : IDbContextFactory<ControlPlaneDbContext>
{
    private readonly DbContextOptions<ControlPlaneDbContext> _options = Build(connectionString);

    private static DbContextOptions<ControlPlaneDbContext> Build(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ControlPlaneDbContext>();
        ControlPlaneServiceCollectionExtensions.Configure(builder, connectionString);
        return (DbContextOptions<ControlPlaneDbContext>)builder.Options;
    }

    public ControlPlaneDbContext CreateDbContext() => new(_options);
}

/// <summary>Bellek içi WAL taklidi — testin segment üzerinde tam denetimi olsun diye.</summary>
public sealed class FakeSegmentSource : IRawSegmentSource
{
    private readonly Dictionary<string, List<ReadOnlyMemory<byte>>> _segments = new(StringComparer.Ordinal);

    public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

    public void Add(string id, IEnumerable<ReadOnlyMemory<byte>> lines) =>
        _segments[id] = lines.ToList();

    public IReadOnlyList<PendingSegment> ListPending() => _segments.Keys
        .Where(k => !Deleted.Contains(k))
        .Select(k => new PendingSegment(k, DateTimeOffset.UnixEpoch))
        .ToArray();

    public IEnumerable<ReadOnlyMemory<byte>> ReadLines(string segmentId) =>
        _segments.TryGetValue(segmentId, out var lines) ? lines : [];

    public void Delete(string segmentId) => Deleted.Add(segmentId);
}

/// <summary>
/// Gerçek S3 deposunu sarar ve üstüne bozulma/kaybolma senaryosu ekler.
///
/// <para>
/// Sarmalayıcı olmasının sebebi: yükleme yolu gerçek RustFS'e karşı koşsun,
/// yalnızca <b>bozulma</b> taklit edilsin. Tamamen sahte bir depo kullanmak,
/// scrub'ın gerçek depoda çalıştığını göstermezdi.
/// </para>
/// </summary>
public sealed class FakeObjectStoreOverS3(S3RawObjectStore inner) : IRawObjectStore, IDisposable
{
    private readonly Dictionary<string, byte[]> _corrupted = new(StringComparer.Ordinal);
    private bool _hidden;

    public Task EnsureBucketAsync(CancellationToken cancellationToken = default) =>
        inner.EnsureBucketAsync(cancellationToken);

    public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        inner.PutAsync(key, content, cancellationToken);

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_hidden)
        {
            return null;
        }

        return _corrupted.TryGetValue(key, out var bad)
            ? bad
            : await inner.GetAsync(key, cancellationToken);
    }

    public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) =>
        _hidden ? Task.FromResult<RawObjectInfo?>(null) : inner.HeadAsync(key, cancellationToken);

    public void Corrupt(string key) => _corrupted[key] = "bozuk"u8.ToArray();

    /// <summary>Nesnelerin kaybolduğu durumu taklit eder.</summary>
    public void Hide() => _hidden = true;

    public void Dispose() => inner.Dispose();
}

/// <summary>İlerletilebilir saat — saklama süresi testleri gerçek 48 saat bekleyemez.</summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
