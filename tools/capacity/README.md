# Capacity discovery (B03–B05)

From the repository root:

```sh
bash tools/machine-resources.sh check
dotnet build Bizigo.sln --configuration Release -m:1
dotnet run --project src/Bizigo.Cli --no-build --configuration Release -- capacity auto --config tools/capacity/dry-run.json --dry-run
```

Copy the example JSON and configure the target, profile, rate range, duration,
latency SLO, safety thresholds, generator location and an independent target
probe. `--dry-run` uses the live command's validator, prints its plan, and starts
no processes or network requests. Relative paths are relative to the current
working directory. The simulator DLL must already be built.

`same` means generator and target share a host; `separate` is the operator's
explicit deployment declaration. The CLI starts the generator as a distinct
local process, so a remote target can use `separate`. It cannot independently
verify host placement. `unknown` is retained and can never publish capacity.

Remove `--dry-run` to execute. Each candidate rate gets three consecutive
attempts with identical configuration and independent GUID-derived RUN-IDs.
The engine doubles the rate until failure or the configured maximum, then
bisects to `ResolutionEps`. A maximum-range success is only a verified lower
bound, not a measurement of the physical ceiling. `AttemptBudget` bounds the
search. A restricted or aborted attempt stops the whole discovery, even if
earlier rates passed.

| Exit | Verdict | Capacity |
| --- | --- | --- |
| 0 | PASS | Verified lower EPS, linked to three persisted attempts |
| 1 | FAIL | null (minimum rate did not pass) |
| 2 | INCONCLUSIVE | null (generator, ledger or measurement limited) |
| 3 | ABORTED | null (safety breaker or cancellation) |
| 4 | Configuration or persistence error | No published capacity |

## Target probe protocol

`Probe.FileName` and `Probe.Arguments` start an operator-supplied executable,
without a shell. The probe receives one JSON `CapacityProbeRequest` on stdin
and must write exactly one `CapacityProbeResponse` JSON to stdout, exit 0,
and write any diagnostics to stderr. Models are in
`sim/Bizigo.Capacity/CapacityProcessRunner.cs` and `CapacityOptions.cs`.
Enum names or their existing numeric representations are accepted. Unknown
JSON members are rejected to detect configuration/schema mistakes.

The request includes `SchemaVersion=1`, `Phase`, full `Attempt`, `Host`, `Port`,
`Transport`, `ManifestPath`, `StartedAt` and the local `GeneratorPid` (null before
launch, diagnostics only; it is not a PID on a remote target). Phases are `baseline` before the
generator starts, periodic `sample`, and `final` after the generator and
settling window finish. Every response must contain `Sample`; only `final`
also needs `Observation`.

```json
{
  "Sample": {
    "RunId": "copy-the-request-attempt-id",
    "Target": "copy-the-request-target",
    "MonotonicSeconds": 1234.5,
    "CapturedAt": "2026-09-25T12:00:00Z",
    "WireDrops": 12,
    "CpuPercent": 30,
    "RecvQueue": 0,
    "UnavailableReason": null
  },
  "Observation": null
}
```

Read these values on the **target**, in its socket namespace. On Linux an
adapter can use the target socket's `/proc/net/udp` drops and receive queue,
and `/proc/stat` deltas for CPU. A TCP adapter must use a documented target
drop counter; absence is null with a reason, not zero. Baseline wire drops
are historical; the breaker considers new deltas. Each metric has its own
consecutive-violation counter. Equality to the configured threshold passes.
`ConsecutiveSamples`, `SampleIntervalSeconds`, `MaxSampleGapSeconds`, and
`ProbeTimeoutSeconds` are explicit safety controls. No samples, stale timestamps,
clock reversal, scope mismatch, counter reset, invalid numbers or probe silence
prevent PASS. Target UTC must be synchronized within the configured freshness
budget, while `MonotonicSeconds` comes from a stable target monotonic clock.
`MissingProbePolicy` is `Inconclusive` (default) or `Abort`.

For final observations, query the actual target's archive and searchable
product surface with the attempt RUN-ID and owner-group scope. Supply:

