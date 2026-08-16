namespace Bizigo.Ingest.Discovery;

/// <summary>
/// Keşif yolunun sayaçları. Sidecar sıcak yolda olmadığı için arızası
/// <b>sessiz</b>: ingest akmaya devam eder, yalnızca <c>template_id</c>
/// boş kalır. Bu sayaçlar olmadan "sidecar üç gündür ölü" durumu kimsenin
/// dikkatini çekmez.
/// </summary>
public sealed class DiscoveryStats
{
    private long _enqueued;
    private long _droppedQueueFull;
    private long _droppedCircuitOpen;
    private long _requests;
    private long _requestFailures;
    private long _timeouts;
    private long _minedMessages;
    private long _newTemplates;
    private long _cacheHits;
    private long _cacheMisses;
    private long _signatureDrift;

    /// <summary>Kuyruğa alınan keşif isteği sayısı.</summary>
    public long Enqueued => Interlocked.Read(ref _enqueued);

    /// <summary>Kuyruk dolu olduğu için düşürülenler — F1 §9 "dolunca düşür".</summary>
    public long DroppedQueueFull => Interlocked.Read(ref _droppedQueueFull);

    /// <summary>Devre kesici açıkken hiç denenmeden düşürülenler.</summary>
    public long DroppedCircuitOpen => Interlocked.Read(ref _droppedCircuitOpen);

    public long Requests => Interlocked.Read(ref _requests);

    public long RequestFailures => Interlocked.Read(ref _requestFailures);

    public long Timeouts => Interlocked.Read(ref _timeouts);

    public long MinedMessages => Interlocked.Read(ref _minedMessages);

    /// <summary>İlk kez görülen şablonlar — F3'ün "ilk görülen imza" sinyali.</summary>
    public long NewTemplates => Interlocked.Read(ref _newTemplates);

    public long CacheHits => Interlocked.Read(ref _cacheHits);

    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    /// <summary>
    /// .NET'in ürettiği imza ile sidecar'ın maskelediği metnin ayrıştığı
    /// durumlar. <b>Sıfırdan büyükse maskeleme sözlüğü iki motorda farklı
    /// davranıyor demektir</b> ve önbellekteki <c>template_id</c>'lere
    /// güvenilemez (K14 tek kaynak varsayımı çökmüş olur).
    /// </summary>
    public long SignatureDrift => Interlocked.Read(ref _signatureDrift);

    public void Enqueue() => Interlocked.Increment(ref _enqueued);

    public void DropQueueFull() => Interlocked.Increment(ref _droppedQueueFull);

    public void DropCircuitOpen() => Interlocked.Increment(ref _droppedCircuitOpen);

    public void Request() => Interlocked.Increment(ref _requests);

    public void RequestFailed(bool timedOut)
    {
        Interlocked.Increment(ref _requestFailures);
        if (timedOut)
        {
            Interlocked.Increment(ref _timeouts);
        }
    }

    public void Mined(int messages, int newTemplates)
    {
        Interlocked.Add(ref _minedMessages, messages);
        Interlocked.Add(ref _newTemplates, newTemplates);
    }

    public void CacheHit() => Interlocked.Increment(ref _cacheHits);

    public void CacheMiss() => Interlocked.Increment(ref _cacheMisses);

    public void Drift() => Interlocked.Increment(ref _signatureDrift);
}
