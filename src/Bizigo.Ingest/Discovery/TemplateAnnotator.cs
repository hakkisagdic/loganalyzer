using Bizigo.Parsing.Grok;

namespace Bizigo.Ingest.Discovery;

/// <summary>
/// Sıcak yolun keşif katmanına tek dokunuşu.
///
/// <para>
/// Arayüz olmasının sebebi K14: sidecar kapalıyken ingest'te <b>hiçbir şey</b>
/// değişmemeli. <see cref="NullTemplateAnnotator"/> boş string döndürüyor ve
/// boru hattı bunu zaten olağan durum olarak ele alıyor.
/// </para>
/// </summary>
public interface ITemplateAnnotator
{
    /// <summary>
    /// Olayın <c>template_id</c>'si — bilinmiyorsa boş string.
    /// <b>Asla bloklamaz, asla istisna fırlatmaz.</b>
    /// </summary>
    string Annotate(string sourceClass, string body, bool parseFailed);
}

public sealed class NullTemplateAnnotator : ITemplateAnnotator
{
    public string Annotate(string sourceClass, string body, bool parseFailed) => string.Empty;
}

/// <summary>
/// Yerel imza + önbellek; ıskalayanı keşif kuyruğuna atar.
///
/// <para>
/// <b>Sidecar burada çağrılmıyor.</b> Yazma anında sidecar'a sormak onu sıcak
/// yola sokardı; onun yerine aynı maskeleme sözlüğüyle
/// (<see cref="MaskCatalog"/>) yerel bir imza üretilip önbelleğe bakılıyor.
/// Bir imzanın karşılığı ilk kez sorulduğunda boş dönüyor; sidecar cevabı
/// geldiğinde önbelleğe giriyor ve <b>aynı imzalı sonraki olaylar</b>
/// etiketleniyor. Yani ilk örnek keşfin kendisi, sonrakiler ücretsiz.
/// </para>
/// </summary>
public sealed class DiscoveryAnnotator(
    SidecarOptions options,
    MaskCatalog masks,
    TemplateCache cache,
    DiscoveryQueue queue,
    DiscoveryStats stats) : ITemplateAnnotator
{
    public string Annotate(string sourceClass, string body, bool parseFailed)
    {
        if (!options.Enabled || masks.Masks.Count == 0 || string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        // Örnekleme yalnızca *başarılı* olaylar için. `failed` olanlar
        // örneklenmiyor: keşfin asıl konusu onlar.
        var sampled = !parseFailed && options.SampleRate > 0
            && Random.Shared.NextDouble() < options.SampleRate;

        if (!parseFailed && !sampled)
        {
            return string.Empty;
        }

        var signature = masks.Signature(body);
        if (signature.Length == 0)
        {
            return string.Empty;
        }

        if (cache.TryGet(signature, out var templateId))
        {
            stats.CacheHit();
            return templateId;
        }

        stats.CacheMiss();
        queue.TryEnqueue(new DiscoveryItem(sourceClass, signature, body));
        return string.Empty;
    }
}
