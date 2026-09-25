namespace Bizigo.Capacity;

public static class CapacityEvaluation
{
    public static CapacityGap Classify(int expected, IEnumerable<int>? observed)
    {
        if (expected <= 0 || observed is null) return CapacityGap.Unknown;
        var found = observed.ToHashSet();
        if (found.Count == 0 || found.Any(i => i < 0 || i >= expected)) return CapacityGap.Unknown;
        var missing = Enumerable.Range(0, expected).Where(i => !found.Contains(i)).ToArray();
        if (missing.Length == 0) return CapacityGap.None;
        if (missing[^1] - missing[0] + 1 != missing.Length) return CapacityGap.Scattered;
        if (missing[0] == 0) return CapacityGap.Head;
        if (missing[^1] == expected - 1) return CapacityGap.Tail;
        return CapacityGap.Contiguous;
    }

    public static CapacityDecision Evaluate(CapacityAttempt attempt, CapacityRunManifest? manifest,
        CapacityObservation? observation, CapacityDecision? safetyStop)
    {
        if (safetyStop is not null) return safetyStop;
        CapacityDecision Unknown(string reason) => new(CapacityVerdict.Inconclusive, CapacityGap.Unknown, reason);
        if (manifest is null || observation is null) return Unknown("Missing manifest or observation.");
        var ledger = observation.Ledger;
        if (manifest.RunId != attempt.RunId || observation.RunId != attempt.RunId
            || ledger.RunId != attempt.RunId || observation.Target != attempt.Options.Target)
            return Unknown("Manifest/ledger/observation identity does not match attempt.");
        if (manifest.Expected <= 0 || manifest.Expected != manifest.Attainment.Emitted
            || manifest.Expected != ledger.Expected || manifest.Digests.Distinct(StringComparer.Ordinal).Count() != manifest.Expected)
            return Unknown("Empty or inconsistent manifest/ledger expected counts.");
        if (attempt.Options.GeneratorLocation == "unknown" || manifest.GeneratorOnSameHost is null
            || manifest.GeneratorOnSameHost != (attempt.Options.GeneratorLocation == "same"))
            return Unknown("Generator location unknown or inconsistent.");
        if (manifest.Attainment.Verdict != GeneratorVerdict.Attained
            || manifest.Attainment.RequestedEventsPerSecond is not { } rate
            || !double.IsFinite(rate)
            || Math.Abs(rate - attempt.EventsPerSecond) > Math.Max(1e-6, attempt.EventsPerSecond * .001)
            || manifest.Attainment.Elapsed.TotalSeconds < attempt.Options.DurationSeconds
            || manifest.Attainment.AchievedEventsPerSecond is not { } achieved || !double.IsFinite(achieved)
            || achieved / rate < GeneratorAttainment.MinimumAttainment
            || Math.Abs(achieved - manifest.Expected / manifest.Attainment.Elapsed.TotalSeconds) > 1e-6)
            return Unknown("GENERATOR-LIMITED: target rate/duration was not attained.");
        if (observation.Archived is null || observation.Searchable is null)
            return Unknown("Missing archive/sequence identity evidence.");

        // Foreign runs can be returned by a window probe; they never enter this run's counts.
        var archived = observation.Archived.Where(i => i.RunId == attempt.RunId).ToArray();
        var searchable = observation.Searchable.Where(i => i.RunId == attempt.RunId).ToArray();
        bool Invalid(CapacityIdentity i) => i.Sequence < 0 || i.Sequence >= manifest.Expected
            || !string.Equals(i.Digest, manifest.Digests[i.Sequence], StringComparison.Ordinal);
        if (archived.Any(Invalid) || searchable.Any(Invalid)) return Unknown("Invalid sequence/digest identity evidence.");
        if (ledger.ProductArchived.Value != archived.Select(i => i.Sequence).Distinct().Count()
            || ledger.ProductSearchable.Value != searchable.Length)
            return Unknown("Ledger counts disagree with independent run identity evidence.");
        if (new[] { ledger.WireDrops, ledger.CollectorAccepted, ledger.CollectorRefused,
                ledger.ProductArchived, ledger.ProductSearchable }.Any(r => r.Value is < 0))
            return Unknown("Negative ledger reading.");
        if (ledger.Verdict is LedgerVerdict.Limited or LedgerVerdict.Uncertain)
            return Unknown("LEDGER-LIMITED: " + ledger.Rationale);
        var gap = Classify(manifest.Digests.Count, searchable.Select(i => i.Sequence));
        if (gap != CapacityGap.None || archived.Length != manifest.Expected
            || searchable.Length != manifest.Expected
            || archived.Select(i => i.Sequence).Distinct().Count() != archived.Length
            || searchable.Select(i => i.Sequence).Distinct().Count() != searchable.Length
            || ledger.Verdict != LedgerVerdict.Consistent)
            return new(CapacityVerdict.Fail, gap, "Zero-loss or duplicate invariant failed.");
        if (observation.LatencyMilliseconds is not { } latency || !double.IsFinite(latency) || latency < 0)
            return Unknown("Latency SLO measurement unavailable.");
        return latency > attempt.Options.MaxLatencyMilliseconds
            ? new(CapacityVerdict.Fail, gap, "Latency SLO exceeded.")
            : new(CapacityVerdict.Pass, gap, "Attained, consistent, zero loss and SLO satisfied.");
    }
}
