namespace Bizigo.Storage.Raw;

/// <param name="Id">Segmentin kararlı kimliği (WAL'da dosya yolu).</param>
/// <param name="SealedAt">Yazımın kapandığı an — saklama süresi buradan işler.</param>
public sealed record PendingSegment(string Id, DateTimeOffset SealedAt);

/// <summary>
/// Yükleyicinin WAL'a bakışı.
///
/// <para>
/// Arayüz olmasının sebebi bağımlılık yönü: arşiv katmanı ingest'i tanımaz.
/// Kuyruk sonradan Kafka'ya taşınırsa (K5 riski #5) burada değişen tek şey
/// uygulama olur.
/// </para>
/// </summary>
public interface IRawSegmentSource
{
    /// <summary>Yazımı kapanmış, henüz arşive gitmemiş segmentler.</summary>
    IReadOnlyList<PendingSegment> ListPending();

    /// <summary>Segmentteki NDJSON satırları — çerçeve sınırları düzleştirilmiş.</summary>
    IEnumerable<ReadOnlyMemory<byte>> ReadLines(string segmentId);

    /// <summary>
    /// Segmenti siler. Çağıran <b>yalnızca</b> içerik arşivde doğrulandıktan ve
    /// saklama süresi dolduktan sonra çağırır.
    /// </summary>
    void Delete(string segmentId);
}
