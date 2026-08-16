using System.Globalization;
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
            .Accepts<byte[]>(OtlpContentTypes.Protobuf, OtlpContentTypes.Json);

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
        await request.Body.CopyToAsync(buffer, cancellationToken);

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
