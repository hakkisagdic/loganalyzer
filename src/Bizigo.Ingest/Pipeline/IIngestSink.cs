using Bizigo.Contracts;
using Bizigo.Ingest.Text;

namespace Bizigo.Ingest.Pipeline;

/// <param name="Raw">Orijinal baytlarıyla ham kayıt — arşive giden hal.</param>
/// <param name="Decoded">Kodlama tespiti sonrası metin.</param>
public sealed record DecodedRecord(RawRecord Raw, DecodedBody Decoded);

/// <summary>
/// Boru hattının parse adımı. T03'te <see cref="PassthroughSink"/> ile geçiştir;
/// dispatcher (T06) bunun yerine geçer.
///
/// <para>
/// Arayüz baştan <b>çoklu kayıt</b> alıyor (K5 riski #5): araya Kafka girerse ya
/// da toplu yazma gerekirse imza değişmesin.
/// </para>
/// </summary>
public interface IIngestSink
{
    ValueTask HandleAsync(IReadOnlyList<DecodedRecord> batch, CancellationToken cancellationToken);
}
