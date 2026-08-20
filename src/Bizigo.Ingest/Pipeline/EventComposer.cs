using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Ingest.Discovery;
using Bizigo.Normalization;
using Bizigo.Parsing.Dispatch;
using Bizigo.Parsing.Engine;
using Bizigo.Parsing.Grok;
using Microsoft.Extensions.Logging;

namespace Bizigo.Ingest.Pipeline;

/// <summary>
/// Tek bir çözülmüş kaydı <see cref="ParsedEvent"/>'e çeviren adım: dispatch →
/// imza → şablon etiketi.
///
/// <para>
/// <b>Neden <see cref="ParsingSink"/>'ten ayrıldı (T39):</b> altın örnek
/// yükleyicisi de tam olarak bu üç işi yapmak zorunda, ama kaynağını envanterden
/// çözmüyor — kaynağı kendisi biliyor. Sink'in içinde kalsaydı yükleyicinin tek
/// seçeneği ya Postgres'e bağlanmak ya da bu üç satırı <b>kopyalamak</b> olurdu.
/// İkinci kopya, imzanın ya da şablon etiketinin bir gün yalnızca bir yolda
/// değişmesi demektir; o ayrışma hata vermez, yalnızca ölçüm verisini
/// üretimdekinden farklı kılar.
/// </para>
///
/// <para>
/// Kapsam çözümü bilinçli olarak <b>dışarıda</b> bırakıldı: <c>owner_group</c>
/// ve <c>source_id</c> olaydan değil kaynaktan geliyor (K17) ve bu sınıf olayı
/// görüyor, kaynağı değil.
/// </para>
/// </summary>
public sealed class EventComposer(
    Dispatcher dispatcher,
    ITemplateAnnotator templates,
    MaskCatalog masks,
    ILogger<EventComposer> logger)
{
    public ParsedEvent Compose(DecodedRecord record, ResolvedSource source)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(source);

        var result = dispatcher.Dispatch(record.Decoded.Body, source.ParserId);

        if (result.Result.TimedOut)
        {
            // Zaman aşımı karantina sinyali (F1 §4.1 kademe 3); görünür olmalı.
            logger.LogWarning(
                "Parser {Parser} zaman aşımına uğradı ({Source}).",
                result.Result.ParserId,
                source.SourceId);
        }

        // İmza — **her olayda**, örneklemesiz, önbelleksiz (K35). Bu satır
        // F3'ün "ilk-görülen imza" ve "hacim sapması" korelasyonlarının
        // tamamını sidecar'dan kurtarıyor: `template_id` bir imzanın ilk
        // görülüşünde tanım gereği boş dönüyor, yani "yeni bir şey oldu"
        // diyen tam o satırda kimlik yoktu.
        var signature = masks.Compute(record.Decoded.Body);

        // Keşif katmanı (T12). Sidecar'a burada **gidilmiyor**: yalnızca
        // daha önce öğrenilmiş imzalar önbellekten okunuyor, bilinmeyen
        // imza sınırlı kuyruğa atılıyor. Kuyruk doluysa ya da sidecar ölüyse
        // dönen değer boş — boru hattı hiçbir şey beklemez.
        var templateId = templates.Annotate(
            source.SourceClass,
            record.Decoded.Body,
            signature,
            result.Result.Status == ParseStatus.Failed);

        return new ParsedEvent(
            record.Raw with { OwnerGroup = source.OwnerGroup, SourceId = source.SourceId },
            record.Decoded.Body,
            record.Decoded.Name,
            source,
            result.Result,
            result.Tier,
            templateId,
            signature.Hash);
    }
}
