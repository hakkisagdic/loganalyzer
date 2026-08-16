using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.Utility;

namespace Bizigo.Storage.ClickHouse;

public sealed record AppliedMigration(string Version, string Checksum);

public sealed record MigrationResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> AlreadyApplied);

/// <summary>
/// <c>db/clickhouse/NNNN_ad.sql</c> dosyalarını sırayla uygular ve
/// <c>schema_migrations</c> tablosunda izler.
///
/// EF Core kullanılmıyor: ClickHouse'un DDL'i (ORDER BY, PARTITION BY, skip index,
/// CODEC) ilişkisel göç araçlarına sığmıyor ve şemanın kendisi karar belgesinin
/// parçası — elle yazılmış SQL okunabilir olmalı.
/// </summary>
public sealed class ClickHouseMigrator
{
    private const string MigrationsTable = "schema_migrations";
    private readonly ClickHouseContext _context;

    public ClickHouseMigrator(ClickHouseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<MigrationResult> MigrateAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await EnsureMigrationsTableAsync(connection, cancellationToken);
        var known = await LoadAppliedAsync(connection, cancellationToken);

        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.sql").OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : [];

        var applied = new List<string>();
        var alreadyApplied = new List<string>();

        foreach (var file in files)
        {
            var version = Path.GetFileNameWithoutExtension(file);
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var checksum = Checksum(content);

            if (known.TryGetValue(version, out var existing))
            {
                if (!string.Equals(existing, checksum, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Göç '{version}' uygulandıktan sonra değiştirilmiş. " +
                        "Uygulanmış göç dosyaları düzenlenmez — yeni bir göç dosyası ekleyin.");
                }

                alreadyApplied.Add(version);
                continue;
            }

            foreach (var statement in SqlStatementSplitter.Split(content))
            {
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await RecordAsync(connection, version, checksum, cancellationToken);
            applied.Add(version);
        }

        return new MigrationResult(applied, alreadyApplied);
    }

    private static async Task EnsureMigrationsTableAsync(
        ClickHouseConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {MigrationsTable}
            (
                version    String,
                checksum   String,
                applied_at DateTime64(3, 'UTC') DEFAULT now64(3)
            )
            ENGINE = MergeTree
            ORDER BY version
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> LoadAppliedAsync(
        ClickHouseConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT version, argMax(checksum, applied_at) AS checksum
            FROM {MigrationsTable}
            GROUP BY version
            """;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task RecordAsync(
        ClickHouseConnection connection,
        string version,
        string checksum,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO {MigrationsTable} (version, checksum) VALUES ({{v:String}}, {{c:String}})";
        command.AddParameter("v", version);
        command.AddParameter("c", checksum);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Checksum(string content)
    {
        // Satır sonu farkı (CRLF/LF) göç sürüklenmesi sayılmasın.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Yalnızca teşhis için: uygulanmış göçleri döner.</summary>
    public async Task<IReadOnlyList<AppliedMigration>> GetAppliedAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureMigrationsTableAsync(connection, cancellationToken);

        var map = await LoadAppliedAsync(connection, cancellationToken);
        return map
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AppliedMigration(kv.Key, kv.Value))
            .ToArray();
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"ClickHouseMigrator({MigrationsTable})");
}
