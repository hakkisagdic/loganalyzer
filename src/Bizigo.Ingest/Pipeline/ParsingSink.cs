using Bizigo.ControlPlane;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;

namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Boru hattının parse adımı (F1 §4.2, §8). T03'teki geçişin yerini alıyor.
///
/// <para>
/// İki iş yapıyor ve ikisi de <b>kaynaktan</b> geliyor, olaydan değil: kapsam
/// grubu (K17) ve parser bağı. Olayın kendi içeriğine bakarak grup atamak, bir
/// cihazın kendi yetkisini seçebilmesi demek olurdu.
/// </para>
///
/// <para>
/// Kaynağı çözdükten sonrasını <see cref="EventComposer"/> yapıyor: dispatch,
/// imza ve şablon etiketi orada tek kopya hâlinde duruyor (T39).
/// </para>
///
/// <para>
/// Hiçbir şey <b>reddedilmiyor</b>: eşleşmeyen kaynak <c>_unassigned</c>'a,
/// eşleşmeyen satır <c>failed</c>'a düşüyor. İkisi de ham arşivde duruyor ve
/// parser düzeltilince replay ile geri kazanılıyor (K12).
/// </para>
/// </summary>
public sealed class ParsingSink(
    EventComposer composer,
    SourceDirectory sources,
    DispatchStats stats,
    IParsedEventSink downstream) : IIngestSink
{
    public async ValueTask HandleAsync(
        IReadOnlyList<DecodedRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var parsed = new List<ParsedEvent>(batch.Count);

        foreach (var record in batch)
        {
            var source = sources.Resolve(record.Raw.SourceKey);

            if (!source.IsKnown)
            {
                stats.RecordUnassignedSource();
            }

            parsed.Add(composer.Compose(record, source));
        }

        await downstream.HandleAsync(parsed, cancellationToken);
    }
}
