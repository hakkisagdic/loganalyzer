using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Microsoft.Extensions.Logging;

namespace Bizigo.Ingest.Pipeline;

/// <param name="Raw">Orijinal baytlarıyla ham kayıt.</param>
/// <param name="Source">Envanterden çözülen kimlik ve kapsam.</param>
/// <param name="Parsed">Dispatcher'ın seçtiği parser'ın çıktısı.</param>
/// <param name="Tier">Hangi kademeden geldiği.</param>
public sealed record ParsedEvent(
    RawRecord Raw,
    ResolvedSource Source,
    ParseResult Parsed,
    DispatchTier Tier);

/// <summary>
/// Ayrıştırılmış olayların çıkışı. T07 normalizasyonu ve ClickHouse yazımını
/// buraya takacak; T06'da varsayılan uygulama yalnızca sayıyor.
/// </summary>
public interface IParsedEventSink
{
    ValueTask HandleAsync(IReadOnlyList<ParsedEvent> batch, CancellationToken cancellationToken);
}

public sealed class CountingParsedEventSink : IParsedEventSink
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public ValueTask HandleAsync(IReadOnlyList<ParsedEvent> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Add(ref _count, batch.Count);
        return ValueTask.CompletedTask;
    }
}

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

            parsed.Add(new ParsedEvent(
                record.Raw with { OwnerGroup = source.OwnerGroup, SourceId = source.SourceId },
                source,
                result.Result,
                result.Tier));
        }

        await downstream.HandleAsync(parsed, cancellationToken);
    }
}
