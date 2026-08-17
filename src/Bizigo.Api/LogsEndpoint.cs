using System.Globalization;
using System.IO.Compression;
using Bizigo.Ingest.Pipeline;
using Microsoft.Extensions.Options;

namespace Bizigo.Api;

/// <summary>
/// OTLP/HTTP giriş ucu (F1 §2.1). <b>Tek</b> ingest arayüzü — protokol
/// çeşitliliğinin tamamı collector'ın işi.
/// </summary>
public static class LogsEndpoint
{
    public static IEndpointRouteBuilder MapOtlpLogs(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/v1/logs", HandleAsync)
            .WithName("OtlpLogs")
            .Accepts<byte[]>(OtlpContentTypes.Protobuf, OtlpContentTypes.Json)
            // T09: uç artık anonim değil. Collector servis hesabıyla geliyor ve
            // rolü YALNIZCA `ingest` — kimlik sızarsa veri yazılabilir, OKUNAMAZ.
            // Rol ayrımının tek sebebi bu (F1 §10.1.2).
            .RequireAuthorization(BizigoAuthPolicies.Ingest);

        return routes;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        IngestGateway gateway,
        IOptions<IngestOptions> options,
        CancellationToken cancellationToken)
    {
        var limit = options.Value.MaxRequestBytes;

        // Content-Length yalan söyleyebilir; okuma sınırı asıl koruma.
        if (request.ContentLength > limit)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        using var buffer = new MemoryStream();

        var encoding = request.Headers.ContentEncoding.ToString().Trim();
        if (encoding.Length == 0 || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            await request.Body.CopyToAsync(buffer, cancellationToken);
        }
        else if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            // OTLP/HTTP dışa aktarıcısı **varsayılan olarak** gzip gönderiyor,
            // yani bu dal istisna değil olağan yol. Açılmadığı sürece gövde
            // protobuf gibi görünüp `InvalidProtocolBufferException` veriyor ve
            // hata mesajı yükün sıkıştırılmış olduğunu hiç söylemiyor —
            // uçtan uca ilk denemede tam olarak böyle kaybedildi.
            await using var gzip = new GZipStream(request.Body, CompressionMode.Decompress);
            await CopyBoundedAsync(gzip, buffer, limit, cancellationToken);
        }
        else
        {
            return Results.BadRequest(new { error = $"Desteklenmeyen Content-Encoding: '{encoding}'." });
        }

        if (buffer.Length > limit)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var result = await gateway.AcceptAsync(
            buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
            request.ContentType,
            cancellationToken);

        return result.Outcome switch
        {
            IngestOutcome.Accepted => Results.Ok(new { accepted = result.RecordCount }),
            IngestOutcome.Invalid => Results.BadRequest(new { error = result.Message }),
            IngestOutcome.Full => Throttled(request.HttpContext.Response, result),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Sınırı <b>açılmış</b> boyut üzerinden uygular. <c>Content-Length</c>
    /// sıkıştırılmış boyutu söylüyor, dolayısıyla tek başına koruma değil: küçük
    /// bir gzip gövdesi açıldığında gigabaytlara çıkabilir (zip bomb).
    /// </summary>
    private static async Task CopyBoundedAsync(
        Stream source,
        MemoryStream destination,
        long limit,
        CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        int read;

        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            destination.Write(chunk, 0, read);

            if (destination.Length > limit)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 503 + <c>Retry-After</c>. Collector'ın <c>file_storage</c> kalıcı kuyruğu
    /// bunu görünce bekletip yeniden dener — kendi kuyruğumuzu yazmıyoruz (K5).
    /// Başlık olmadan collector kendi geri çekilme aralığını kullanır ve yığın
    /// toparlanmaya çalışırken üstüne yüklenmeye devam eder.
    /// </summary>
    private static IResult Throttled(HttpResponse response, IngestResult result)
    {
        response.Headers.RetryAfter =
            result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return Results.Json(
            new { error = result.Message },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

public static class OtlpContentTypes
{
    public const string Protobuf = "application/x-protobuf";
    public const string Json = "application/json";
}
