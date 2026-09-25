using System.CommandLine;
using System.Text.Json;
using Bizigo.Capacity;
using Bizigo.Simulators;

namespace Bizigo.Cli;

public static class CapacityCommandHandlers
{
    public static Command Create()
    {
        var config = new Option<FileInfo>("--config") { Required = true, Description = "Capacity JSON configuration." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Validate and print the plan without starting children." };
        var auto = new Command("auto", "Discover a verified zero-loss lower bound with three consecutive repeats.");
        auto.Options.Add(config);
        auto.Options.Add(dryRun);
        auto.SetAction((parse, token) => RunAsync(parse.GetValue(config)!.FullName, parse.GetValue(dryRun), token));
        var capacity = new Command("capacity", "External-process capacity discovery (B03–B05).");
        capacity.Subcommands.Add(auto);
        return capacity;
    }

    public static async Task<int> RunAsync(string configPath, bool dryRun, CancellationToken token)
    {
        try
        {
            var options = JsonSerializer.Deserialize<CapacityProcessOptions>(await File.ReadAllTextAsync(configPath, token), CapacityJson.Options)
                ?? throw new ArgumentException("Empty configuration.");
            options.Validate();
            var profiles = SimulatorProfileStore.LoadAll(Path.Combine(options.RepositoryRoot, "catalog", "simulators"), options.RepositoryRoot);
            var profile = profiles.SingleOrDefault(p => p.Profile.Id == options.Profile);
            if (profile is null || profile.Errors.Count != 0 || profile.Profile.Syslog is null)
                throw new ArgumentException("Missing or invalid simulator syslog profile.");
            if (dryRun)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    DryRun = true, RequiredPasses = CapacityDiscovery.RequiredPasses, Configuration = options,
                    Generator = options.Generator(new("DRY-RUN", options.Discovery.MinimumEps, options.Discovery), "<attempt>/manifest.json"),
                    CapacityEps = (int?)null,
                }, CapacityJson.Options));
                return 0;
            }

            var discovery = new CapacityDiscovery(new CapacityProcessRunner(options), new CapacityRunStore(options.OutputDirectory));
            var result = await discovery.DiscoverAsync(options.Discovery, token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Verdict = result.Verdict.ToString().ToUpperInvariant(), result.CapacityEps,
                result.Reason, result.VerifiedLowerEps, result.FailedUpperEps, result.UpperBoundaryFound,
                result.DiscoveryId, result.ConfirmingRunIds, result.Attempts,
            }, CapacityJson.Options));
            return result.Verdict switch
            {
                CapacityVerdict.Pass => 0, CapacityVerdict.Fail => 1,
                CapacityVerdict.Inconclusive => 2, CapacityVerdict.Aborted => 3, _ => 4,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine("Capacity configuration/persistence error: " + ex.Message);
            return 4;
        }
    }
}
