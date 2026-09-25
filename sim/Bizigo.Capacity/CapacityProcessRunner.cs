using System.Globalization;
using System.Text.Json;

namespace Bizigo.Capacity;

public sealed record CapacityCommand(string FileName, string[] Arguments)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FileName);
        ArgumentNullException.ThrowIfNull(Arguments);
        if (Arguments.Any(a => a is null)) throw new ArgumentException("Null process argument.");
    }
}

public sealed record CapacityProcessOptions
{
    public CapacityOptions Discovery { get; init; } = new();
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5140;
    public string Transport { get; init; } = "tcp";
    public string Profile { get; init; } = "fw-ankara-01";
    public string RepositoryRoot { get; init; } = ".";
    public string SimulatorDll { get; init; } = "sim/Bizigo.Simulators/bin/Release/net10.0/Bizigo.Simulators.dll";
    public string Dotnet { get; init; } = "dotnet";
    public string OutputDirectory { get; init; } = "capacity-runs";
    public CapacityCommand Probe { get; init; } = new("", []);

    public void Validate()
    {
        Discovery.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(Profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(Dotnet);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputDirectory);
        if (Port is <= 0 or > 65535 || Transport is not ("tcp" or "udp"))
            throw new ArgumentException("Invalid port or transport.");
        if (!Directory.Exists(RepositoryRoot) || !File.Exists(SimulatorDll))
            throw new ArgumentException("Repository root or built simulator DLL does not exist.");
        Probe.Validate();
    }

    public CapacityCommand Generator(CapacityAttempt attempt, string manifestPath) => new(Dotnet,
        [Path.GetFullPath(SimulatorDll), "--profile", Profile, "--repo", Path.GetFullPath(RepositoryRoot),
        "--host", Host, "--port", Port.ToString(CultureInfo.InvariantCulture), "--transport", Transport,
        "--pace", "fixed", "--eps", attempt.EventsPerSecond.ToString(CultureInfo.InvariantCulture),
        "--duration", Discovery.DurationSeconds.ToString(CultureInfo.InvariantCulture),
        "--run-id", attempt.RunId, "--manifest", manifestPath, "--payload", "tagged",
        "--generator-location", Discovery.GeneratorLocation]);
}

public sealed record CapacityProbeRequest(int SchemaVersion, string Phase, CapacityAttempt Attempt,
    string Host, int Port, string Transport, string ManifestPath, DateTimeOffset StartedAt, int? GeneratorPid);
public sealed record CapacityProbeResponse(CapacitySafetySample? Sample, CapacityObservation? Observation);

public sealed class CapacityProcessRunner(CapacityProcessOptions options) : ICapacityAttemptRunner
{
    public async Task<CapacityAttemptRecord> RunAsync(CapacityAttempt attempt, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var safety = new CapacitySafety(attempt);
        var samples = new List<CapacitySafetySample>();
        CapacityRunManifest? manifest = null;
        CapacityObservation? observation = null;
        CapacityDecision? forced = null;
        string? missing = null;
        int? pid = null;
        CapacityChild? generator = null;
        var work = Path.Combine(Path.GetFullPath(options.OutputDirectory), ".work", attempt.RunId);
        var manifestPath = Path.Combine(work, "manifest.json");

        async Task Probe(string phase, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(attempt.Options.Safety.ProbeTimeoutSeconds));
            var request = new CapacityProbeRequest(1, phase, attempt, options.Host, options.Port,
                options.Transport, manifestPath, started, pid);
            try
            {
                await using var child = await CapacityChild.StartAsync(options.Probe,
                    JsonSerializer.Serialize(request, CapacityJson.Options), deadline.Token);
                var json = await child.OutputAsync(deadline.Token);
                var response = JsonSerializer.Deserialize<CapacityProbeResponse>(json, CapacityJson.Options)
                    ?? throw new InvalidDataException("Empty probe response.");
                if (response.Sample is null) safety.Missing("Probe did not return safety sample.");
                else
                {
                    samples.Add(response.Sample);
                    safety.Add(response.Sample, DateTimeOffset.UtcNow);
                }
                if (phase == "final") observation = response.Observation;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { safety.Missing("Probe timeout."); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            { safety.Missing("Probe failed: " + ex.Message); }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(work);
            await Probe("baseline", cancellationToken);
            if (safety.Stop is null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(attempt.Options.GeneratorTimeoutSeconds));
                generator = await CapacityChild.StartAsync(options.Generator(attempt, manifestPath), null, deadline.Token);
                pid = generator.Id;
                try
                {
                    while (!generator.HasExited && safety.Stop is null)
                    {
                        // The generator exit races a bounded sampling delay, never the whole run.
                        await Task.WhenAny(generator.WaitAsync(deadline.Token),
                            Task.Delay(TimeSpan.FromSeconds(attempt.Options.Safety.SampleIntervalSeconds), deadline.Token));
                        deadline.Token.ThrowIfCancellationRequested();
                        safety.CheckFreshness(DateTimeOffset.UtcNow);
                        if (safety.Stop is null) await Probe("sample", deadline.Token);
                    }
                    if (safety.Stop is null)
                    {
                        // Exit 8 is the existing B01 generator-limited outcome, with an informative manifest.
                        if (generator.ExitCode is not (0 or 8))
                            throw new IOException($"Generator exited {generator.ExitCode}.");
                        manifest = JsonSerializer.Deserialize<CapacityRunManifest>(
                            await File.ReadAllTextAsync(manifestPath, deadline.Token), CapacityJson.Options)
                            ?? throw new InvalidDataException("Empty manifest.");
                        // Keep probing during the settling window so a silent probe cannot recover at the end.
                        var settleUntil = DateTimeOffset.UtcNow.AddSeconds(attempt.Options.ObservationSeconds);
                        while (DateTimeOffset.UtcNow < settleUntil && safety.Stop is null)
                        {
                            var delay = Math.Min(attempt.Options.Safety.SampleIntervalSeconds,
                                (settleUntil - DateTimeOffset.UtcNow).TotalSeconds);
                            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                            await Probe("sample", cancellationToken);
                        }
                        if (safety.Stop is null) await Probe("final", cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { forced = new(CapacityVerdict.Inconclusive, CapacityGap.Unknown, "Generator timeout."); }
            }
        }
        catch (OperationCanceledException)
        { forced = new(CapacityVerdict.Aborted, CapacityGap.Unknown, "Caller cancellation."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { forced = new(CapacityVerdict.Inconclusive, CapacityGap.Unknown, "Attempt failed: " + ex.Message); }
        finally
        {
            if (generator is not null)
            {
                try { await generator.DisposeAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { forced = new(CapacityVerdict.Inconclusive, CapacityGap.Unknown, "Generator teardown failed: " + ex.Message); }
            }
        }

        var stop = safety.Stop?.Verdict == CapacityVerdict.Aborted ? safety.Stop : forced ?? safety.Stop;
        var decision = CapacityEvaluation.Evaluate(attempt, manifest, observation, stop);
        if (manifest is null || observation is null) missing = decision.Reason;
        return new(attempt, started, DateTimeOffset.UtcNow, manifest, observation, samples, decision, missing, pid);
    }
}
