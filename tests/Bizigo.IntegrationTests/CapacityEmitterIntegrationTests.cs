using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Bizigo.Capacity;
using Bizigo.Simulators;

namespace Bizigo.IntegrationTests;

/// <summary>
/// B01'in duvar saatli tek ölçümü: gerçek TCP soketi, iki bağlantı ve toplu
/// yazma. Birim testleri hükmü sınar; istenen ↔ gerçekleşen EPS sapmasını ancak
/// bu yol gösterebilir.
/// </summary>
public sealed class CapacityEmitterIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Gercek_sokette_hedefe_ulasiyor_ve_manifest_yaziyor()
    {
        const int connections = 2;
        const int count = 400;
        var token = TestContext.Current.CancellationToken;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepts = Enumerable.Range(0, connections)
                .Select(_ => listener.AcceptTcpClientAsync(token).AsTask())
                .ToArray();

            var profile = LoadProfile();
            profile.Syslog!.Transport = "tcp";

            var emitTask = CapacityEmitter.EmitAsync(
                profile,
                DevStackSetup.RepoPath("."),
                IPAddress.Loopback.ToString(),
                new CapacityEmitOptions
                {
                    RunId = "b01-real-socket",
                    Pace = new PaceProfile.Fixed(200, TimeSpan.FromSeconds(5)),
                    MaxLines = count,
                    Connections = connections,
                    BatchSize = 16,
                    PayloadMode = CapacityPayloadMode.Tagged,
                    Port = port,
                    GeneratorOnSameHost = true,
                },
                token);

            var clients = await Task.WhenAll(accepts);
            var received = new ConcurrentBag<string>();

            var readers = clients.Select(async client =>
            {
                using (client)
                using (var reader = new StreamReader(client.GetStream()))
                {
                    while (await reader.ReadLineAsync(token) is { } line)
                    {
                        received.Add(line);
                    }
                }
            });

            var result = await emitTask;
            await Task.WhenAll(readers);

            Assert.Equal(count, result.Transport.Lines);
            Assert.Equal(count, received.Count);
            Assert.Equal(count, result.Manifest.Expected);
            Assert.Equal(count, result.Manifest.Digests.Count);
            Assert.Equal(GeneratorVerdict.Attained, result.Manifest.Attainment.Verdict);
            Assert.InRange(
                result.Manifest.Attainment.Ratio!.Value,
                GeneratorAttainment.MinimumAttainment,
                2.0 - GeneratorAttainment.MinimumAttainment);

            Assert.Contains(received, static line =>
                line.Contains("bizigo_run_id=\"b01-real-socket\"", StringComparison.Ordinal)
                && line.Contains("bizigo_seq=", StringComparison.Ordinal)
                && line.Contains("bizigo_send_ns=", StringComparison.Ordinal));
        }
        finally
        {
            listener.Stop();
        }
    }

    private static SimulatorProfile LoadProfile()
    {
        var results = SimulatorProfileStore.LoadAll(
            DevStackSetup.RepoPath("catalog/simulators"),
            DevStackSetup.RepoPath("."));

        var match = Assert.Single(results, static result => result.Profile.Id == "fw-ankara-01");
        Assert.Empty(match.Errors);
        return match.Profile;
    }
}
