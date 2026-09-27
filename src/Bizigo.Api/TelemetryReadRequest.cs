using System.Globalization;
using System.Text.Json;
using Bizigo.Contracts;

namespace Bizigo.Api;

public sealed record TelemetryReadRequest(TelemetryQuery Query, string Binding, string Route)
{
    private sealed record Continuation(string Binding, string Inner);
    public string? Wrap(string? inner) => inner is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Continuation(Binding, inner)));

    public static TelemetryReadRequest Parse(IQueryCollection input, TelemetrySignal signal, string route, AccessScope scope,
        string? traceId = null, string? spanId = null)
    {
        var page = route is "list" or "trace" or "summary";
        var allowed = new HashSet<string>(["from_nano", "to_nano", "owner_groups"], StringComparer.Ordinal);
        if (page) allowed.UnionWith(["limit", "cursor"]);
        if (route != "point") allowed.Add("resource_id");
        if (route is "list" or "summary" or "count")
        {
            allowed.UnionWith(["name", "service_name", "kind"]);
            if (signal == TelemetrySignal.Traces) allowed.UnionWith(["trace_id", "span_id", "status"]);
        }
        if (route is "trace" or "span") allowed.Add("service_name");
        if (input.Any(p => !allowed.Contains(p.Key) || (p.Key != "owner_groups" && p.Value.Count != 1)))
            throw new ArgumentException("Unknown or repeated telemetry query parameter.");
        string? Get(string key) => input.TryGetValue(key, out var value) ? value[0] : null;
        decimal Nano(string key)
        {
            var value = Get(key);
            if (value is null || value.Length is 0 or > 20 || value.Any(c => c is < '0' or > '9')
                || !decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException("Required integral decimal-string nanosecond bounds.");
            return number;
        }
        int Int(string key, int fallback)
        {
            var value = Get(key);
            if (value is null) return fallback;
            if (value.Length == 0 || value.Any(c => c is < '0' or > '9') || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
                throw new ArgumentException("Invalid integer query parameter.");
            return result;
        }
        var kind = Get("kind");
        var kinds = signal == TelemetrySignal.Metrics ? new[] { "Gauge", "Sum", "Histogram", "ExponentialHistogram", "Summary" }
            : ["Unspecified", "Internal", "Server", "Client", "Producer", "Consumer"];
        if (kind is not null && !kinds.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException("Invalid signal kind.");
        var query = new TelemetryQuery
        {
            Signal = signal, FromNano = Nano("from_nano"), ToNano = Nano("to_nano"), ResourceId = Get("resource_id"),
            Name = Get("name"), ServiceName = Get("service_name"), Kind = kind,
            TraceId = traceId ?? Get("trace_id"), SpanId = spanId ?? Get("span_id"),
            Status = Get("status") is null ? null : Int("status", 0), Limit = page ? Int("limit", 100) : 1,
            OwnerGroups = input["owner_groups"].Select(v => v ?? "").ToArray(),
        };
        query.Validate();
        var binding = RawSignalEnvelope.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Route = route, Query = query, scope.IsUnrestricted, scope.Subject,
            Groups = scope.OwnerGroups.Order(StringComparer.Ordinal).ToArray(),
        }, RawSignalCodec.Json));
        var cursor = Get("cursor");
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 4096) throw new FormatException();
                var continuation = JsonSerializer.Deserialize<Continuation>(Convert.FromBase64String(cursor));
                if (continuation is null || continuation.Binding != binding || string.IsNullOrEmpty(continuation.Inner)) throw new FormatException();
                query = query with { Cursor = continuation.Inner };
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            { throw new ArgumentException("Invalid telemetry cursor for this route and scope."); }
        }
        return new(query, binding, route);
    }
}
