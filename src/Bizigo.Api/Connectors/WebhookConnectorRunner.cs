using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Api.Webhooks;
using Bizigo.Contracts;
using Bizigo.ControlPlane;

namespace Bizigo.Api.Connectors;

/// <summary>
/// Webhook connector'ının gizli <b>olmayan</b> yapılandırması —
/// <c>change_connectors.config_json</c>'un şekli.
///
/// <para>
/// T24'ün <see cref="ChangeWebhookEndpoint"/>'i ile aynı alanlar, <b>eksi gizli
/// anahtar</b>: o, şifreli olarak <c>credential_cipher</c>'da duruyor. İkisini
/// tek yapıda tutmak, yapılandırmayı API yanıtında döndüren ilk satırın anahtarı
/// da döndürmesi demekti.
/// </para>
/// </summary>
public sealed record WebhookConnectorConfig
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = ChangeWebhookProviders.Generic;

    /// <summary>
    /// Ekran bunu <c>"Config"</c> gibi bir <b>metin</b> olarak yolluyor; sayı
    /// değil. Dönüştürücü olmadan çözümleme istisna veriyor ve uç sessizce
    /// "yok" görünüyordu — bir birim testi bunu yakaladı.
    /// </summary>
    [JsonPropertyName("targetKind")]
    [JsonConverter(typeof(JsonStringEnumConverter<ChangeTargetKind>))]
    public ChangeTargetKind TargetKind { get; init; } = ChangeTargetKind.Service;

    [JsonPropertyName("defaultChangeKind")]
    public string DefaultChangeKind { get; init; } = "deploy";

    [JsonPropertyName("signatureHeader")]
    public string SignatureHeader { get; init; } = string.Empty;

    [JsonPropertyName("mapping")]
    public GenericMappingConfig? Mapping { get; init; }
}

/// <summary>Bilinmeyen sağlayıcı için JSON yol eşlemesi (T24 sözdizimi).</summary>
public sealed record GenericMappingConfig
{
    [JsonPropertyName("targetId")] public string TargetId { get; init; } = string.Empty;
    [JsonPropertyName("changeKind")] public string ChangeKind { get; init; } = string.Empty;
    [JsonPropertyName("actor")] public string Actor { get; init; } = string.Empty;
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("timestamp")] public string Timestamp { get; init; } = string.Empty;
    [JsonPropertyName("externalRef")] public string ExternalRef { get; init; } = string.Empty;
    [JsonPropertyName("deliveryId")] public string DeliveryId { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Webhook connector'ı — <b>çalıştırılmıyor, doğrulanıyor</b>.
///
/// <para>
/// Webhook bir <i>push</i> kaynağı: veri dışarıdan geliyor, biz kimseye
/// bağlanmıyoruz. Dolayısıyla <see cref="RunAsync"/> hiç çağrılmıyor
/// (zamanlanmıyor) ve "bağlantı testi" burada başka bir şey demek: <b>ucun
/// gerçekten kabul edecek durumda olup olmadığı</b>. Test, yapılandırmanın
/// T24'ün eşleyicisi tarafından okunabildiğini ve imza anahtarının kayıtlı
/// olduğunu doğruluyor, sonra kullanıcıya CI tarafına yapıştıracağı adresi
/// veriyor.
/// </para>
///
/// <para>
/// Bunu "test edilemez" diye boş geçmek en kolay yoldu ve en pahalısı olurdu:
/// yanlış yapılandırılmış bir webhook ucunun tek belirtisi, aylar sonra RCA'da
/// aranan verinin <b>hiç birikmemiş</b> olmasıdır.
/// </para>
/// </summary>
public sealed class WebhookConnectorRunner(ILogger<WebhookConnectorRunner> log) : IChangeConnectorRunner
{
    public ChangeConnectorType ConnectorType => ChangeConnectorType.Webhook;

    public Task<ConnectorTestResult> TestAsync(
        ConnectorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!TryBuildEndpoint(context.Connector, context.Credential, out var endpoint, out var error))
        {
            return Task.FromResult(new ConnectorTestResult(false, error));
        }

        if (string.IsNullOrEmpty(endpoint.Secret))
        {
            return Task.FromResult(new ConnectorTestResult(
                false, "İmza anahtarı kayıtlı değil; imzasız istek kabul edilmiyor."));
        }

        log.LogInformation("Webhook connector doğrulandı: {Slug}", context.Connector.Slug);

        return Task.FromResult(new ConnectorTestResult(
            true,
            $"Uç hazır. CI tarafına şu adresi tanımlayın: POST /v1/changes/webhooks/{context.Connector.Slug} " +
            $"— imza başlığı: {WebhookSignature.HeaderFor(endpoint)}."));
    }

    /// <summary>
    /// Çağrılmıyor: webhook zamanlanmıyor, veri dışarıdan itiliyor. Yine de
    /// sessizce başarı dönmüyor — bir gün zamanlayıcıya bağlanırsa sebebi
    /// görünsün.
    /// </summary>
    public Task<ConnectorRunResult> RunAsync(
        ConnectorContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectorRunResult(
            true, 0, string.Empty));

    /// <summary>
    /// Veritabanı satırını T24'ün uç yapısına çevirir. Gizli anahtar
    /// yapılandırmadan değil, <b>çözülmüş kimlik bilgisinden</b> geliyor.
    /// </summary>
    public static bool TryBuildEndpoint(
        ChangeConnectorEntity connector,
        string credential,
        out ChangeWebhookEndpoint endpoint,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(connector);

        endpoint = new ChangeWebhookEndpoint();
        error = string.Empty;

        WebhookConnectorConfig? config;

        try
        {
            config = JsonSerializer.Deserialize<WebhookConnectorConfig>(connector.ConfigJson);
        }
        catch (JsonException ex)
        {
            // Mesaj yapılandırmanın İÇERİĞİNİ değil, okunamadığını söylüyor.
            error = $"Yapılandırma okunamadı: {ex.Message}";
            return false;
        }

        if (config is null)
        {
            error = "Yapılandırma boş.";
            return false;
        }

        if (!ChangeWebhookProviders.All.Contains(config.Provider, StringComparer.Ordinal))
        {
            error = $"Bilinmeyen sağlayıcı: '{config.Provider}'.";
            return false;
        }

        endpoint = new ChangeWebhookEndpoint
        {
            Id = connector.Slug,
            Provider = config.Provider,
            OwnerGroup = connector.OwnerGroup,
            Secret = credential,
            SignatureHeader = config.SignatureHeader,
            TargetKind = config.TargetKind,
            DefaultChangeKind = config.DefaultChangeKind,
            Enabled = connector.Enabled,
        };

        if (config.Mapping is { } mapping)
        {
            endpoint.Mapping = new GenericWebhookMapping
            {
                TargetId = mapping.TargetId,
                ChangeKind = mapping.ChangeKind,
                Actor = mapping.Actor,
                Summary = mapping.Summary,
                Timestamp = mapping.Timestamp,
                ExternalRef = mapping.ExternalRef,
                DeliveryId = mapping.DeliveryId,
            };

            foreach (var (key, path) in mapping.Details)
            {
                endpoint.Mapping.Details[key] = path;
            }
        }

        return true;
    }
}
