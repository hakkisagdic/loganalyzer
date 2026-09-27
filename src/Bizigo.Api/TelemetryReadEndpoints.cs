using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;
using Bizigo.Evidence;
using Bizigo.Query;

namespace Bizigo.Api;

/// <summary>Read authorization and audited IScopedQuery are the only telemetry read path.</summary>
public static class TelemetryReadEndpoints
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    public static IEndpointRouteBuilder MapTelemetryReads(this IEndpointRouteBuilder routes)
    {
        foreach (var signal in new[] { TelemetrySignal.Metrics, TelemetrySignal.Traces })
        {
            var group = routes.MapGroup(signal == TelemetrySignal.Metrics ? "/v1/metrics" : "/v1/traces")
                .RequireAuthorization(BizigoAuthPolicies.Read).WithTags("telemetry-read");
            void Common(RouteHandlerBuilder endpoint) => endpoint.ProducesProblem(400).ProducesProblem(422).ProducesProblem(503).ProducesProblem(504)
                .Produces(401).Produces(403);
            Common(group.MapGet("", (HttpContext http, IScopedQuery query, ICurrentUser user) => HandleAsync(http, query, user.Scope, signal, "list"))
                .WithName("List" + signal).Produces<TelemetryPageDto>());
            Common(group.MapGet("/count", (HttpContext http, IScopedQuery query, ICurrentUser user) => HandleAsync(http, query, user.Scope, signal, "count"))
                .WithName("Count" + signal).Produces<TelemetryCountDto>());
            Common(group.MapGet("/summary", (HttpContext http, IScopedQuery query, ICurrentUser user) => HandleAsync(http, query, user.Scope, signal, "summary"))
                .WithName("Summarize" + signal).Produces<TelemetrySummaryPageDto>());
            Common(group.MapGet("/feed", (HttpContext http, IScopedQuery query, ICurrentUser user) => HandleAsync(http, query, user.Scope, signal, "feed"))
                .WithName("Feed" + signal).Produces<TelemetryCountDto>());
            if (signal == TelemetrySignal.Metrics)
                Common(group.MapGet("/points/{**logicalId}", (string logicalId, HttpContext http, IScopedQuery query, ICurrentUser user) =>
                    HandleAsync(http, query, user.Scope, signal, "point", logicalId: Uri.UnescapeDataString(logicalId)))
                    .WithName("GetMetricPoint").Produces<TelemetryRecordDto>().ProducesProblem(404));
            else
            {
                Common(group.MapGet("/{traceId}", (string traceId, HttpContext http, IScopedQuery query, ICurrentUser user) =>
                    HandleAsync(http, query, user.Scope, signal, "trace", traceId: traceId))
                    .WithName("GetTrace").Produces<TelemetryPageDto>().ProducesProblem(404));
                Common(group.MapGet("/{traceId}/spans/{spanId}", (string traceId, string spanId, HttpContext http, IScopedQuery query, ICurrentUser user) =>
                    HandleAsync(http, query, user.Scope, signal, "span", traceId: traceId, spanId: spanId))
                    .WithName("GetSpan").Produces<TelemetryRecordDto>().ProducesProblem(404));
            }
        }
        return routes;
    }

    private static IResult Failure(int status, string reason) => Results.Problem(statusCode: status, title: reason,
        extensions: new Dictionary<string, object?> { ["status"] = "Failed", ["reason"] = reason, ["count"] = null, ["records"] = Array.Empty<object>() });
    private static IResult QueryFailure(string? reason) => Failure(reason == "ResultTooLarge" ? 422 : 503,
        reason == "ResultTooLarge" ? "RecordTooLarge" : reason ?? "QueryUnavailable");

    internal static async Task<IResult> HandleAsync(HttpContext http, IScopedQuery query, AccessScope scope, TelemetrySignal signal,
        string route, string? traceId = null, string? spanId = null, string? logicalId = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        try
        {
            var request = TelemetryReadRequest.Parse(http.Request.Query, signal, route, scope, traceId, spanId);
            var q = request.Query;
            if (route is "count" or "feed")
            {
                var result = route == "count" ? await query.CountTelemetryAsync(q, scope, token)
                    : await query.GetTelemetryFeedAsync(signal, q.ResourceId, TelemetryEvidenceReader.Narrow(scope, q.OwnerGroups ?? []), token);
                if (result.Status == TelemetryResultStatus.Failed || result.Count is null) return QueryFailure(result.Error);
                token.ThrowIfCancellationRequested();
                return Results.Json(new TelemetryCountDto(result.Status.ToString(), result.Count.Value.ToString(CultureInfo.InvariantCulture), true, null), WireOptions);
            }
            if (route == "summary")
            {
                var result = await query.SummarizeTelemetryAsync(q, scope, token);
                if (result.Status == TelemetryResultStatus.Failed) return QueryFailure(result.Error);
                var groups = new List<TelemetrySummaryDto>(); var size = 8192;
                foreach (var row in result.Groups)
                {
                    var dto = new TelemetrySummaryDto(row.Key, row.Count.ToString(CultureInfo.InvariantCulture), row.FirstNano.ToString(CultureInfo.InvariantCulture),
                        row.LastNano.ToString(CultureInfo.InvariantCulture), TelemetryWire.Record(row.Last));
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, WireOptions).Length + 1;
                    if (size + bytes > TelemetryQuery.MaxBytes) break;
                    size += bytes; groups.Add(dto);
                }
                if (groups.Count == 0 && result.Groups.Count > 0) return Failure(422, "RecordTooLarge");
                var trimmed = groups.Count < result.Groups.Count;
                var cursor = trimmed ? query.CreateTelemetryCursor(q, scope, 0, result.Groups[groups.Count - 1].Key, true) : result.Cursor;
                token.ThrowIfCancellationRequested();
                return Results.Json(new TelemetrySummaryPageDto(result.Status.ToString(), groups, result.Partial || trimmed, request.Wrap(cursor),
                    trimmed ? "ResponseBudget" : result.Partial ? "QueryBudget" : null), WireOptions);
            }
            var page = route == "point" ? await query.GetMetricPointAsync(logicalId!, TelemetryEvidenceReader.Narrow(scope, q.OwnerGroups ?? []), token)
                : route is "trace" or "span" ? await query.GetTraceAsync(q, scope, token) : await query.SearchTelemetryAsync(q, scope, token);
            if (page.Status == TelemetryResultStatus.Failed) return QueryFailure(page.Error);
            var sourceRows = route == "point" ? page.Records.Where(r => r.TimeUnixNano >= q.FromNano && r.TimeUnixNano < q.ToNano).ToArray() : page.Records;
            if (route is "point" or "span")
            {
                if (sourceRows.Count == 0) return Failure(404, "NotFound");
                if (sourceRows.Count != 1 || page.Partial) return Failure(503, "ConflictingIdentity");
                var dto = TelemetryWire.Record(sourceRows[0]);
                if (JsonSerializer.SerializeToUtf8Bytes(dto, WireOptions).Length > TelemetryQuery.MaxBytes) return Failure(422, "RecordTooLarge");
                token.ThrowIfCancellationRequested();
                return Results.Json(dto, WireOptions);
            }
            if (route == "trace" && sourceRows.Count == 0) return Failure(404, "NotFound");
            var rows = new List<TelemetryRecordDto>(); var used = 8192;
            foreach (var row in sourceRows)
            {
                var dto = TelemetryWire.Record(row);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, WireOptions).Length + 1;
                if (used + bytes > TelemetryQuery.MaxBytes) break;
                used += bytes; rows.Add(dto);
            }
            if (rows.Count == 0 && sourceRows.Count > 0) return Failure(422, "RecordTooLarge");
            var limited = rows.Count < sourceRows.Count;
            var next = limited ? query.CreateTelemetryCursor(q, scope, sourceRows[rows.Count - 1].TimeUnixNano, sourceRows[rows.Count - 1].LogicalId) : page.Cursor;
            token.ThrowIfCancellationRequested();
            return Results.Json(new TelemetryPageDto(page.Status.ToString(), rows, page.Partial || limited, request.Wrap(next),
                limited ? "ResponseBudget" : page.Partial ? "QueryBudget" : null), WireOptions);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(504, "Timeout"); }
        catch (TimeoutException) { return Failure(504, "Timeout"); }
        catch (ArgumentException) { return Failure(400, "InvalidQuery"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Failure(503, "QueryUnavailable"); }
    }
}
