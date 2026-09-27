using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bizigo.Contracts;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public sealed record TelemetrySqlPlan(string Sql, IReadOnlyDictionary<string, object> Parameters);

/// <summary>One query path for scope, exact time, logical deduplication and expiry.</summary>
public sealed class TelemetryReader(ClickHouseContext context, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    /// <summary>Diagnostic capture of the actual executed SQL/typed values, used by EXPLAIN verification.</summary>
    public Action<TelemetrySqlPlan>? ObserveQuery { get; set; }

    private sealed record Position(string Binding, ulong Time, string Key);
    private static string Fingerprint(TelemetryQuery query, ScopePredicate scope, bool summary) => RawSignalEnvelope.Hash(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Query = query with { Cursor = null }, scope.IsUnrestricted, scope.DeniesEverything,
            Groups = scope.Groups.Order(StringComparer.Ordinal).ToArray(), summary,
        }, RawSignalCodec.Json));

    private static Position? ReadPosition(TelemetryQuery query, ScopePredicate scope, bool summary)
    {
        if (query.Cursor is null) return null;
        try
        {
            var position = JsonSerializer.Deserialize<Position>(Convert.FromBase64String(query.Cursor), RawSignalCodec.Json);
            if (position is null || position.Binding != Fingerprint(query, scope, summary)
                || string.IsNullOrEmpty(position.Key) || position.Key.Length > 2048)
                throw new FormatException();
            return position;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new ArgumentException("Invalid telemetry cursor for this query and scope.", nameof(query)); }
    }

    private static string Cursor(TelemetryQuery query, ScopePredicate scope, bool summary, ulong timestamp, string key) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Position(Fingerprint(query, scope, summary), timestamp, key), RawSignalCodec.Json));

    public static string CreateCursor(TelemetryQuery query, ScopePredicate scope, bool summary, ulong timestamp, string key)
    {
        query.Validate();
        if (string.IsNullOrEmpty(key) || key.Length > 2048) throw new ArgumentException("Invalid continuation key.", nameof(key));
        return Cursor(query, scope, summary, timestamp, key);
    }

    private ulong NowNano() => checked((ulong)(time.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);

    private static (string Table, string Time) Names(TelemetrySignal signal) =>
        (TelemetryWriter.Table(signal), signal == TelemetrySignal.Metrics ? "ts" : "start_time");

    private TelemetrySqlPlan Plan(TelemetryQuery query, ScopePredicate scope, ulong asOf,
        string operation, bool outside = false)
    {
        query.Validate();
        var summary = operation == "summary";
        var position = ReadPosition(query, scope, summary);
        var (table, timestamp) = Names(query.Signal);
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        { ["from"] = (ulong)query.FromNano, ["as_of"] = asOf, ["take"] = query.Limit + 1 };
        var scopeSql = scope.ToSqlFragment();
        if (outside) scopeSql = scope.IsUnrestricted || scope.DeniesEverything ? "0" : "NOT (" + scopeSql + ")";
        if (scope.HasParameter) parameters["scope_groups"] = scope.ParameterValue;
        var where = new List<string> { scopeSql, "expires_nano > {as_of:UInt64}", timestamp + " >= {from:UInt64}" };
        if (query.ToNano <= ulong.MaxValue)
        { where.Add(timestamp + " < {to:UInt64}"); parameters["to"] = (ulong)query.ToNano; }
        void Filter(string column, object? value, string type = "String")
        {
            if (value is null) return;
            where.Add(column + " = {f_" + column + ":" + type + "}"); parameters["f_" + column] = value;
        }
        Filter("resource_id", query.ResourceId); Filter("metric_name", query.Name);
        Filter("service_name", query.ServiceName); Filter("kind", query.Kind);
        Filter("trace_id", query.TraceId?.ToLowerInvariant()); Filter("span_id", query.SpanId?.ToLowerInvariant());
        Filter("status", query.Status, "Int32");
        if (position is not null && operation == "list")
        {
            where.Add($"({timestamp}, logical_id) > ({{last_time:UInt64}}, {{last_id:String}})");
            parameters["last_time"] = position.Time; parameters["last_id"] = position.Key;
        }
        var select = operation switch
        {
            "count" => "count()",
            "summary" => $"summary_key, count(), min({timestamp}), max({timestamp}), argMax(record, ({timestamp}, logical_id)), argMax(record_sha256, ({timestamp}, logical_id)), argMax(record_version, ({timestamp}, logical_id))",
            _ => "record, record_sha256, record_version",
        };
        var suffix = operation == "count" ? string.Empty : operation == "summary"
            ? " GROUP BY summary_key" : $" ORDER BY {timestamp}, logical_id LIMIT {{take:Int32}}";
        if (summary)
        {
            if (position is not null)
            { suffix += " HAVING summary_key > {last_key:String}"; parameters["last_key"] = position.Key; }
            suffix += " ORDER BY summary_key LIMIT {take:Int32}";
        }
        return new($"SELECT {select} FROM {table} FINAL WHERE {string.Join(" AND ", where)}{suffix}" + Settings(), parameters);
    }

    private string Settings() => " SETTINGS max_execution_time=" +
        Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300).ToString(CultureInfo.InvariantCulture);

    private async Task ExecuteAsync(TelemetrySqlPlan plan, Func<DbDataReader, Task> consume, CancellationToken token)
    {
        ObserveQuery?.Invoke(plan);
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = plan.Sql;
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        foreach (var parameter in plan.Parameters) command.AddParameter(parameter.Key, parameter.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        await consume(reader);
    }

    private static TelemetryRecord Record(DbDataReader reader, int offset = 0)
    {
        var json = reader.GetString(offset);
        if (Convert.ToUInt16(reader.GetValue(offset + 2), CultureInfo.InvariantCulture) != 1
            || RawSignalEnvelope.Hash(Encoding.UTF8.GetBytes(json)) != reader.GetString(offset + 1))
            throw new InvalidDataException("Invalid typed telemetry checksum/version.");
        var record = JsonSerializer.Deserialize<TelemetryRecord>(json, RawSignalCodec.Json)
            ?? throw new InvalidDataException("Missing typed telemetry record.");
        TelemetryWriter.Validate(record);
        return record;
    }

    private static bool ReadFailure(Exception ex, CancellationToken token) =>
        ex is DbException or HttpRequestException or IOException or InvalidDataException or TimeoutException or JsonException or InvalidOperationException
        || ex.GetType().Namespace?.StartsWith("ClickHouse.Driver", StringComparison.Ordinal) == true
        || (ex is OperationCanceledException && !token.IsCancellationRequested);
    private static string Failure(Exception ex) => ex is JsonException or InvalidDataException
        ? "InvalidTypedRecord" : "DatabaseUnavailable";

    private async Task<TelemetryResultStatus> EmptyStatusAsync(TelemetrySignal signal, string? resource,
        ScopePredicate scope, CancellationToken token)
    {
        var result = await FeedAsync(signal, resource, scope, token);
        if (result.Status == TelemetryResultStatus.Failed) throw new IOException("Feed history unavailable.");
        return result.Count > 0 ? TelemetryResultStatus.Empty : TelemetryResultStatus.NeverFed;
    }

    public async Task<TelemetryPage> SearchAsync(TelemetryQuery query, ScopePredicate scope, CancellationToken token = default)
    {
        var plan = Plan(query, scope, NowNano(), "list");
        try
        {
            var rows = new List<TelemetryRecord>();
            var partial = false;
            var bytes = 8192; // wrapper and the bounded continuation token
            await ExecuteAsync(plan, async reader =>
            {
                while (await reader.ReadAsync(token))
                {
                    if (rows.Count == query.Limit) { partial = true; break; }
                    var row = Record(reader);
                    var size = JsonSerializer.SerializeToUtf8Bytes(row, RawSignalCodec.Json).Length + 1;
                    if (size + bytes > TelemetryQuery.MaxBytes)
                    {
                        if (rows.Count == 0) throw new ResultTooLargeException();
                        partial = true; break;
                    }
                    rows.Add(row); bytes += size;
                }
            }, token);
            var status = rows.Count > 0 ? TelemetryResultStatus.Data : await EmptyStatusAsync(query.Signal, query.ResourceId, scope, token);
            return new(status, rows, partial, partial ? Cursor(query, scope, false, rows[^1].TimeUnixNano, rows[^1].LogicalId) : null);
        }
        catch (ResultTooLargeException) { return new(TelemetryResultStatus.Failed, [], Error: "ResultTooLarge"); }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, [], Error: Failure(ex)); }
    }

    public async Task<TelemetryPage> MetricDetailAsync(string logicalId, ScopePredicate scope, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(logicalId) || logicalId.Length > 2048) throw new ArgumentException("Invalid logical ID.", nameof(logicalId));
        var parameters = new Dictionary<string, object> { ["id"] = logicalId, ["as_of"] = NowNano() };
        if (scope.HasParameter) parameters["scope_groups"] = scope.ParameterValue;
        var plan = new TelemetrySqlPlan("SELECT record, record_sha256, record_version FROM metric_points FINAL WHERE " + scope.ToSqlFragment()
            + " AND logical_id = {id:String} AND expires_nano > {as_of:UInt64} LIMIT 2" + Settings(), parameters);
        try
        {
            var rows = new List<TelemetryRecord>();
            await ExecuteAsync(plan, async reader =>
            {
                while (await reader.ReadAsync(token)) rows.Add(Record(reader));
            }, token);
            if (rows.Count > 1) throw new InvalidDataException("Conflicting logical telemetry identity.");
            if (JsonSerializer.SerializeToUtf8Bytes(rows, RawSignalCodec.Json).Length > TelemetryQuery.MaxBytes - 8192)
                return new(TelemetryResultStatus.Failed, [], Error: "ResultTooLarge");
            return new(rows.Count == 0 ? TelemetryResultStatus.Empty : TelemetryResultStatus.Data, rows);
        }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, [], Error: Failure(ex)); }
    }

    public async Task<TelemetryCount> CountAsync(TelemetryQuery query, ScopePredicate scope, bool outside = false, CancellationToken token = default)
    {
        // A count covers the whole filtered window, independently of list cursor/limit.
        query.Validate();
        var plan = Plan(query with { Cursor = null }, scope, NowNano(), "count", outside);
        try
        {
            long count = 0;
            await ExecuteAsync(plan, async reader =>
            { if (await reader.ReadAsync(token)) count = checked((long)Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture)); }, token);
            var status = count > 0 ? TelemetryResultStatus.Data : outside ? TelemetryResultStatus.Empty
                : await EmptyStatusAsync(query.Signal, query.ResourceId, scope, token);
            return new(status, count);
        }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, null, Failure(ex)); }
    }

    public async Task<TelemetryCount> CountExcludedInputsAsync(TelemetryInputWindow window, ScopePredicate scope, CancellationToken token = default)
    {
        window.Validate();
        var (table, timestamp) = Names(window.Signal);
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal) { ["as_of"] = NowNano() };
        if (scope.HasParameter) parameters["scope_groups"] = scope.ParameterValue;
        string Range(string prefix, decimal from, decimal to)
        {
            parameters[prefix + "from"] = (ulong)from;
            var lower = timestamp + " >= {" + prefix + "from:UInt64}";
            if (to > ulong.MaxValue) return "(" + lower + ")";
            parameters[prefix + "to"] = (ulong)to;
            return "(" + lower + " AND " + timestamp + " < {" + prefix + "to:UInt64})";
        }
        var outside = scope.IsUnrestricted || scope.DeniesEverything ? "0" : "NOT (" + scope.ToSqlFragment() + ")";
        var where = outside + " AND expires_nano > {as_of:UInt64} AND ("
            + Range("e", window.EventFrom, window.EventTo) + " OR " + Range("b", window.BaselineFrom, window.BaselineTo) + ")";
        if (window.SourceIds.Count > 0)
        {
            where += " AND resource_id IN {sources:Array(String)}";
            parameters["sources"] = window.SourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        var plan = new TelemetrySqlPlan("SELECT uniqExact(logical_id) FROM " + table + " FINAL WHERE " + where + Settings(), parameters);
        try
        {
            long count = 0;
            await ExecuteAsync(plan, async reader =>
            { if (await reader.ReadAsync(token)) count = checked((long)Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture)); }, token);
            return new(count == 0 ? TelemetryResultStatus.Empty : TelemetryResultStatus.Data, count);
        }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, null, Failure(ex)); }
    }

    public async Task<TelemetrySummaryPage> SummaryAsync(TelemetryQuery query, ScopePredicate scope, CancellationToken token = default)
    {
        var plan = Plan(query, scope, NowNano(), "summary");
        try
        {
            var groups = new List<TelemetrySummary>(); var partial = false; var bytes = 8192;
            await ExecuteAsync(plan, async reader =>
            {
                while (await reader.ReadAsync(token))
                {
                    if (groups.Count == query.Limit) { partial = true; break; }
                    var row = new TelemetrySummary(reader.GetString(0), checked((long)Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture)),
                        Convert.ToUInt64(reader.GetValue(2), CultureInfo.InvariantCulture), Convert.ToUInt64(reader.GetValue(3), CultureInfo.InvariantCulture), Record(reader, 4));
                    var size = JsonSerializer.SerializeToUtf8Bytes(row, RawSignalCodec.Json).Length + 1;
                    if (bytes + size > TelemetryQuery.MaxBytes)
                    {
                        if (groups.Count == 0) throw new ResultTooLargeException();
                        partial = true; break;
                    }
                    groups.Add(row); bytes += size;
                }
            }, token);
            return new(groups.Count > 0 ? TelemetryResultStatus.Data : await EmptyStatusAsync(query.Signal, query.ResourceId, scope, token),
                groups, partial, partial ? Cursor(query, scope, true, 0, groups[^1].Key) : null);
        }
        catch (ResultTooLargeException) { return new(TelemetryResultStatus.Failed, [], Error: "ResultTooLarge"); }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, [], Error: Failure(ex)); }
    }

    public async Task<TelemetryCount> FeedAsync(TelemetrySignal signal, string? resourceId, ScopePredicate scope, CancellationToken token = default)
    {
        if (!Enum.IsDefined(signal) || resourceId is { Length: > 1024 }) throw new ArgumentException("Invalid feed query.");
        var parameters = new Dictionary<string, object> { ["signal"] = (byte)signal };
        if (scope.HasParameter) parameters["scope_groups"] = scope.ParameterValue;
        var where = scope.ToSqlFragment() + " AND signal = {signal:UInt8}";
        if (resourceId is not null) { where += " AND resource_id = {resource:String}"; parameters["resource"] = resourceId; }
        try
        {
            long count = 0;
            await ExecuteAsync(new("SELECT count() FROM telemetry_feed_history FINAL WHERE " + where + Settings(), parameters),
                async reader => { if (await reader.ReadAsync(token)) count = checked((long)Convert.ToUInt64(reader.GetValue(0), CultureInfo.InvariantCulture)); }, token);
            return new(count > 0 ? TelemetryResultStatus.Data : TelemetryResultStatus.NeverFed, count);
        }
        catch (Exception ex) when (ReadFailure(ex, token)) { return new(TelemetryResultStatus.Failed, null, Failure(ex)); }
    }

    private sealed class ResultTooLargeException : Exception;
}
