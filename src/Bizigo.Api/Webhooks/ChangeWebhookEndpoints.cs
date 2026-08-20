using System.Security.Cryptography;
using System.Text;
using Bizigo.Contracts;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bizigo.Api.Webhooks;

/// <summary>
/// İmzalı değişiklik webhook'u (T24, K34).
///
/// <para>
/// <b>Bu ucun kimliği bir token değil, bir imza.</b> CI sistemleri OIDC akışı
/// yürütmüyor; GitHub'ın verdiği tek kimlik kanıtı paylaşılan anahtarla
/// hesaplanmış HMAC'tir. Uç bu yüzden <c>AllowAnonymous</c> ama
/// <b>doğrulanmamış hiçbir istek kayıt oluşturmuyor</b> — imza kontrolü gövde
/// ayrıştırılmadan önce koşuyor.
/// </para>
///
/// <para>
/// <b>Kapsam kararı — ve neden <c>AccessScope.System</c> değil:</b> yazma
/// <see cref="IScopedQuery.WriteChangeAsync"/>'den geçmek zorunda (K17) ve o
/// metot bir kapsam istiyor. Webhook'un token'ı olmadığına göre kapsam ucun
/// <b>yapılandırmasından</b> geliyor: her uç tek bir <c>owner_group</c>'a bağlı
/// ve yalnızca ona yazabiliyor. Sistem kapsamı (sınırsız) kullanılsaydı sızan
/// tek bir gizli anahtar, her ekibin zaman çizelgesine olay düşürebilirdi — ve
/// RCA'nın F3'te güvendiği kanıt tam olarak bu tablo. Kapsam kapısı burada da
/// gerçek bir kapı.
/// </para>
///
/// <para>
/// <b>UI formu bu ticket'ta yok:</b> Next.js iskeleti (T13) paralel yazılıyor.
/// Elle giriş için <c>POST /v1/changes</c> zaten var ve kapsam kapısından
/// geçiyor; form onu çağıracak.
/// </para>
/// </summary>
public static class ChangeWebhookEndpoints
{
    public static IServiceCollection AddChangeWebhooks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new ChangeWebhookOptions();
        configuration.GetSection(ChangeWebhookOptions.SectionName).Bind(options);

        services.AddSingleton(options);

        // Yapılandırma dosyasından gelen uçlar. T25'ten sonra bunlar artık
        // BİRİNCİL kaynak değil, yedek: `ControlPlaneWebhookRegistry` önce
        // ekrandan tanımlanan connector'lara bakıyor.
        services.AddSingleton(_ => new ChangeWebhookRegistry(options));

        services.AddSingleton<ChangeWebhookDeliveryLog>();

        // Ham arşiv modülü de kaydediyor; `TryAdd` ikisinin sırasından bağımsız
        // kılıyor ve bu modülün tek başına eklenmesini mümkün bırakıyor.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    public static IEndpointRouteBuilder MapChangeWebhooks(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/changes/webhooks/{endpointId}", ReceiveAsync)
            .AllowAnonymous()
            .WithName("ReceiveChangeWebhook")
            .WithTags("changes");

