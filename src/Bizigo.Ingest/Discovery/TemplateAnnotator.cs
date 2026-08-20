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
    /// <param name="signature">
    /// Sıcak yolda <b>zaten hesaplanmış</b> maskelenmiş imza (K35). Annotator
    /// artık kendi maskelemesini yapmıyor: maskeleme her olayda koşuyor ve
    /// ikinci bir geçiş maliyeti iki katına çıkarırdı.
    /// </param>
    string Annotate(string sourceClass, string body, EventSignature signature, bool parseFailed);
}

public sealed class NullTemplateAnnotator : ITemplateAnnotator
{
    public string Annotate(string sourceClass, string body, EventSignature signature, bool parseFailed) =>
        string.Empty;
}

/// <summary>
/// Yerel imza + önbellek; ıskalayanı keşif kuyruğuna atar.
///
/// <para>
/// <b>Sidecar burada çağrılmıyor.</b> Yazma anında sidecar'a sormak onu sıcak
/// yola sokardı; onun yerine sıcak yolda üretilmiş imzayla
/// (<see cref="EventSignature"/>) önbelleğe bakılıyor. Bir imzanın karşılığı ilk
/// kez sorulduğunda boş dönüyor; sidecar cevabı geldiğinde önbelleğe giriyor ve
/// <b>aynı imzalı sonraki olaylar</b> etiketleniyor. Yani ilk örnek keşfin
/// kendisi, sonrakiler ücretsiz.
/// </para>
///
/// <para>
/// <b>K35'ten sonra ne değişti:</b> imzayı artık bu sınıf üretmiyor,
/// <see cref="MaskCatalog.Compute"/> her olayda sıcak yolda üretiyor. Bunun
/// doğrudan sonucu, örneklemenin anlamının daralması — aşağıda.
/// </para>
/// </summary>
public sealed class DiscoveryAnnotator(
    SidecarOptions options,
    TemplateCache cache,
    DiscoveryQueue queue,
    DiscoveryStats stats) : ITemplateAnnotator
{
    public string Annotate(string sourceClass, string body, EventSignature signature, bool parseFailed)
    {
        if (!options.Enabled || signature.IsEmpty || string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        // Önbellek araması artık **her** olayda yapılıyor. Eskiden örnekleme
        // aramadan da önce geliyordu, çünkü aramanın önkoşulu maskelemeydi ve
        // maskeleme pahalıydı. K35 maskelemeyi zaten ödediğimiz bir maliyete
        // çevirdi; geriye kalan bir sözlük araması ve onu zar atarak atlamak
        // başarılı olayların %99'unu bilinen bir şablondan mahrum bırakıyordu.
        if (cache.TryGet(signature.Text, out var templateId))
        {
            stats.CacheHit();
            return templateId;
        }

        stats.CacheMiss();

        // Örnekleme burada duruyor ve **yalnızca sidecar'ın yükünü** koruyor:
        // kuyruğa girmek keşif isteği demek. Yalnızca *başarılı* olaylar
        // örnekleniyor; `failed` olanlar keşfin asıl konusu.
        var sampled = !parseFailed && options.SampleRate > 0
            && Random.Shared.NextDouble() < options.SampleRate;

        if (!parseFailed && !sampled)
        {
            return string.Empty;
        }

        queue.TryEnqueue(new DiscoveryItem(sourceClass, signature.Text, body));
        return string.Empty;
    }
}
