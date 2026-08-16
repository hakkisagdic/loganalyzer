using Bizigo.Storage.Raw;

namespace Bizigo.Ingest.Wal;

/// <summary>
/// Arşiv yükleyicisinin WAL'a bakışı (<see cref="IRawSegmentSource"/>).
///
/// <para>
/// Uyarlayıcı burada, arşiv katmanında değil: bağımlılık yönü ingest → arşiv.
/// Tersi olsaydı arşiv katmanı WAL'ın dosya biçimini bilmek zorunda kalır ve
/// kuyruk Kafka'ya taşındığında iki yerde birden değişiklik gerekirdi.
/// </para>
/// </summary>
public sealed class WalSegmentSource(WriteAheadLog wal) : IRawSegmentSource
{
    public IReadOnlyList<PendingSegment> ListPending() => wal
        .ListSealedSegments()
        .Select(s => new PendingSegment(s.Path, File.GetLastWriteTimeUtc(s.Path)))
        .ToArray();

    /// <summary>
    /// Çerçeveleri satırlara açar. Bir çerçeve bir isteğin batch'i, satırlar da
    /// o batch'in kayıtları — arşiv katmanı çerçeveyi değil kaydı görüyor.
    /// </summary>
    public IEnumerable<ReadOnlyMemory<byte>> ReadLines(string segmentId)
    {
        foreach (var frame in WriteAheadLog.ReadFrames(segmentId))
        {
            // Sınırlar önce hesaplanıyor: ReadOnlySpan bir `yield return` sınırını
            // geçemez (CS4007), o yüzden döngü içinde span tutulmuyor.
            foreach (var (start, length) in LineBounds(frame))
            {
                yield return frame.Slice(start, length);
            }
        }
    }

    private static List<(int Start, int Length)> LineBounds(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        var bounds = new List<(int, int)>();
        var start = 0;

        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] != (byte)'\n')
            {
                continue;
            }

            if (i > start)
            {
                bounds.Add((start, i - start));
            }

            start = i + 1;
        }

        // Satır sonu olmadan biten kalıntı: kodlayıcı her satırı '\n' ile
        // kapattığı için normalde oluşmaz, ama sessizce yutulmaz.
        if (start < span.Length)
        {
            bounds.Add((start, span.Length - start));
        }

        return bounds;
    }

    public void Delete(string segmentId) =>
        wal.Delete(new WalSegmentInfo(0, segmentId, 0, IsOpen: false));
}
