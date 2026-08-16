using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Discovery;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Microsoft.Extensions.Logging;

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
/// Hiçbir şey <b>reddedilmiyor</b>: eşleşmeyen kaynak <c>_unassigned</c>'a,
/// eşleşmeyen satır <c>failed</c>'a düşüyor. İkisi de ham arşivde duruyor ve
/// parser düzeltilince replay ile geri kazanılıyor (K12).
/// </para>
/// </summary>
public sealed class ParsingSink(
    Dispatcher dispatcher,
    SourceDirectory sources,
    DispatchStats stats,
    IParsedEventSink downstream,
    ITemplateAnnotator templates,
    ILogger<ParsingSink> logger) : IIngestSink
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

            var result = dispatcher.Dispatch(record.Decoded.Body, source.ParserId);

            if (result.Result.TimedOut)
            {
                // Zaman aşımı karantina sinyali (F1 §4.1 kademe 3); görünür olmalı.
                logger.LogWarning(
                    "Parser {Parser} zaman aşımına uğradı ({Source}).",
                    result.Result.ParserId,
                    source.SourceId);
            }

            // Keşif katmanı (T12). Sidecar'a burada **gidilmiyor**: yalnızca
            // daha önce öğrenilmiş imzalar önbellekten okunuyor, bilinmeyen
            // imza sınırlı kuyruğa atılıyor. Kuyruk doluysa ya da sidecar ölüyse
            // dönen değer boş — boru hattı hiçbir şey beklemez.
            var templateId = templates.Annotate(
                source.SourceClass,
                record.Decoded.Body,
                result.Result.Status == ParseStatus.Failed);

            parsed.Add(new ParsedEvent(
                record.Raw with { OwnerGroup = source.OwnerGroup, SourceId = source.SourceId },
                record.Decoded.Body,
                record.Decoded.Name,
                source,
                result.Result,
                result.Tier,
                templateId));
        }

        await downstream.HandleAsync(parsed, cancellationToken);
    }
}
