using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public sealed record TopologyRepairTableIdentity(string Name, string Uuid, string Engine,
    string SortingKey, string CreateQuerySha256);

/// <summary>
/// Reads live Atomic-database identities; a PG certificate by itself cannot
/// attest a ClickHouse table after an out-of-band drop/recreate or downgrade.
/// </summary>
public sealed class TopologyRepairSchemaInspector(ClickHouseContext context)
{
    public static IReadOnlyList<string> CanonicalNames { get; } = Array.AsReadOnly(new[]
    {
        "topology_edges_observed", "topology_span_conflicts",
        "topology_parent_resolution", "topology_edge_lifecycle",
    });

    private static readonly IReadOnlyDictionary<string, (string[] Names, string[] Types)> ReadyShapes =
        new Dictionary<string, (string[], string[])>(StringComparer.Ordinal)
        {
            ["topology_edges_observed"] =
            (
                [.. TopologyObservedRowDigest.FrozenColumns, "physical_row_sha256"],
                ["LowCardinality(String)", "LowCardinality(String)", "LowCardinality(String)",
                 "String", "String", "LowCardinality(String)", "UInt8", "LowCardinality(String)",
                 "Float32", "FixedString(64)", "String", "String", "String",
                 "FixedString(64)", "FixedString(64)", "FixedString(64)", "FixedString(64)",
                 "String", "String", "Int64", "Int64", "Int64", "Int64", "Int64", "Int64",
                 "UInt64", "UInt64", "UInt64", "UInt64", "Array(String)", "Array(String)",
                 "Array(String)", "Decimal(21,0)", "Decimal(21,0)", "Decimal(21,0)",
                 "Decimal(21,0)", "FixedString(64)", "UInt64", "DateTime('UTC')", "UInt8",
                 "FixedString(64)" ]
            ),
            ["topology_span_conflicts"] =
            (["semantic_anchor", "first_fingerprint", "conflicting_fingerprint", "candidate_context_json", "publication_seq"],
             ["FixedString(64)", "FixedString(64)", "FixedString(64)", "String", "UInt64"]),
            ["topology_parent_resolution"] =
            (["child_anchor", "child_fingerprint", "reason", "captured_context_json", "publication_seq"],
             ["FixedString(64)", "FixedString(64)", "LowCardinality(String)", "String", "UInt64"]),
            ["topology_edge_lifecycle"] =
            (["child_owner_group", "parent_owner_group", "from_node_id", "to_node_id",
              "first_seen", "last_seen", "parent_event_time_nano", "child_event_time_nano",
              "edge_id", "publication_seq", "expires_nano", "physical_row_sha256"],
             ["LowCardinality(String)", "LowCardinality(String)", "String", "String",
              "UInt64", "UInt64", "UInt64", "UInt64", "FixedString(64)", "UInt64",
              "Decimal(21,0)", "FixedString(64)"]),
        };

    public async Task<string> ReadDatabaseUuidAsync(CancellationToken cancellationToken)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT toString(uuid) FROM system.databases WHERE name = currentDatabase()";
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var uuid = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!Guid.TryParse(uuid, out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException("Topology repair requires an Atomic ClickHouse database UUID.");
        return parsed.ToString("D");
    }

