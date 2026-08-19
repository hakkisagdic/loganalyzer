using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting.Notifications;

/// <summary>
/// HTTP ile konuşan üç kanalın ortak gövdesi: Slack, Teams ve genel webhook.
///
/// <para>
/// Üçünün de gizli bilgisi <b>hedef URL'in kendisi</b> ve üçü de aynı hata
/// sınıflandırmasına tabi. Ortaklaştırılan şey tam olarak bu ikisi; ayrışan tek
/// şey gövdenin biçimi.
/// </para>
///
/// <para>
/// <b>İstisna mesajı asla olduğu gibi taşınmıyor.</b> <c>HttpRequestException</c>
/// çoğu zaman hedef host'u mesajında taşıyor ve o host gizli URL'in parçası.
/// Hata metni burada kontrollü bir şablondan kuruluyor, sonra bir kez daha
/// redaksiyondan geçiriliyor — iki kat, çünkü tek katı unutmak yeterdi.
/// </para>
/// </summary>
public abstract class HttpNotificationChannel(IHttpClientFactory clients, AlertingOptions options)
    : INotificationChannel
{
    public abstract NotificationChannelType Type { get; }

    /// <summary>Kanala özgü gövde ve içerik tipi.</summary>
    protected abstract HttpContent BuildContent(NotificationMessage message);

    public async Task<ChannelResult> SendAsync(
        NotificationMessage message,
        ResolvedChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(channel);

        if (!Uri.TryCreate(channel.Secret, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            // Adres yazılmıyor: geçersiz de olsa gizli bilgi.
            return ChannelResult.Permanent("Kanalın hedef adresi geçerli bir HTTP(S) URL'i değil.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = BuildContent(message),
        };

        foreach (var (key, value) in channel.Settings.Headers)
        {
            // Gövde başlıkları ayrı ayarlanıyor; ikisini karıştırmak
            // "Content-Type geçersiz" istisnasına yol açar.
            if (!request.Headers.TryAddWithoutValidation(key, value))
            {
                request.Content?.Headers.TryAddWithoutValidation(key, value);
            }
        }

        try
        {
            using var client = clients.CreateClient(nameof(HttpNotificationChannel));
            client.Timeout = options.ChannelTimeout;

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ChannelResult.Delivered();
            }

            var code = (int)response.StatusCode;

            // Gövdeyi hataya koymuyoruz: bazı sağlayıcılar isteğin URL'ini
            // yankılıyor ve o yankı doğrudan gizli bilginin kendisi olurdu.
            var error = string.Create(CultureInfo.InvariantCulture, $"Kanal {code} döndü.");

            return IsRetryable(response.StatusCode)
                ? ChannelResult.Transient(error)
                : ChannelResult.Permanent(error);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ChannelResult.Transient(
                $"Kanal {options.ChannelTimeout.TotalSeconds:0} sn içinde yanıt vermedi.");
        }
        catch (HttpRequestException ex)
        {
            // Yalnızca istisnanın TÜRÜ taşınıyor, mesajı değil: mesaj host adını
            // taşıyor ve host gizli URL'in parçası.
            return ChannelResult.Transient(
                SecretRedactor.Redact($"Kanala ulaşılamadı ({ex.HttpRequestError}).", channel.Secret));
        }
    }

    /// <summary>
    /// Hangi durum kodu yeniden denenmeye değer.
    ///
    /// <para>
    /// 5xx ve 429 geçici; 408 de öyle. Kalan 4xx'ler yapılandırma hatası —
    /// yanlış yazılmış bir webhook URL'ini beş kez denemek kuyruğu şişirmekten
    /// başka bir şey yapmaz.
    /// </para>
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;

    protected static StringContent Json(string payload) =>
        new(payload, Encoding.UTF8, "application/json");
}

/// <summary>
/// Slack gelen webhook'u. Gövde <c>{"text": "..."}</c> — Slack markdown'ı bu
/// alanda yorumluyor ve blok API'sine göre çok daha az kırılgan.
/// </summary>
public sealed class SlackChannel(IHttpClientFactory clients, AlertingOptions options)
    : HttpNotificationChannel(clients, options)
{
    public override NotificationChannelType Type => NotificationChannelType.Slack;

    protected override HttpContent BuildContent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(JsonSerializer.Serialize(new { text = message.ToPlainText() }));
    }
}

/// <summary>
/// Microsoft Teams gelen webhook'u — <c>MessageCard</c> biçimi.
///
/// <para>
/// Teams düz metni kabul etmiyor; kart şeması zorunlu. <c>potentialAction</c>
/// bağlantıyı kartın üstünde bir düğmeye çeviriyor, ki "şuna bak" mesajının
/// tamamı o düğme.
/// </para>
/// </summary>
public sealed class TeamsChannel(IHttpClientFactory clients, AlertingOptions options)
    : HttpNotificationChannel(clients, options)
{
    public override NotificationChannelType Type => NotificationChannelType.Teams;

    protected override HttpContent BuildContent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var card = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["@type"] = "MessageCard",
            ["@context"] = "https://schema.org/extensions",
            ["summary"] = message.Title,
            ["themeColor"] = "D93025",
            ["title"] = message.Title,
            ["text"] = message.ToPlainText().Replace("\n", "\n\n", StringComparison.Ordinal),
        };

        if (!string.IsNullOrWhiteSpace(message.Link))
        {
            card["potentialAction"] = new[]
            {
                new
                {
                    @type = "OpenUri",
                    name = "Aramayı aç",
                    targets = new[] { new { os = "default", uri = message.Link } },
                },
            };
        }

        return Json(JsonSerializer.Serialize(card));
    }
}

/// <summary>
/// Genel webhook. Gövde <b>makine okunabilir</b>: alıcı bir insan değil, çoğu
/// zaman bir otomasyon.
/// </summary>
public sealed class WebhookChannel(IHttpClientFactory clients, AlertingOptions options)
    : HttpNotificationChannel(clients, options)
{
    public override NotificationChannelType Type => NotificationChannelType.Webhook;

    protected override HttpContent BuildContent(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Json(message.ToJson());
    }
}
