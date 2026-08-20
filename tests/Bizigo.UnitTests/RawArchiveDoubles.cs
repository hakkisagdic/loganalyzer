using Bizigo.Storage.Raw;

namespace Bizigo.UnitTests;

/// <summary>
/// Bellek içi nesne deposu.
///
/// <para>
/// Entegrasyon tarafındaki <c>FakeObjectStoreOverS3</c> gerçek RustFS'i sarıyor
/// ve <b>bozulmayı</b> taklit ediyor; burada gerek yok. Bu testlerin ölçtüğü
/// şey silme kapısı ve kurtarma kararı — ikisi de depo değil <b>manifest</b>
/// üzerinden veriliyor.
/// </para>
/// </summary>
internal sealed class InMemoryObjectStore : IRawObjectStore
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public List<string> Written { get; } = [];

    public Task EnsureBucketAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        _objects[key] = content.ToArray();
        Written.Add(key);
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var content) ? content : null);

    public Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.TryGetValue(key, out var content)
            ? new RawObjectInfo(key, content.LongLength)
            : null);

    /// <summary>Nesneyi depodan kaybeder — RustFS'in veri kaybettiği senaryo.</summary>
    public void Lose(string key) => _objects.Remove(key);

    public bool Has(string key) => _objects.ContainsKey(key);
}

/// <summary>Bellek içi WAL taklidi; segment üzerinde tam denetim için.</summary>
internal sealed class InMemorySegmentSource : IRawSegmentSource
{
    private readonly Dictionary<string, List<ReadOnlyMemory<byte>>> _segments = new(StringComparer.Ordinal);

    public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

    public void Add(string id, IEnumerable<ReadOnlyMemory<byte>> lines) =>
        _segments[id] = [.. lines];

    /// <summary>Segmenti diskten yok eder — saklama süresi dolup silinmiş hâli.</summary>
    public void Forget(string id) => _segments.Remove(id);

    public IReadOnlyList<PendingSegment> ListPending() =>
        [.. _segments.Keys
            .Where(k => !Deleted.Contains(k))
            .Select(k => new PendingSegment(k, DateTimeOffset.UnixEpoch))];

    public IEnumerable<ReadOnlyMemory<byte>> ReadLines(string segmentId) =>
        _segments.TryGetValue(segmentId, out var lines) ? lines : [];

    public void Delete(string segmentId)
    {
        Deleted.Add(segmentId);
        _segments.Remove(segmentId);
    }
}

/// <summary>
/// <c>raw_ref</c> yazımını yutan alıcı. Bu testlerin konusu değil; ClickHouse
/// tarafının kendi testleri var.
/// </summary>
internal sealed class NoOpRawRefSink : IRawRefSink
{
    public ValueTask RecordAsync(
        string objectKey,
        IReadOnlyList<RawRefEntry> refs,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
