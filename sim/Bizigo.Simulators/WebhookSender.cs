using System.Net.Http.Headers;

namespace Bizigo.Simulators;

/// <param name="Status">HTTP durum kodu.</param>
/// <param name="Body">Cevap gövdesi — <c>change_id</c> ve <c>duplicate</c> burada.</param>
public sealed record WebhookSendResult(int Status, string Body);

/// <summary>
/// Üretilen teslimatı gerçekten gönderen taraf (S07).
///
/// <para>
/// <b>Neden ayrı bir tip:</b> üreteç saf — girdi verilince aynı baytları
/// döndürüyor ve ağ görmüyor. Gönderme yan etkili. İkisi tek yerde olsaydı
/// üretecin belirlenimciliğini sınayan testler bir HTTP istemcisi kurmak
/// zorunda kalırdı.
/// </para>
/// </summary>
public static class WebhookSender
{
    /// <summary>
    /// Aynı teslimatı <paramref name="times"/> kez gönderir.
    ///
    /// <para>
    /// <b>Tekrar burada, çağıranda değil:</b> S07'nin kabul kriteri
    /// <i>"aynı teslimat iki kez gönderilince tek kayıt"</i> ve "aynı"nın
    /// tanımı <b>aynı baytlar, aynı başlıklar</b>. Çağıran döngüyü kendi
    /// kursaydı üreteci iki kez çağırırdı ve iki gövde arasına bir zaman
    /// damgası farkı girseydi test <i>"idempotans çalışmıyor"</i> derdi —
    /// oysa gönderilen şey aynı teslimat değildi.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<WebhookSendResult>> SendAsync(
        HttpClient client,
        Uri url,
        WebhookDelivery delivery,
        int times = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);

        var results = new List<WebhookSendResult>(times);

        for (var i = 0; i < times; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);

            // Gövde HER SEFERİNDE aynı bayt dizisinden kuruluyor;
            // `ByteArrayContent` kopyalamıyor, o yüzden yeniden kullanım güvenli
            // değil ve her turda yenisi kuruluyor.
            var content = new ByteArrayContent(delivery.Body);

            foreach (var (name, value) in delivery.Headers)
            {
                // Content-Type içerik başlığı; istek başlıklarına eklenemez.
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue(value);
                    continue;
                }

                request.Headers.TryAddWithoutValidation(name, value);
            }

            request.Content = content;

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            results.Add(new WebhookSendResult((int)response.StatusCode, body));
        }

        return results;
    }
}
