using System.Globalization;
using Bizigo.Contracts;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// <c>change_events</c> okuma yolu. F3'teki <c>change.feed</c> kanıt sağlayıcısının
/// veri kaynağı; F1'de yalnızca yazılıp okunabildiği doğrulanıyor.
/// </summary>
public sealed class ChangeEventReader(ClickHouseContext context)
{
    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    private ClickHouseOptions _options => _context.Options;

    /// <summary>
    /// Kapsam <b>dışında</b> kaç değişiklik olduğunu sayar, içeriğini
    /// döndürmez (K17, RCA §3.2) — olay tarafındaki
    /// <see cref="EventReader.CountOutOfScopeAsync"/>'in ikizi.
    ///
    /// <para>
    /// Kanıt sağlayıcısının bu sayıya ihtiyacı var çünkü alternatifi sessiz bir
    /// yalan: sayamadığı için <c>0</c> dönen bir sağlayıcı, rapora "kapsamınız
    /// dışında ilişkili değişiklik yok" cümlesini kurdurur. Kök neden başka
    /// grubun cihazındaki bir config değişikliğiyse, rapor bunu <b>bilmeden</b>
    /// yanlış sonuca varır.
    /// </para>
    /// </summary>
    public async Task<long> CountOutOfScopeAsync(
        ChangeQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Sınırsız kapsamda "dışarısı" yok.
        if (scope.IsUnrestricted)
        {
            return 0;
        }

        var conditions = new List<string>
        {
            "ts >= {ts_from:DateTime64(3)} AND ts < {ts_to:DateTime64(3)}",
            scope.DeniesEverything ? "1" : "NOT (" + scope.ToSqlFragment() + ")",
        };

        if (query.TargetIds.Count > 0)
        {
            conditions.Add("target_id IN ({targets:Array(String)})");
        }

        if (query.ChangeKinds.Count > 0)
        {
            conditions.Add("change_kind IN ({kinds:Array(String)})");
        }

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count() FROM {_options.ChangeEventsTable} WHERE {string.Join(" AND ", conditions)}";
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        command.AddParameter("ts_from", query.From.UtcDateTime);
        command.AddParameter("ts_to", query.To.UtcDateTime);

        if (!scope.DeniesEverything && scope.HasParameter)
        {
            command.AddParameter("scope_groups", scope.ParameterValue);
        }

        if (query.TargetIds.Count > 0)
        {
            command.AddParameter("targets", query.TargetIds.ToArray());
        }

        if (query.ChangeKinds.Count > 0)
        {
            command.AddParameter("kinds", query.ChangeKinds.ToArray());
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ChangeEvent>> SearchAsync(
        ChangeQuery query,
        ScopePredicate scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (scope.DeniesEverything)
        {
            return [];
        }

        var conditions = new List<string>
        {
            "ts >= {ts_from:DateTime64(3)} AND ts < {ts_to:DateTime64(3)}",
            scope.ToSqlFragment(),
        };

        if (query.TargetIds.Count > 0)
        {
            conditions.Add("target_id IN ({targets:Array(String)})");
        }

        if (query.ChangeKinds.Count > 0)
        {
            conditions.Add("change_kind IN ({kinds:Array(String)})");
        }

        if (query.OwnerGroups.Count > 0)
        {
            conditions.Add("owner_group IN ({narrow:Array(String)})");
        }

        var limit = Math.Clamp(query.Limit, 1, _options.MaxRowsPerQuery);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT ts, change_id, owner_group, toUInt8(target_kind) AS target_kind_num,
                   target_id, change_kind, actor, summary,
                   mapKeys(details) AS detail_keys, mapValues(details) AS detail_values,
                   source, external_ref
            FROM {_options.ChangeEventsTable}
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY ts DESC
            LIMIT {limit}
            """;
        command.CommandTimeout = _options.QueryTimeoutSeconds;

        command.AddParameter("ts_from", query.From.UtcDateTime);
        command.AddParameter("ts_to", query.To.UtcDateTime);
        if (scope.HasParameter)
        {
            command.AddParameter("scope_groups", scope.ParameterValue);
        }

        if (query.TargetIds.Count > 0)
        {
            command.AddParameter("targets", query.TargetIds.ToArray());
        }

        if (query.ChangeKinds.Count > 0)
        {
            command.AddParameter("kinds", query.ChangeKinds.ToArray());
        }

        if (query.OwnerGroups.Count > 0)
        {
            command.AddParameter("narrow", query.OwnerGroups.ToArray());
        }

        var results = new List<ChangeEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var keys = (string[])reader["detail_keys"];
            var values = (string[])reader["detail_values"];
            var details = new Dictionary<string, string>(keys.Length, StringComparer.Ordinal);
            for (var i = 0; i < keys.Length && i < values.Length; i++)
            {
                details[keys[i]] = values[i];
            }

            results.Add(new ChangeEvent
            {
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind((DateTime)reader["ts"], DateTimeKind.Utc)),
                ChangeId = (Guid)reader["change_id"],
                OwnerGroup = (string)reader["owner_group"],
                TargetKind = (ChangeTargetKind)Convert.ToByte(reader["target_kind_num"], CultureInfo.InvariantCulture),
                TargetId = (string)reader["target_id"],
                ChangeKind = (string)reader["change_kind"],
                Actor = (string)reader["actor"],
                Summary = (string)reader["summary"],
                Details = details,
                Source = (string)reader["source"],
                ExternalRef = (string)reader["external_ref"],
            });
        }

        return results;
    }
}
