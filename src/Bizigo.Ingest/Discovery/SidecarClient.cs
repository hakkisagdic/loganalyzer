using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Bizigo.Ingest.Discovery;

/// <param name="Id">Çağıranın verdiği kimlik; yanıtta aynen döner.</param>
/// <param name="Text">Maskelenmemiş ham gövde.</param>
public sealed record MineMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text);

public sealed record MineRequest(
    [property: JsonPropertyName("source_key")] string SourceKey,
    [property: JsonPropertyName("messages")] IReadOnlyList<MineMessage> Messages);

public sealed record MineResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("template_id")]
    public string? TemplateId { get; init; }

    [JsonPropertyName("template")]
    public string? Template { get; init; }

    [JsonPropertyName("is_new")]
    public bool IsNew { get; init; }

    /// <summary>Sidecar'ın maskelediği metin — yerel imzayla karşılaştırılıyor.</summary>
    [JsonPropertyName("masked")]
    public string Masked { get; init; } = string.Empty;
}

public sealed record MineResponse
{
    [JsonPropertyName("api_version")]
    public string ApiVersion { get; init; } = string.Empty;

    [JsonPropertyName("masks_version")]
    public int MasksVersion { get; init; }

    [JsonPropertyName("cluster_count")]
    public int ClusterCount { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<MineResult> Results { get; init; } = [];
}

/// <summary>Sonuç ya da neden başarısız olduğu — çağıran devre kesiciyi buna göre sürer.</summary>
public sealed record SidecarOutcome(MineResponse? Response, string? Error, bool TimedOut, bool VersionMismatch)
{
    public static SidecarOutcome Ok(MineResponse response) => new(response, null, false, false);

    public static SidecarOutcome Failed(string error, bool timedOut = false) =>
        new(null, error, timedOut, false);

    public static SidecarOutcome Incompatible(string error) => new(null, error, false, true);
}

/// <summary>
/// Sidecar'ın HTTP istemcisi (F1 §9).
///
/// <para>
/// <b>Hiçbir koşulda istisna fırlatmıyor.</b> Bu bilinçli: çağıran keşif
/// işçisi ve orada yakalanmayan tek bir istisna işçiyi düşürür, keşif sessizce
/// ölür. Hatalar <see cref="SidecarOutcome"/> ile veri olarak dönüyor.
/// </para>
///
/// <para>
/// <c>IHttpClientFactory</c> kullanılmıyor: tek bir sabit adrese konuşan tek
/// bir uzun ömürlü istemci var, DNS değişimi <c>PooledConnectionLifetime</c>
/// ile karşılanıyor. Fabrika bir paket bağımlılığı daha getirirdi ve burada
/// karşılığı yok.
/// </para>
/// </summary>
public sealed class SidecarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SidecarOptions _options;

    public SidecarClient(SidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = options.Timeout,
        })
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = options.Timeout,
        };
    }

    /// <summary>Test edilebilirlik için: hazır bir <see cref="HttpClient"/> ile.</summary>
    public SidecarClient(SidecarOptions options, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _http = http;
        _http.Timeout = options.Timeout;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public Task<SidecarOutcome> MineAsync(MineRequest request, CancellationToken cancellationToken) =>
        PostAsync("v1/mine/batch", request, cancellationToken);

    public Task<SidecarOutcome> MatchAsync(MineRequest request, CancellationToken cancellationToken) =>
        PostAsync("v1/mine/match", request, cancellationToken);

    private async Task<SidecarOutcome> PostAsync(
        string path,
        MineRequest request,
        CancellationToken cancellationToken)
    {
        // İki iptal kaynağı: kapanış ve 2 sn sözleşmesi. `HttpClient.Timeout`
        // tek başına yetmiyor çünkü gövdenin okunması ayrı bir aşama.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);

        try
        {
            using var response = await _http.PostAsJsonAsync(path, request, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SidecarOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content
                .ReadFromJsonAsync<MineResponse>(deadline.Token)
                .ConfigureAwait(false);

            if (body is null)
            {
                return SidecarOutcome.Failed("Boş yanıt gövdesi.");
            }

            if (!string.Equals(body.ApiVersion, _options.ApiVersion, StringComparison.Ordinal))
            {
                return SidecarOutcome.Incompatible(
                    $"Sözleşme sürümü uyuşmuyor: beklenen '{_options.ApiVersion}', gelen '{body.ApiVersion}'.");
            }

            if (_options.MasksVersion > 0 && body.MasksVersion != _options.MasksVersion)
            {
                // Farklı maske sürümü = farklı imza = yanlış `template_id`.
                return SidecarOutcome.Incompatible(
                    $"Maskeleme sözlüğü sürümü uyuşmuyor: beklenen {_options.MasksVersion}, gelen {body.MasksVersion}.");
            }

            return SidecarOutcome.Ok(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SidecarOutcome.Failed($"Zaman aşımı ({_options.Timeout.TotalSeconds:0.#} sn).", timedOut: true);
        }
        catch (OperationCanceledException)
        {
            return SidecarOutcome.Failed("İptal edildi (kapanış).");
        }
        catch (Exception ex)
        {
            return SidecarOutcome.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
