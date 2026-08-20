using Bizigo.Contracts.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.ControlPlane;

namespace Bizigo.Alerting.Notifications;

/// <param name="Ok">Teslim edildi mi.</param>
/// <param name="Retryable">
/// Yeniden denemenin anlamı var mı. <b>Ayrım bilinçli:</b> yanlış yapılandırılmış
/// bir kanalı (404, 401) beş kez denemek, kanalın kırık olduğunu öğrenmeyi beş
/// geri adım süresi geciktirir ve kuyruğu şişirir. Geçici arıza (5xx, 429, ağ)
/// yeniden deneniyor; kalıcı olan hemen kapanıyor.
/// </param>
/// <param name="Error">
/// Kullanıcıya ve loga gidecek sebep. <b>Redaksiyondan geçmiş olmak zorunda</b> —
/// bu tipi üreten her kanal <see cref="SecretRedactor"/>'dan geçiriyor.
/// </param>
public sealed record ChannelResult(bool Ok, bool Retryable, string Error)
{
    public static ChannelResult Delivered() => new(true, false, string.Empty);

    public static ChannelResult Transient(string error) => new(false, true, error);

    public static ChannelResult Permanent(string error) => new(false, false, error);
}

/// <summary>
/// Kanalın gizli <b>olmayan</b> yapılandırması (<c>notification_channels.config_json</c>).
///
/// <para>
/// Alanların hepsi her kanal için anlamlı değil — SMTP alanları yalnızca
/// e-postada, başlıklar yalnızca genel webhook'ta. Tek tip olmasının sebebi
/// yapılandırmanın <b>tek bir JSON şemasıyla</b> okunabilmesi: kanal tipi başına
/// ayrı şema, UI'ın da API'nin de dört ayrı doğrulama yolu taşıması demekti.
/// </para>
///
/// <para>
/// ⚠️ Buraya <b>hiçbir gizli bilgi girmiyor</b>. Kanal listeleme ucu bu nesneyi
/// olduğu gibi döndürüyor ve bunu güvenle yapabilmesinin tek sebebi, gizli
/// bilginin yapısal olarak başka bir kolonda olması.
/// </para>
/// </summary>
public sealed record ChannelSettings
{
    /// <summary>Genel webhook: ek istek başlıkları.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>E-posta: SMTP sunucusu.</summary>
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 25;

    public string From { get; init; } = string.Empty;

    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>E-posta: SMTP kullanıcı adı. Parola gizli tarafta.</summary>
    public string User { get; init; } = string.Empty;

    public bool UseStartTls { get; init; } = true;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static ChannelSettings Parse(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new ChannelSettings()
            : JsonSerializer.Deserialize<ChannelSettings>(json, Options) ?? new ChannelSettings();

    public string Serialize() => JsonSerializer.Serialize(this, Options);
}

/// <param name="Secret">
/// Çözülmüş gizli bilgi: webhook URL'i ya da SMTP parolası. Kanal göndericisinin
/// dışına <b>hiçbir biçimde</b> çıkmıyor.
/// </param>
public sealed record ResolvedChannel(
    NotificationChannelEntity Entity,
    ChannelSettings Settings,
    string Secret);

/// <summary>
/// Bir bildirim kanalı (T22).
///
/// <para>
/// <b>Senaryo motorundan bağımsız</b> — F4'te agent senaryoları da bu kanalları
/// kullanacak. Arayüz bu yüzden "alarm" kelimesini hiç geçirmiyor: içeriği
/// <see cref="NotificationMessage"/> taşıyor, kanal yalnızca biçimlendirip
/// gönderiyor.
/// </para>
/// </summary>
public interface INotificationChannel
{
    NotificationChannelType Type { get; }

    Task<ChannelResult> SendAsync(
        NotificationMessage message,
        ResolvedChannel channel,
        CancellationToken cancellationToken = default);
}
