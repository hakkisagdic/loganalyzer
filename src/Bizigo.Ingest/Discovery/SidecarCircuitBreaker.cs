namespace Bizigo.Ingest.Discovery;

public enum CircuitState
{
    /// <summary>İstekler geçiyor.</summary>
    Closed,

    /// <summary>Kapalı devre — hiçbir istek denenmiyor.</summary>
    Open,

    /// <summary>Süre doldu; tek bir yoklama isteği geçiyor.</summary>
    HalfOpen,
}

/// <summary>
/// Sidecar devre kesicisi (F1 §9: ardışık N hata → 5 dk kapalı).
///
/// <para>
/// Buradaki asıl kazanç hata anında değil, <b>hata sonrasında</b>: sidecar
/// ölünce her istek bağlantı reddiyle hızlıca düşer ama yine de her olay için
/// bir soket denemesi yapılırdı. Devre açıldıktan sonra hiç denenmiyor, yani
/// ölü bir sidecar'ın maliyeti sıfıra iniyor. Ölçülebilir olması için de
/// sağlık ucunda görünüyor (<c>/internal/discovery/stats</c>).
/// </para>
///
/// <para>
/// Yarı açık durumda <b>tek</b> yoklama geçiyor: sidecar geri geldiğinde beş
/// dakika daha beklemek gereksiz, ama hâlâ ölüyse tüm kuyruğu üstüne
/// yollamak da anlamsız.
/// </para>
/// </summary>
public sealed class SidecarCircuitBreaker(SidecarOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private int _consecutiveFailures;
    private long _openedCount;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;
    private CircuitState _state = CircuitState.Closed;

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                RefreshLocked();
                return _state;
            }
        }
    }

    public long OpenedCount => Interlocked.Read(ref _openedCount);

    public string? LastError { get; private set; }

    /// <summary>
    /// Bir istek denenebilir mi? Yarı açıkta yalnızca ilk çağıran <c>true</c>
    /// alır — yoklama tek olmalı.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            RefreshLocked();

            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.HalfOpen when !_probeInFlight:
                    _probeInFlight = true;
                    return true;

                default:
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _probeInFlight = false;
            _state = CircuitState.Closed;
            LastError = null;
        }
    }

    public void RecordFailure(string reason)
    {
        lock (_gate)
        {
            LastError = reason;
            _probeInFlight = false;

            if (_state == CircuitState.HalfOpen)
            {
                // Yoklama da düştü: sayaca bakmadan doğrudan yeniden aç.
                Open();
                return;
            }

            if (++_consecutiveFailures >= options.FailureThreshold)
            {
                Open();
            }
        }

        void Open()
        {
            _state = CircuitState.Open;
            _openedAt = time.GetUtcNow();
            _consecutiveFailures = 0;
            Interlocked.Increment(ref _openedCount);
        }
    }

    /// <summary>
    /// Sürüm uyuşmazlığı (F1 §9). Tek bir hata gibi değil, <b>doğrudan</b>
    /// açılış sebebi: yanlış sürümle konuşmak sessizce yanlış veri üretir,
    /// hiç konuşmamak yalnızca özelliği kapatır.
    /// </summary>
    public void TripOnVersionMismatch(string reason)
    {
        lock (_gate)
        {
            LastError = reason;
            _probeInFlight = false;
            _consecutiveFailures = 0;
            _state = CircuitState.Open;
            _openedAt = time.GetUtcNow();
            Interlocked.Increment(ref _openedCount);
        }
    }

    private void RefreshLocked()
    {
        if (_state == CircuitState.Open && time.GetUtcNow() - _openedAt >= options.BreakDuration)
        {
            _state = CircuitState.HalfOpen;
            _probeInFlight = false;
        }
    }
}
