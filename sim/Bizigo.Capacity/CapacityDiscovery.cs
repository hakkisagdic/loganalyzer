namespace Bizigo.Capacity;

public sealed record CapacityAttemptReference(string RunId, int EventsPerSecond, CapacityVerdict Verdict, string Path);

public sealed record CapacityDiscoveryResult(
    string DiscoveryId, CapacityVerdict Verdict, string Reason, int? VerifiedLowerEps,
    int? FailedUpperEps, IReadOnlyList<CapacityAttemptReference> Attempts,
    IReadOnlyList<string> ConfirmingRunIds)
{
    public int SchemaVersion => 1;
    public int? CapacityEps => Verdict == CapacityVerdict.Pass ? VerifiedLowerEps : null;
    public bool UpperBoundaryFound => FailedUpperEps is not null;
}

public sealed class CapacityDiscovery(ICapacityAttemptRunner runner, ICapacityRunStore store,
    TimeProvider? timeProvider = null)
{
    public const int RequiredPasses = 3;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public static string AttemptId(string discoveryId, int ordinal) => $"{discoveryId}-{ordinal:D6}";

    public async Task<CapacityDiscoveryResult> DiscoverAsync(CapacityOptions options,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var id = Guid.NewGuid().ToString("N");
        var attempts = new List<CapacityAttemptReference>();
        var confirming = new List<string>();
        int? lower = null, upper = null;
        var rate = options.MinimumEps;

        async Task<CapacityDiscoveryResult> Finish(CapacityVerdict verdict, string reason)
        {
            var result = new CapacityDiscoveryResult(id, verdict, reason, lower, upper, attempts.ToArray(),
                verdict == CapacityVerdict.Pass ? confirming.ToArray() : []);
            await store.SaveDiscoveryAsync(result, CancellationToken.None);
            return result;
        }

        while (true)
        {
            var streak = new List<string>();
            for (var repetition = 0; repetition < RequiredPasses; repetition++)
            {
                if (attempts.Count >= options.AttemptBudget)
                    return await Finish(CapacityVerdict.Inconclusive, "Attempt budget exhausted.");
                var attempt = new CapacityAttempt(AttemptId(id, attempts.Count + 1), rate, options);
                var started = time.GetUtcNow();
                CapacityAttemptRecord record;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    record = await runner.RunAsync(attempt, cancellationToken);
                    if (record.Attempt != attempt)
                        throw new InvalidDataException("Runner returned an unrelated attempt.");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    var verdict = ex is OperationCanceledException ? CapacityVerdict.Aborted : CapacityVerdict.Inconclusive;
                    record = new(attempt, started, time.GetUtcNow(), null, null, [],
                        new(verdict, CapacityGap.Unknown, ex.Message), ex.Message);
                }

                string path;
                try { path = await store.SaveAttemptAsync(record, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return await Finish(CapacityVerdict.Inconclusive, "Attempt persistence failed: " + ex.Message);
                }
                attempts.Add(new(attempt.RunId, rate, record.Decision.Verdict, path));
                if (record.Decision.Verdict is CapacityVerdict.Inconclusive or CapacityVerdict.Aborted)
                    return await Finish(record.Decision.Verdict, record.Decision.Reason);
                if (record.Decision.Verdict == CapacityVerdict.Fail)
                {
                    upper = rate;
                    break;
                }
                streak.Add(attempt.RunId);
            }

            if (streak.Count == RequiredPasses)
            {
                lower = rate;
                confirming = streak;
                if (rate == options.MaximumEps)
                    return await Finish(CapacityVerdict.Pass, "Configured maximum verified; physical ceiling not measured.");
            }
            if (lower is null) return await Finish(CapacityVerdict.Fail, "Minimum rate failed; no verified capacity.");
            if (upper is not null)
            {
                if ((long)upper.Value - lower.Value <= options.ResolutionEps)
                    return await Finish(CapacityVerdict.Pass, "Verified lower bound within configured resolution.");
                rate = lower.Value + (upper.Value - lower.Value) / 2;
            }
            else rate = (int)Math.Min(options.MaximumEps, (long)rate * 2);
        }
    }
}