    public async Task<IReadOnlyList<TopologyRepairTableIdentity>> ReadCanonicalAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, toString(uuid), engine, sorting_key, create_table_query
            FROM system.tables
            WHERE database = currentDatabase()
              AND name IN ('topology_edges_observed', 'topology_span_conflicts',
                           'topology_parent_resolution', 'topology_edge_lifecycle')
            ORDER BY name
            """;
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var identities = new List<TopologyRepairTableIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var uuid = reader.GetString(1);
            var engine = reader.GetString(2);
            var key = reader.GetString(3);
            var create = reader.GetString(4);
            if (!CanonicalNames.Contains(name, StringComparer.Ordinal)
                || !Guid.TryParse(uuid, out var parsed) || parsed == Guid.Empty
                || key.Length == 0 || create.Length == 0)
                throw new InvalidDataException("Topology repair canonical schema is incomplete.");
            identities.Add(new(name, parsed.ToString("D"), engine, key,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(create))).ToLowerInvariant()));
        }
        if (identities.Count != CanonicalNames.Count
            || identities.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != CanonicalNames.Count)
            throw new InvalidDataException("Topology repair canonical table set is incomplete.");
        return identities;
    }

    /// <summary>
    /// The pre-cutover canonical tables may have the unsafe old engines. Only
    /// call this after a four-table exchange, or when checking a Ready cert.
    /// system.tables.engine alone cannot distinguish a versioned from an
    /// unversioned ReplacingMergeTree.
    /// </summary>
    public async Task<bool> HasVersionedObservedEngineAsync(CancellationToken cancellationToken)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, create_table_query FROM system.tables
            WHERE database = currentDatabase()
              AND name IN ('topology_edges_observed', 'topology_span_conflicts',
                           'topology_parent_resolution', 'topology_edge_lifecycle')
            """;
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var compact = string.Concat(reader.GetString(1).Where(static character => !char.IsWhiteSpace(character)));
            if (!seen.Add(name)) return false;
            if (name == "topology_edges_observed")
            {
                if (!compact.Contains("ReplacingMergeTree(publication_seq)", StringComparison.OrdinalIgnoreCase)
                    || !compact.Contains("TTLttl_at", StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!ReadyShapes.ContainsKey(name)
                     || compact.Contains("TTL", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return seen.Count == CanonicalNames.Count;
    }

    public async Task<bool> HasReadyColumnShapesAsync(CancellationToken cancellationToken)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table, name, type FROM system.columns
            WHERE database = currentDatabase()
              AND table IN ('topology_edges_observed', 'topology_span_conflicts',
                            'topology_parent_resolution', 'topology_edge_lifecycle')
            ORDER BY table, position
            """;
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var actual = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (!ReadyShapes.ContainsKey(name)) return false;
            if (!actual.TryGetValue(name, out var columns))
                actual[name] = columns = [];
            if (columns.Count >= 64) return false;
            columns.Add((reader.GetString(1), NormalizeType(reader.GetString(2))));
        }
        foreach (var (table, expected) in ReadyShapes)
        {
            if (!actual.TryGetValue(table, out var columns) || columns.Count != expected.Names.Length
                || expected.Types.Length != expected.Names.Length) return false;
            for (var ordinal = 0; ordinal < columns.Count; ordinal++)
                if (columns[ordinal].Name != expected.Names[ordinal]
                    || columns[ordinal].Type != NormalizeType(expected.Types[ordinal])) return false;
        }
        return true;
    }

    private static string NormalizeType(string value) =>
        string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    public async Task<IReadOnlyDictionary<string, string>> ReadCopyUuidsAsync(
        TopologyRepairCopyTables copies, CancellationToken cancellationToken)
    {
        var names = new[] { copies.Observed, copies.Conflicts, copies.ParentResolutions, copies.Lifecycle };
        if (names.Length != CanonicalNames.Count) throw new InvalidDataException("Repair copy count differs.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        for (var index = 0; index < names.Length; index++)
        {
            var canonical = CanonicalNames[index];
            var prefix = canonical + "_copy_";
            var name = names[index];
            if (!name.StartsWith(prefix, StringComparison.Ordinal)
                || name.Length != prefix.Length + 32
                || name[prefix.Length..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Repair copy name is invalid.");
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT toString(uuid) FROM system.tables "
                + "WHERE database = currentDatabase() AND name = {table:String}";
            command.AddParameter("table", name);
            command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
            var raw = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!Guid.TryParse(raw, out var parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("Repair copy UUID is missing.");
            result.Add(canonical, parsed.ToString("D"));
        }
        if (result.Values.Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("Repair copy UUIDs are not distinct.");
        return result;
    }

    public async Task<IReadOnlySet<string>> ReadRetainedTopologyUuidsAsync(CancellationToken cancellationToken)
    {
        await using var connection = context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT toString(uuid) FROM system.tables
            WHERE database = currentDatabase() AND name LIKE 'topology_%'
            LIMIT 4097
            """;
        command.CommandTimeout = Math.Clamp(context.Options.QueryTimeoutSeconds, 1, 300);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (result.Count == 4096)
                throw new InvalidDataException("Topology repair audit table identity cap exceeded.");
            var uuid = reader.GetString(0);
            if (!Guid.TryParse(uuid, out var parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("Topology repair audit table identity is invalid.");
            result.Add(parsed.ToString("D"));
        }
        return result;
    }
}
