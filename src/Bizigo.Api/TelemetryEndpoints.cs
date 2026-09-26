using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bizigo.Contracts;
using Bizigo.Ingest.Otlp;
using Bizigo.Ingest.Pipeline;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Bizigo.Api;

public sealed record SignalIngestStatus(
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("accepted_batches")] long AcceptedBatches,
    [property: JsonPropertyName("accepted_leaves")] long AcceptedLeaves,
    [property: JsonPropertyName("rejected_full")] long RejectedFull,
    [property: JsonPropertyName("rejected_invalid")] long RejectedInvalid,
    [property: JsonPropertyName("wal_bytes")] long WalBytes,
    [property: JsonPropertyName("corrupt_segments")] int CorruptSegments);

public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapOtlpTelemetry(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/metrics", (HttpRequest request, SignalIngest ingest, IOptions<IngestOptions> options,
            CancellationToken token) => HandleAsync(TelemetrySignal.Metrics, request, ingest, options.Value.MaxRequestBytes, token))
            .WithName("OtlpMetrics").Accepts<byte[]>(OtlpContentTypes.Protobuf, OtlpContentTypes.Json)
            .RequireAuthorization(BizigoAuthPolicies.Ingest);
        routes.MapPost("/v1/traces", (HttpRequest request, SignalIngest ingest, IOptions<IngestOptions> options,
            CancellationToken token) => HandleAsync(TelemetrySignal.Traces, request, ingest, options.Value.MaxRequestBytes, token))
            .WithName("OtlpTraces").Accepts<byte[]>(OtlpContentTypes.Protobuf, OtlpContentTypes.Json)
            .RequireAuthorization(BizigoAuthPolicies.Ingest);
        // Operational health only: no metric/span content or owner-scoped query surface.
        routes.MapGet("/internal/ingest/signals", (SignalIngest ingest) => Results.Json(new SignalIngestStatus(
            ingest.Ready, ingest.LastFailure, ingest.AcceptedBatches, ingest.AcceptedLeaves,
            ingest.RejectedFull, ingest.RejectedInvalid, ingest.Wal.TotalBytes, ingest.Wal.Recovery.CorruptSegments.Count)))
            .Produces<SignalIngestStatus>().RequireAuthorization(BizigoAuthPolicies.Ingest);
        return routes;
    }

    private static async Task<IResult> HandleAsync(TelemetrySignal signal, HttpRequest request,
        SignalIngest ingest, long limit, CancellationToken token)
    {
        var type = OtlpTelemetryDecoder.ContentType(request.ContentType);
        if (type is not (OtlpContentTypes.Protobuf or OtlpContentTypes.Json))
            return Error(415, "Unsupported Content-Type.", OtlpContentTypes.Json);
        var body = await LogsEndpoint.ReadBodyAsync(request, limit, token);
        if (body.Status != LogsEndpoint.BodyStatus.Ok)
            return Error(body.Status == LogsEndpoint.BodyStatus.TooLarge ? 413 : 400, "Invalid or oversized request body.", type);
        var result = await ingest.AcceptAsync(signal, body.Bytes, type, token);
        if (result.Status != 200)
        {
            if (result.RetryAfterSeconds > 0)
                request.HttpContext.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Error(result.Status, result.Error ?? "Telemetry request failed.", type);
        }
        IMessage response = signal == TelemetrySignal.Metrics
            ? new ExportMetricsServiceResponse
            {
                PartialSuccess = result.Rejected == 0 ? null : new ExportMetricsPartialSuccess
                { RejectedDataPoints = result.Rejected, ErrorMessage = result.Error ?? "Invalid data points." },
            }
            : new ExportTraceServiceResponse
            {
                PartialSuccess = result.Rejected == 0 ? null : new ExportTracePartialSuccess
                { RejectedSpans = result.Rejected, ErrorMessage = result.Error ?? "Invalid spans." },
            };
        return Results.Bytes(type == OtlpContentTypes.Json
            ? JsonSerializer.SerializeToUtf8Bytes(OtlpJsonCodec.Format(response)) : response.ToByteArray(), type);
    }

    private static IResult Error(int status, string message, string type)
    {
        // google.rpc.Status: message field 2; code/details are optional for OTLP/HTTP.
        byte[] bytes;
        if (type == OtlpContentTypes.Json) bytes = JsonSerializer.SerializeToUtf8Bytes(new { message });
        else
        {
            using var stream = new MemoryStream();
            using (var output = new CodedOutputStream(stream, leaveOpen: true))
            { output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString(message); }
            bytes = stream.ToArray();
        }
        return new OtlpErrorResult(status, type, bytes);
    }

    private sealed class OtlpErrorResult(int status, string contentType, byte[] bytes) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
    }
}