- `RunId`, `Target`, existing five-reading `ArrivalLedger` model, and measured
  `LatencyMilliseconds` (the deployment's declared latency SLO statistic).
- `Archived` and `Searchable` identity lists, each containing `RunId`, zero-based
  `Sequence`, and lowercase SHA-256 `Digest` of the original Latin-1 wire bytes
  **without newline framing**. Query errors produce null and an explicit reason.
- Archive count means distinct exact digest/sequence matches; searchable count
  includes duplicates. Foreign run identities may be present in a wider query,
  but ledger counts must be scoped to this run. They are independently checked.

Never derive product counts from the generator manifest or replace an unreadable
probe with zero. The CLI deliberately does not embed credentials, bypass product
scope, or pretend that one deployment's counter mapping works for every target.
The adapter is the deployment integration boundary and must document its
counter sources and latency statistic. `fixture-probe.py` is explicitly synthetic
and only supports the acceptance harness; it is **not** a production adapter.

## Persistence and cancellation

Each final attempt is a single schema-versioned JSON file with configuration,
manifest, independent observation, ledger report, safety samples, verdict and
reason. Missing manifest/observation remains null with a reason. Intermediate
generator files live under `.work/<RUN-ID>/`; they are not completed run records.
The summary `*-discovery.json` references the three persisted passing attempts
behind its capacity. Attempt persistence failure cannot publish a capacity.

Writes use a unique same-directory temporary file, flush-to-disk, then rename
without overwrite. Readers enumerate only `*.json` in the output directory.
Crash leftovers ending in `.tmp` are incomplete and must not be counted as runs.
The process-crash fixture proves atomic visibility; it makes no physical
power-loss or filesystem-directory-fsync claim. Files are local operator-owned
evidence; retention and backup are the operator's responsibility.

Each command starts beneath a private supervisor before it can create children.
On Unix the supervisor establishes a new process session/group; on Windows it
is assigned to a kill-on-close job before launch. It keeps ownership alive after
the command exits. Normal exit, nonzero exit, timeout and cancellation all
terminate the owned group/job before reporting completion, including children
whose original parent has already exited and children with detached stdio.
The command must retain membership in its inherited process group/job; deliberately
escaping that OS boundary is not supported. Unrelated processes are untouched.

Cancellation and a tripped breaker terminate the owned generator tree; probe
calls and generator execution have separate deadlines. Failures carry their
reason and missing evidence instead of becoming target loss. The runner allows
at most two seconds per active process teardown, inside the five-second contract
bound; an unsuccessful teardown is reported as a measurement failure.

## Acceptance commands

```sh
bash tools/machine-resources.sh check
dotnet test tests/Bizigo.UnitTests --configuration Release --filter 'FullyQualifiedName~Capacity|FullyQualifiedName~Generator|FullyQualifiedName~Arrival' -m:1
bash tools/machine-resources.sh check
dotnet test tests/Bizigo.IntegrationTests --configuration Release --filter 'FullyQualifiedName~CapacityEmitter' -m:1
bash tools/machine-resources.sh check
python3 tools/capacity-process-smoke.py
bash tools/machine-resources.sh check
python3 tools/capacity-descendant-smoke.py
bash tools/machine-resources.sh check
python3 tools/capacity-mutation-gate.py --descendants
bash tools/machine-resources.sh check
python3 tools/capacity-mutation-gate.py
bash tools/machine-resources.sh check
python3 tools/capacity-mutation-gate.py --legacy
```

The mutation tool loads the existing B01/B02 mutation catalogs with `--legacy`
and executes all negative tests and positive controls in a fresh source copy.
With `--descendants`, it changes process-group cleanup to parent-only cleanup
and requires both generator and probe regressions to detect a surviving PID;
compilation failures or unrelated fixture failures do not count as a kill.
It uses TRX to require actual failing tests; compilation/runner failure and zero
tests cannot masquerade as killed mutations. After reverting each injected
change, the complete capacity/generator/arrival suite must pass. The main tree
is never mutated. The TCP/UDP and writer child fixtures are container-free and
are explicitly synthetic acceptance checks, not hardware capacity measurements.