        return routes;
    }

    private static async Task<IResult> ReceiveAsync(
        string endpointId,
        HttpRequest request,
        IChangeWebhookRegistry registry,
        ChangeWebhookOptions options,
        IScopedQuery query,
        ChangeWebhookDeliveryLog deliveries,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var log = loggers.CreateLogger(typeof(ChangeWebhookEndpoints));
        var endpoint = await registry.FindAsync(endpointId, cancellationToken);

        // Bilinmeyen ya da pasif uç: gövde hiç okunmuyor. "Var ama kapalı"
        // ayrımı da dışarı verilmiyor.
        if (endpoint is null)
        {
            return Results.NotFound(new { error = "Bilinmeyen webhook ucu." });
        }

        var body = await LogsEndpoint.ReadBodyAsync(request, options.MaxBodyBytes, cancellationToken);

        if (body.Status != LogsEndpoint.BodyStatus.Ok)
        {
            return body.Status == LogsEndpoint.BodyStatus.TooLarge
                ? Results.StatusCode(StatusCodes.Status413PayloadTooLarge)
                : Results.BadRequest(new { error = $"Desteklenmeyen Content-Encoding: '{body.Encoding}'." });
        }

        string? Header(string name) =>
            request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

        var verdict = WebhookSignature.Verify(endpoint, Header, body.Bytes.Span);

        if (verdict != SignatureVerdict.Valid)
        {
            // Log satırı ucu ve yargıyı taşıyor; gizli anahtarı, beklenen imzayı
            // ya da gelen imzayı DEĞİL. Cevap da aynı sebeple tek cümle.
            log.LogWarning(
                "Webhook imzası reddedildi: uç={Endpoint} sağlayıcı={Provider} yargı={Verdict}",
                endpoint.Id, endpoint.Provider, verdict);

            return verdict == SignatureVerdict.NotConfigured
                ? Results.StatusCode(StatusCodes.Status500InternalServerError)
                : Results.Json(new { error = "İmza doğrulanamadı." }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var mapped = ChangeWebhookMapper.Map(endpoint, Header, body.Bytes.Span, clock);

        if (mapped.Outcome == WebhookMapOutcome.Invalid)
        {
            return Results.BadRequest(new { error = mapped.Reason });
        }

        if (mapped.Outcome == WebhookMapOutcome.Ignored)
        {
            // 202: istek doğruydu, bu bildirim bir değişiklik değil. 4xx dönmek
            // GitHub'ın webhook'u kırmızı işaretlemesine yol açardı.
            return Results.Accepted(value: new { ignored = true, reason = mapped.Reason });
        }

        var change = mapped.Change!;
        var deliveryKey = DeliveryKey(endpoint, mapped.DeliveryId, body.Bytes.Span);

        var claim = await deliveries.ClaimAsync(
            new ChangeWebhookDeliveryEntity
            {
                DeliveryKey = deliveryKey,
                EndpointId = endpoint.Id,
                Provider = endpoint.Provider,
                OwnerGroup = endpoint.OwnerGroup,
                ChangeId = change.ChangeId,
                ReceivedAt = clock.GetUtcNow(),
            },
            cancellationToken);

        if (!claim.Claimed)
        {
            // 200, 409 değil: sağlayıcı için bu bir hata değil, zaten istenen
            // sonuç. 409 gören GitHub webhook'u kırmızı işaretler.
            return Results.Ok(new { change_id = claim.ChangeId, duplicate = true });
        }

        try
        {
            // Webhook'un kapsamı ucun yapılandırmasından — sınıf yorumundaki
            // gerekçeye bakın.
            var scope = AccessScope.ForGroups($"webhook:{endpoint.Id}", [endpoint.OwnerGroup]);

            await query.WriteChangeAsync(change, scope, cancellationToken);
        }
        catch (Exception)
        {
            // Talep duruyor ama değişiklik yazılamadı: kaydı geri alıyoruz ki
            // sağlayıcının yeniden denemesi sessizce "mükerrer" sayılmasın. Aksi
            // halde tek bir geçici ClickHouse hatası olayı KALICI olarak
            // kaybettirirdi ve T24'ün bütün değeri o kaydın var olmasında.
            //
            // İptal jetonu bilerek `None`: istemci bağlantıyı kesmiş olsa bile
            // geri alma koşmalı.
            try
            {
                await deliveries.ReleaseAsync(deliveryKey, CancellationToken.None);
            }
            catch (Exception release)
            {
                // Geri almanın kendi hatası ASIL hatanın yerine geçmemeli —
                // F1'de `ClickHouseEventSink.DisposeAsync` tam olarak böyle
                // açılış hatasını yutmuştu.
                log.LogError(release, "Webhook teslimat talebi geri alınamadı: {Key}", deliveryKey);
            }

            throw;
        }

        return Results.Created(
            $"/v1/changes/{change.ChangeId}",
            new { change_id = change.ChangeId, delivery_key = deliveryKey });
    }

    /// <summary>
    /// <c>{uç}:{teslimat kimliği}</c>. Sağlayıcı bir kimlik vermiyorsa gövdenin
    /// sha256'sı kullanılıyor — yani idempotans, yapılandırma olmadan da
    /// "aynı gövde iki kez" düzeyinde çalışıyor.
    /// </summary>
    internal static string DeliveryKey(
        ChangeWebhookEndpoint endpoint,
        string deliveryId,
        ReadOnlySpan<byte> body)
    {
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            // Uzun bir teslimat kimliği kolonu taşırmasın: kısaltma yerine
            // hash'liyoruz, çünkü kırpma iki farklı kimliği aynı yapabilirdi.
            var id = deliveryId.Trim();

            return id.Length <= 200
                ? $"{endpoint.Id}:{id}"
                : $"{endpoint.Id}:h:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))}";
        }

        return $"{endpoint.Id}:b:{Convert.ToHexStringLower(SHA256.HashData(body))}";
    }
}
