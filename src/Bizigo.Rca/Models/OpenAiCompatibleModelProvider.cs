using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Bizigo.Rca.Models;

/// <summary>
/// OpenAI-uyumlu <c>/chat/completions</c> sağlayıcısı — K6'nın adını koyduğu
/// soyutlama.
///
/// <para>
/// <b>Ollama, vLLM ve kurum içi GPU kümesi için ayrı sınıf yok</b> ve olmamalı:
/// üçü de aynı sözleşmeyi konuşuyor, farkları base URL ve model adı. Üç sınıf
/// yazılsaydı K6'nın kapısı üç kez çağrılırdı ve üçüncüsü unutulurdu.
/// </para>
///
/// <para>
/// <b>Anahtar ortam değişkeninden okunuyor</b>, yapılandırmadan değil —
/// simülatör profillerinin <c>credential_env</c> disiplininin aynısı ve aynı
/// gerekçeyle: alanın kendisi olsaydı bir gün birinin oraya gerçek bir anahtar
/// yazması an meselesiydi.
/// </para>
///
/// <para>
/// <b>Hata bir istisna değil bir sonuç.</b> Uç kapalıysa, zaman aşımına
/// uğradıysa ya da gövde okunamadıysa koşum düşmüyor — <c>Failure</c> dolu bir
/// <see cref="ModelCompletion"/> dönüyor. Sebep RCA'nın kendi dürüstlük
/// satırı: "model cevap vermedi" ile "model boş cevap verdi" farklı cümleler
/// ve ikisi de rapora yazılabilmeli.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleModelProvider(
    HttpClient http,
    ILogger<OpenAiCompatibleModelProvider> logger,
    TimeProvider? timeProvider = null) : IModelProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Name => "openai-compatible";

    public async ValueTask<ModelCompletion> CompleteAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = request.Endpoint;
        var started = _time.GetTimestamp();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));

        var body = new ChatRequest(
            endpoint.Model,
            [
                new ChatMessage("system", request.System.Text),
                new ChatMessage("user", request.User.Text),
            ]);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(endpoint.BaseUri, "chat/completions"))
        {
            Content = JsonContent.Create(body, options: Json),
        };

        if (endpoint.ApiKeyEnvironmentVariable is { Length: > 0 } variable
            && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } key)
        {
            message.Headers.Authorization = new("Bearer", key);
        }

        try
        {
            using var response = await http.SendAsync(message, cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Basarisiz(
                    started,
                    $"Uç {(int)response.StatusCode} döndü.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync<ChatResponse>(Json, cts.Token)
                .ConfigureAwait(false);

            var text = payload?.Choices?.FirstOrDefault()?.Message?.Content;

            if (text is null)
            {
                // "Gövde okunamadı" ile "model boş cevap verdi" ayrı cümleler.
                return Basarisiz(started, "Uç beklenen gövdeyi döndürmedi.");
            }

            return new ModelCompletion(
                text,
                payload?.Usage?.PromptTokens,
                payload?.Usage?.CompletionTokens,
                _time.GetElapsedTime(started),
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Model ucu {Endpoint} {Seconds} sn içinde cevap vermedi.", endpoint.Name, endpoint.TimeoutSeconds);
            return Basarisiz(started, $"Uç {endpoint.TimeoutSeconds} sn içinde cevap vermedi.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Model ucu {Endpoint} erişilemedi.", endpoint.Name);
            return Basarisiz(started, $"Uca erişilemedi: {ex.Message}");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Model ucu {Endpoint} okunamayan gövde döndürdü.", endpoint.Name);
            return Basarisiz(started, $"Gövde okunamadı: {ex.Message}");
        }
    }

    private ModelCompletion Basarisiz(long started, string failure) =>
        new(string.Empty, null, null, _time.GetElapsedTime(started), failure);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
