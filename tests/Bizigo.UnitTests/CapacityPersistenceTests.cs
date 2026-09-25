using System.Text.Json;
using Bizigo.Capacity;

namespace Bizigo.UnitTests;

public sealed class CapacityPersistenceTests
{
    [Fact]
    public async Task Atomic_failure_never_replaces_existing_record_and_paths_are_safe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "capacity-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CapacityRunStore(directory);
            var path = await store.WriteAsync("existing", new { Value = 1 }, TestContext.Current.CancellationToken);
            var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            var failed = new CapacityRunStore(directory, _ => throw new IOException("after fsync"));
            await Assert.ThrowsAsync<IOException>(() => failed.WriteAsync("new", new { Value = 2 }, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, "new.json")));
            await Assert.ThrowsAsync<IOException>(() => store.WriteAsync("existing", new { Value = 3 }, TestContext.Current.CancellationToken));
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync("../escape", new { }, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Published_capacity_links_to_three_existing_pass_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "capacity-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CapacityRunStore(directory);
            var references = new List<CapacityAttemptReference>();
            for (var i = 0; i < 3; i++)
            {
                var (a, m, o) = CapacityEvaluationTests.Fixture("run-" + i);
                var record = new CapacityAttemptRecord(a, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, m, o, [],
                    CapacityEvaluation.Evaluate(a, m, o, null), null);
                var path = await store.SaveAttemptAsync(record, TestContext.Current.CancellationToken);
                references.Add(new(a.RunId, 10, CapacityVerdict.Pass, path));
            }
            var summary = new CapacityDiscoveryResult("discovery", CapacityVerdict.Pass, "fixture", 10, null,
                references, references.Select(r => r.RunId).ToArray());
            await store.SaveDiscoveryAsync(summary, TestContext.Current.CancellationToken);
            var reopened = JsonSerializer.Deserialize<CapacityDiscoveryResult>(await File.ReadAllTextAsync(
                Path.Combine(directory, "discovery-discovery.json"), TestContext.Current.CancellationToken), CapacityJson.Options)!;
            Assert.Equal(10, reopened.CapacityEps);
            Assert.Equal(3, reopened.ConfirmingRunIds.Count);
            File.Delete(references[0].Path);
            await Assert.ThrowsAsync<FileNotFoundException>(() => store.SaveDiscoveryAsync(summary with { DiscoveryId = "missing" }, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, "missing-discovery.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
}
