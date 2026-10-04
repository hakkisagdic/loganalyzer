using System.Globalization;
using Bizigo.ControlPlane;
using Bizigo.Query;
using Bizigo.Storage.ClickHouse;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Cli;

/// <summary>
/// Explicit operator path for existing-data 0014 maintenance. The CLI records
/// a human drain assertion; it never infers old-writer quiescence from a PG
/// advisory lock, an environment boolean, or an empty ClickHouse query.
/// </summary>
internal static class TopologyRepairCommandHandlers
{
    public static async Task<int> AttestAsync(string subject, string statement, DateTimeOffset validUntil,
        string? postgresConnection, string? clickHouseConnection, CancellationToken cancellationToken)
    {
        if (!TryCreateRunner(postgresConnection, clickHouseConnection, out var runner, out var storage)) return 2;
        using (storage)
        {
            try
            {
                var generation = await runner.RecordOperatorDrainAsync(subject, statement, validUntil,
                    cancellationToken);
                Console.WriteLine($"topology repair drain attested for generation {generation}");
                return 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Console.Error.WriteLine("Topology drain attestation was not recorded; verify identities and phase.");
                return 3;
            }
        }
    }

    public static async Task<int> ResumeAsync(string? postgresConnection, string? clickHouseConnection,
        CancellationToken cancellationToken)
    {
        if (!TryCreateRunner(postgresConnection, clickHouseConnection, out var runner, out var storage)) return 2;
        using (storage)
        {
            var result = await runner.InitializeAsync(TopologyRepairStartMode.OperatorResume, cancellationToken);
            Console.WriteLine($"topology repair: {result.Status}; generation={result.Generation?.ToString(CultureInfo.InvariantCulture) ?? "none"}; "
                + $"code={result.DiagnosticCode ?? "none"}");
            return result.Status switch
            {
                TopologyRepairInitializationStatus.Ready => 0,
                TopologyRepairInitializationStatus.MaintenanceRequired => 3,
                TopologyRepairInitializationStatus.RepairIncomplete => 4,
                TopologyRepairInitializationStatus.Busy => 5,
                _ => 4,
            };
        }
    }

    private static bool TryCreateRunner(string? postgresConnection, string? clickHouseConnection,
        out TopologyPublicationRepairRunner runner, out ClickHouseContext storage)
    {
        postgresConnection ??= Environment.GetEnvironmentVariable("BIZIGO_CONTROLPLANE");
        clickHouseConnection ??= Environment.GetEnvironmentVariable("BIZIGO_CLICKHOUSE");
        if (string.IsNullOrWhiteSpace(postgresConnection) || string.IsNullOrWhiteSpace(clickHouseConnection))
        {
            Console.Error.WriteLine("Both PostgreSQL and ClickHouse connection strings are required.");
            runner = null!;
            storage = null!;
            return false;
        }
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(postgresConnection).Options;
        var factory = new SingleContextFactory(options);
        storage = new ClickHouseContext(new ClickHouseOptions { ConnectionString = clickHouseConnection });
        var readiness = new TopologyObservedRepairReadiness(factory, storage);
        runner = new TopologyPublicationRepairRunner(factory, storage, readiness);
        return true;
    }
}
