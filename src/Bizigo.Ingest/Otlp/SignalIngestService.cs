using Bizigo.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bizigo.Ingest.Otlp;

public sealed class SignalIngestService(SignalIngest ingest, IOptions<SignalOptions> options,
    ILogger<SignalIngestService> logger, ITopologyObservedRepairReadiness readiness) : BackgroundService
{
    private readonly ITopologyObservedRepairReadiness repairReadiness =
        readiness ?? throw new ArgumentNullException(nameof(readiness));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The HTTP host may serve declared-only reads during maintenance, but
        // no observed recovery/consumer starts before the durable repair gate.
        // This startup wait does not replace per-publication phase/lock checks.
        await RetryAsync(async ct => { await repairReadiness.RequireReadyAsync(ct); }, stoppingToken);
        await RetryAsync(ingest.RecoverAsync, stoppingToken);
        await Task.WhenAll(ConsumeAsync(stoppingToken), RetainAsync(stoppingToken));
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in ingest.Reader.ReadAllAsync(stoppingToken))
        {
            await RetryAsync(ct => ingest.ProcessAsync(work, ct), stoppingToken);
            ingest.CompleteWork();
        }
    }

    private async Task RetainAsync(CancellationToken stoppingToken)
    {
        // A full WAL rejects new work. Retention must make progress even when
        // the reader is idle or the consumer is retrying a different envelope.
        using var timer = new PeriodicTimer(options.Value.RetryInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await ingest.SweepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { ingest.SetFailure(ex); logger.LogError(ex, "Signal WAL retention failed; segments retained."); }
        }
    }

    private async Task RetryAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        while (true)
        {
            try { await operation(token); return; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ingest.SetFailure(ex);
                logger.LogError(ex, "Signal ingest/replay failed; durable envelope retained for retry.");
                await Task.Delay(options.Value.RetryInterval, token);
            }
        }
    }
}
