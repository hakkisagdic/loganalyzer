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
        var body = await ReadBodyAsync(request, limit, cancellationToken);

        switch (body.Status)
        {
            case BodyStatus.TooLarge:
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            case BodyStatus.UnsupportedEncoding:
                return Results.BadRequest(new
                {
                    error = $"Desteklenmeyen Content-Encoding: '{body.Encoding}'.",
                });
        }

        var result = await gateway.AcceptAsync(
            body.Bytes,
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

    public enum BodyStatus
    {
        Ok,
        TooLarge,
        UnsupportedEncoding,
    }

    /// <param name="Bytes">Açılmış gövde. <see cref="BodyStatus.Ok"/> dışında anlamsız.</param>
    /// <param name="Encoding">Gelen <c>Content-Encoding</c>; hata mesajı için.</param>
    public readonly record struct BodyRead(BodyStatus Status, ReadOnlyMemory<byte> Bytes, string Encoding);

    /// <summary>
    /// İstek gövdesini okur, gerekiyorsa <b>açar</b> ve sınırı açılmış boyut
    /// üzerinden uygular.
    ///
    /// <para>
    /// <b>gzip olağan yol, istisna değil:</b> OTLP/HTTP dışa aktarıcısı varsayılan
    /// olarak sıkıştırıyor. Açılmadığında gövde protobuf sanılıp
    /// <c>InvalidProtocolBufferException</c> veriyor ve hata mesajı sıkıştırmadan
    /// hiç bahsetmiyor — uçtan uca ilk denemede veri tam olarak böyle kayboldu.
    /// </para>
    ///
    /// <para>
    /// Sınır <c>Content-Length</c>'e güvenmiyor: o sıkıştırılmış boyutu söylüyor
    /// ve küçük bir gzip gövdesi açıldığında sınırsız büyüyebilir (zip bomb).
    /// </para>
    ///
    /// <para>
    /// <b>Neden public:</b> uç davranışının bu parçası tek başına sınanabilir
    /// olmalı; aksi halde ancak koşan bir yığınla test edilebilirdi ve zaten
    /// öyle olduğu için gözden kaçmıştı.
    /// </para>
    /// </summary>
    public static async Task<BodyRead> ReadBodyAsync(
        HttpRequest request,
        long limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var encoding = request.Headers.ContentEncoding.ToString().Trim();

        // Content-Length yalan söyleyebilir; okuma sınırı asıl koruma. Yine de
        // apaçık büyük bir istekte gövdeyi hiç okumamak ucuz.
        if (request.ContentLength > limit)
        {
            return new BodyRead(BodyStatus.TooLarge, default, encoding);
        }

        var identity = encoding.Length == 0
            || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase);

        if (!identity && !encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return new BodyRead(BodyStatus.UnsupportedEncoding, default, encoding);
        }

        using var buffer = new MemoryStream();

        if (identity)
        {
            await CopyBoundedAsync(request.Body, buffer, limit, cancellationToken);
        }
        else
        {
            await using var gzip = new GZipStream(request.Body, CompressionMode.Decompress);
            await CopyBoundedAsync(gzip, buffer, limit, cancellationToken);
        }

        return buffer.Length > limit
            ? new BodyRead(BodyStatus.TooLarge, default, encoding)
            : new BodyRead(BodyStatus.Ok, buffer.ToArray(), encoding);
    }

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
