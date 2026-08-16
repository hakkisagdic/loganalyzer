namespace Bizigo.Parsing.Dispatch;

/// <summary>
/// Dispatcher sağlık sayaçları.
///
/// <para>
/// <b><c>bound_ratio</c> asıl ölçü</b> (F1 §4.2): kademe 1'den geçen olayların
/// oranı. Hedef >%95. Düşüyorsa sistem hâlâ çalışıyor gibi görünür — satırlar
/// literal filtreyle yine ayrıştırılır — ama envanter bakımsız kalmış demektir
/// ve yanlış parser'a düşme riski artmıştır. Bu metrik olmadan o sessiz çürüme
/// fark edilmez.
/// </para>
/// </summary>
public sealed class DispatchStats
{
    private long _bound;
    private long _candidate;
    private long _unmatched;
    private long _attempts;
    private long _boundMisses;
    private long _unassignedSources;

    public long Bound => Interlocked.Read(ref _bound);
    public long Candidate => Interlocked.Read(ref _candidate);
    public long Unmatched => Interlocked.Read(ref _unmatched);
    public long Total => Bound + Candidate + Unmatched;

    /// <summary>Denenen parser sayısının toplamı — ön filtrenin ne kadar daralttığı.</summary>
    public long Attempts => Interlocked.Read(ref _attempts);

    /// <summary>
    /// Envanterde bağlı parser'ın tutmadığı durumlar. Sıfırdan büyükse cihaz
    /// yazılımı değişmiş ya da bağ yanlış — envanterin düzeltilmesi gerekir.
    /// </summary>
    public long BoundMisses => Interlocked.Read(ref _boundMisses);

    /// <summary>Envanterde bulunamayan kaynaklardan gelen olaylar (F1 §8).</summary>
    public long UnassignedSources => Interlocked.Read(ref _unassignedSources);

    public double BoundRatio => Total == 0 ? 1.0 : (double)Bound / Total;

    public double UnmatchedRatio => Total == 0 ? 0.0 : (double)Unmatched / Total;

    public void Record(DispatchTier tier, int attempts)
    {
        Interlocked.Add(ref _attempts, attempts);

        switch (tier)
        {
            case DispatchTier.InventoryBound:
                Interlocked.Increment(ref _bound);
                break;
            case DispatchTier.Candidate:
                Interlocked.Increment(ref _candidate);
                break;
            default:
                Interlocked.Increment(ref _unmatched);
                break;
        }
    }

    public void RecordBoundMiss() => Interlocked.Increment(ref _boundMisses);

    public void RecordUnassignedSource() => Interlocked.Increment(ref _unassignedSources);
}
