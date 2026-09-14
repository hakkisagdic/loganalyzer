using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Mcp;

namespace Bizigo.Simulators.Mcp.Tools;

/// <summary>
/// <c>sim.webhook.emit</c> — imzalı bir değişiklik olayı gönderir (S07).
///
/// <para>
/// <b>Teslimat kimliği çağırandan geliyor ve rastgele üretilmiyor</b> — S07'nin
/// kararı, burada da geçerli. Rastgele olsaydı <c>times: 2</c> iki <b>farklı</b>
/// teslimat gönderirdi, idempotans sınaması hiç kurulamazdı, ve
/// <i>"iki kayıt oluştu"</i> sonucu <b>doğru görünürdü</b>.
/// </para>
///
/// <para>
/// <b>Zaman damgası sabit</b> ve bu da aynı kararın parçası: aynı istek aynı
/// baytları üretmezse gövde hash'ine düşen idempotans yolu her turda farklı bir
/// anahtar üretir. Sabit an <c>SimulatorSamples.Moment</c> — CLI'ın kullandığı
/// anın <b>aynısı</b>, çünkü iki farklı sabit bir gün ayrışacak iki sabittir.
/// </para>
///
/// <para>
/// <b>Gizli anahtar yanıtta yok</b> ve olmayacak. Çağıranın verdiği bir sır,
/// modelin bağlamına geri yazılırsa oradan çıkmıyor: MCP yanıtı doğrudan model
/// bağlamına giriyor ve bir ajan onu sonraki turlarda tekrar edebilir. Yanıt
/// yalnızca <b>hangi başlığın</b> imzayı taşıdığını söylüyor, değerini değil.
/// </para>
/// </summary>
/// <param name="context">Filo ve durum zemini.</param>
/// <param name="http">
/// Paylaşılan istemci. DI'dan geliyor çünkü çağrı başına
/// <c>new HttpClient()</c> soket tüketir; <c>Program.cs</c>'in tek atımlık CLI
/// yolunda bu bir sorun değil, uzun ömürlü bir MCP oturumunda oluyor.
/// </param>
public sealed class WebhookEmitTool(SimulatorMcpContext context, HttpClient http) : SimulatorTool
{
    /// <summary>Protokoldeki adı.</summary>
    public const string ToolIdentifier = "sim.webhook.emit";

    /// <inheritdoc/>
    public override string ToolName => ToolIdentifier;

    /// <inheritdoc/>
    public override string ToolTitle => "Webhook gönder";

    /// <inheritdoc/>
    public override string ToolDescription =>
        "Bir cihaz için imzalı değişiklik olayı gönderir (github/gitlab/jenkins/generic). Aynı "
        + "teslimat kimliğiyle tekrar gönderildiğinde alıcı tek kayıt oluşturmalı.";

    /// <inheritdoc/>
    public override bool IsReadOnly => false;

