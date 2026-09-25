using System.Text.Json;
using Bizigo.Capacity;

// Process-only acceptance fixture, not shipped in the product CLI.
if (args[0] == "write")
{
    var store = new CapacityRunStore(args[1], args.Length > 3 ? temporary =>
    {
        File.WriteAllText(args[3], temporary);
        Thread.Sleep(Timeout.Infinite);
    } : null);
    await store.WriteAsync(args[2], new { Evidence = "complete", Value = 42 });
}
else if (args[0] == "read")
{
    foreach (var file in Directory.GetFiles(args[1], "*.json"))
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Console.WriteLine(document.RootElement.GetProperty("Value").GetInt32());
    }
}
else if (args[0] == "cancel")
{
    var options = JsonSerializer.Deserialize<CapacityProcessOptions>(await File.ReadAllTextAsync(args[1]), CapacityJson.Options)!;
    using var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(TimeSpan.FromMilliseconds(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)));
    var record = await new CapacityProcessRunner(options).RunAsync(new("cancel-fixture", options.Discovery.MinimumEps, options.Discovery), cancellation.Token);
    await new CapacityRunStore(options.OutputDirectory).SaveAttemptAsync(record, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(record, CapacityJson.Options));
}
else throw new ArgumentException("Unknown fixture mode.");
