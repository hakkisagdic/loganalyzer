using System.Diagnostics;
using Bizigo.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>Planner-only: pause at the real reader's SQL execution observation
/// boundary, abort an authenticated HTTP read, and verify token propagation and
/// real PG cancellation audit. This does not claim a remote CH query was killed.</summary>
[Collection(DevStackCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TelemetryCancellationIntegrationTests(DevStackFixture stack)
{
    [Fact]
    public async Task Telemetry_inflight_cancel_is_bounded_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await TelemetryDbFixture.CreateAsync(stack, ct);
        await f.SourceAsync("cancel-A", "A");
        using var ingest = f.Open(); await ingest.RecoverAsync(ct);
        await f.EmitAsync(ingest, TelemetryDbFixture.Metrics("cancel-A", f.Now, false), TelemetrySignal.Metrics);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propagated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var api = await TelemetryApiHost.StartAsync(f, ct, queryStarted: token =>
        {
            entered.TrySetResult();
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Client cancellation never reached the reader.");
            propagated.TrySetResult();
        });
        using var clientCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = api.GetCancellableAsync($"/v1/metrics/count?from_nano={f.Now - 10000000000UL}&to_nano={f.Now + 10000000000UL}", clientCancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.False(pending.IsCompleted);
        var watch = Stopwatch.StartNew(); clientCancel.Cancel();
        await propagated.Task.WaitAsync(TimeSpan.FromSeconds(1), ct);
        var propagationMs = watch.ElapsedMilliseconds;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2), ct));
        while (!await f.Db.AuditLog.AnyAsync(r => r.Subject == api.Subject("A"), ct) && watch.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(10, ct);
        var audit = Assert.Single(await f.Db.AuditLog.AsNoTracking().Where(r => r.Subject == api.Subject("A")).ToArrayAsync(ct));
        Assert.Contains("outcome=cancelled", audit.Details, StringComparison.Ordinal);
        Assert.True(propagationMs <= 1000); Assert.True(watch.Elapsed <= TimeSpan.FromSeconds(2));
        f.Reader.ObserveQuery = null;
        TelemetryDbFixture.Evidence("s04-inflight-cancel", new { propagationMs, elapsedMs = watch.ElapsedMilliseconds, audit });
    }
}
