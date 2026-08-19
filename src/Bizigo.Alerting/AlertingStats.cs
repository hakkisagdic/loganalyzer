using System.Diagnostics.CodeAnalysis;

namespace Bizigo.Alerting;

/// <summary>
/// Motorun sayaçları (T21).
///
/// <para>
/// <b>Neden ayrı bir sayaç sınıfı:</b> alarm motorunun arızası kendini belli
/// etmiyor. Bir kural sürekli zaman aşımına uğruyorsa belirtisi "alarm gelmedi"
/// olur ve bu, "her şey yolunda" ile aynı görünür. F1'in sidecar devre kesicisi
/// aynı sınıftan bir sorundu ve çözümü de aynı: arızayı <b>görünür</b> yapmak.
/// </para>
///
/// <para>
/// <see cref="ScopedQueries"/> özellikle önemli: T21'in kabul kriteri "kural
/// sayısı arttığında ClickHouse'a atılan sorgu sayısı doğrusal ötesi büyümüyor"
/// diyor ve bu ancak sayılabiliyorsa doğrulanabilir bir iddia.
/// </para>
/// </summary>
public sealed class AlertingStats
{
    private long _turns;
    private long _evaluated;
    private long _fired;
    private long _suppressed;
    private long _timedOut;
    private long _failed;
    private long _scopedQueries;
    private long _notificationsQueued;
    private long _notificationsDelivered;
    private long _notificationsRetried;
    private long _notificationsAbandoned;

    public long Turns => Interlocked.Read(ref _turns);
    public long Evaluated => Interlocked.Read(ref _evaluated);
    public long Fired => Interlocked.Read(ref _fired);
    public long Suppressed => Interlocked.Read(ref _suppressed);

    /// <summary>Sıfırdan büyükse motor <b>bilmediği</b> bir şeyi "sessiz" sanmış olabilir.</summary>
    public long TimedOut => Interlocked.Read(ref _timedOut);

    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Değerlendirme yolunda <see cref="Bizigo.Query.IScopedQuery"/>'ye yapılan çağrı sayısı.</summary>
    public long ScopedQueries => Interlocked.Read(ref _scopedQueries);

    public long NotificationsQueued => Interlocked.Read(ref _notificationsQueued);
    public long NotificationsDelivered => Interlocked.Read(ref _notificationsDelivered);
    public long NotificationsRetried => Interlocked.Read(ref _notificationsRetried);

    /// <summary>Deneme hakkı bitmiş teslimler. Sıfırdan büyükse bir kanal kalıcı olarak kırık.</summary>
    public long NotificationsAbandoned => Interlocked.Read(ref _notificationsAbandoned);

    public void Turn() => Interlocked.Increment(ref _turns);
    public void Evaluate() => Interlocked.Increment(ref _evaluated);
    public void Fire() => Interlocked.Increment(ref _fired);
    public void Suppress() => Interlocked.Increment(ref _suppressed);
    public void TimeOut() => Interlocked.Increment(ref _timedOut);
    public void Fail() => Interlocked.Increment(ref _failed);
    public void ScopedQuery() => Interlocked.Increment(ref _scopedQueries);
    public void QueueNotification() => Interlocked.Increment(ref _notificationsQueued);
    public void DeliverNotification() => Interlocked.Increment(ref _notificationsDelivered);
    public void RetryNotification() => Interlocked.Increment(ref _notificationsRetried);
    public void AbandonNotification() => Interlocked.Increment(ref _notificationsAbandoned);

    [SuppressMessage("Design", "CA1024:Use properties where appropriate",
        Justification = "Anlık görüntü bir hesap; özellik gibi bedava görünmemeli.")]
    public AlertingSnapshot Snapshot() => new(
        Turns, Evaluated, Fired, Suppressed, TimedOut, Failed, ScopedQueries,
        NotificationsQueued, NotificationsDelivered, NotificationsRetried, NotificationsAbandoned);
}

public sealed record AlertingSnapshot(
    long Turns,
    long Evaluated,
    long Fired,
    long Suppressed,
    long TimedOut,
    long Failed,
    long ScopedQueries,
    long NotificationsQueued,
    long NotificationsDelivered,
    long NotificationsRetried,
    long NotificationsAbandoned);