    /// <inheritdoc/>
    public override JsonElement InputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":   { "type": "string", "description": "Profil kimliği; olay bu cihazın hostname'ine düşer." },
            "url":      { "type": "string", "description": "POST /v1/changes/webhooks/<uç> adresi." },
            "secret":   { "type": "string", "description": "Ucun paylaşılan gizli anahtarı. Yanıtta geri dönmez." },
            "provider": { "type": "string", "enum": ["github", "gitlab", "jenkins", "generic"] },
            "delivery": { "type": "string", "description": "Teslimat kimliği; verilmezse `<cihaz>-1`." },
            "times":    { "type": "integer", "minimum": 1, "maximum": 10, "description": "Aynı teslimatı kaç kez göndersin (varsayılan 1)." }
          },
          "required": ["device", "url", "secret"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    public override JsonElement OutputSchema { get; } = McpSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "device":           { "type": "string" },
            "provider":         { "type": "string" },
            "delivery_id":      { "type": "string" },
            "signature_header": { "type": "string" },
            "body_bytes":       { "type": "integer", "minimum": 0 },
            "sends": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "attempt": { "type": "integer", "minimum": 1 },
                  "status":  { "type": "integer" },
                  "body":    { "type": "string" }
                },
                "required": ["attempt", "status", "body"],
                "additionalProperties": false
              }
            },
            "created":     { "type": "integer", "minimum": 0 },
            "idempotent":  { "type": "boolean" },
            "note":        { "type": "string" }
          },
          "required": ["device", "provider", "delivery_id", "signature_header", "body_bytes", "sends", "created", "idempotent", "note"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc/>
    protected override async ValueTask<McpToolResult> ExecuteAsync(
        McpToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var device = invocation.Required<string>("device");
        var url = invocation.Required<string>("url");
        var secret = invocation.Required<string>("secret");
        var provider = invocation.Optional("provider", WebhookProviders.GitHub)!;
        var times = invocation.Optional("times", 1);

        if (context.FindProfile(device) is not { } profile)
        {
            return McpToolResult.Failure(SimulatorErrors.UnknownDevice(device, context));
        }

        if (!WebhookProviders.All.Contains(provider, StringComparer.Ordinal))
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.InvalidArgument,
                $"'{provider}' bilinmeyen bir sağlayıcı. Bilinenler: {string.Join(", ", WebhookProviders.All)}.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = provider }));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.InvalidArgument,
                $"`url` mutlak bir http/https adresi olmalı; '{url}' verildi.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["url"] = url }));
        }

        var deliveryId = invocation.Optional<string>("delivery") ?? $"{profile.Id}-1";

        var delivery = WebhookDeliveryFactory.Create(WebhookDeliveryRequest.FromProfile(
            profile, provider, secret, deliveryId, SimulatorSamples.Moment));

        IReadOnlyList<WebhookSendResult> sent;

        try
        {
            sent = await WebhookSender
                .SendAsync(http, target, delivery, times, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            return McpToolResult.Failure(new McpToolError(
                McpToolError.Unavailable,
                $"Webhook ucuna ulaşılamadı ({target}): {error.Message}. Hiçbir teslimat gönderilmedi.",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["url"] = url }));
        }

        return McpToolResult.Structured(Shape(profile.Id, provider, deliveryId, delivery, sent));
    }

    /// <inheritdoc/>
    public override ValueTask<McpToolResult> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GERÇEK üreteç, gerçek imza — ağ yok. Üreteç saf olduğu için örnek
        // burada asıl yolun neredeyse tamamını koşturabiliyor: yalnızca
        // `WebhookSender` (tek yan etkili parça) taklit ediliyor.
        var delivery = WebhookDeliveryFactory.Create(WebhookDeliveryRequest.FromProfile(
            SimulatorSamples.Profile,
            WebhookProviders.GitHub,
            "ornek-anahtar",
            $"{SimulatorSamples.Profile.Id}-1",
            SimulatorSamples.Moment));

        return ValueTask.FromResult(McpToolResult.Structured(Shape(
            SimulatorSamples.Profile.Id,
            WebhookProviders.GitHub,
            $"{SimulatorSamples.Profile.Id}-1",
            delivery,
            [new WebhookSendResult(201, """{"duplicate":false}""")])));
    }

    private static Payload Shape(
        string device,
        string provider,
        string deliveryId,
        WebhookDelivery delivery,
        IReadOnlyList<WebhookSendResult> sent)
    {
        var created = sent.Count(r => r.Status == 201);

        return new Payload(
            device,
            provider,
            deliveryId,
            SignatureHeader(delivery),
            delivery.Body.Length,
            [.. sent.Select((r, i) => new SendPayload(i + 1, r.Status, r.Body))],
            created,

            // TEK SATIRLIK ÖLÇÜT (S07): ikinci gönderim 201 dönerse idempotans
            // kırık. `times == 1` iken kanıt YOK, ve `idempotent: true` demek
            // ölçülmemiş bir şeyi ölçülmüş göstermek olurdu — o yüzden tek
            // gönderimde de ölçüt "en fazla bir kayıt" olarak duruyor ve not
            // kanıtın kurulmadığını söylüyor.
            created <= 1,
            sent.Count > 1 ? IdempotencyMeasured : IdempotencyNotMeasured);
    }

    /// <summary>
    /// İmzayı taşıyan başlığın <b>adı</b> — değeri değil. Sağlayıcıya göre
    /// değişiyor ve modelin alıcı tarafta neye bakacağını bilmesi gerekiyor.
    /// </summary>
    private static string SignatureHeader(WebhookDelivery delivery) =>
        delivery.Headers.Keys.FirstOrDefault(name =>
            name.Contains("Signature", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase))
        ?? WebhookDeliveryFactory.DefaultSignatureHeader;

    private const string IdempotencyMeasured =
        "Aynı teslimat birden çok kez gönderildi: `created` 1'den büyükse alıcının idempotansı kırık.";

    private const string IdempotencyNotMeasured =
        "Tek gönderim yapıldı; idempotans ÖLÇÜLMEDİ. Ölçmek için `times: 2` verin — ikinci "
        + "gönderim 200 ve `duplicate: true` dönmeli, 201 değil.";

    /// <summary>Yanıt tipi — anonim nesne değil (§8).</summary>
    private sealed record Payload(
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("delivery_id")] string DeliveryId,
        [property: JsonPropertyName("signature_header")] string SignatureHeader,
        [property: JsonPropertyName("body_bytes")] int BodyBytes,
        [property: JsonPropertyName("sends")] IReadOnlyList<SendPayload> Sends,
        [property: JsonPropertyName("created")] int Created,
        [property: JsonPropertyName("idempotent")] bool Idempotent,
        [property: JsonPropertyName("note")] string Note);

    private sealed record SendPayload(
        [property: JsonPropertyName("attempt")] int Attempt,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("body")] string Body);
}
