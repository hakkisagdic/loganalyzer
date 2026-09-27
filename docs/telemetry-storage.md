# Metric and trace storage

OTLP admission resolves inventory ownership at each metric point's `timeUnixNano`
or span's `startTimeUnixNano`. Inventory upsert and CSV import write half-open
ownership intervals in the same PostgreSQL transaction as the current source.
Owner, aliases and enabled state all create a revision. Existing inventory is
bootstrapped only from migration time; `CreatedAt` is not historical proof.
Unavailable history returns retryable admission failure before WAL append.
Healthy missing/ambiguous history preserves the leaf under `_unassigned`.

The v2 WAL envelope binds the exact decompressed export and accepted leaf keys
to the server's per-leaf source, owner, history revision and event time. The
binding checksum also includes retention. Payload `owner_group` cannot grant
access. A transfer A→B never changes an accepted A record on replay; later
inventory assignment never silently reclassifies `_unassigned` records.

`ClickHouse:TelemetryRetentionDays` defaults to 90, matching the default event
retention. Admission captures it in the envelope. Configuration changes apply
to subsequent admissions; replay retains the original expiry. Supported values
are 1–36500 days. Changing the policy does not recover physically deleted data.

## Delivery and query contract

The production path is fsync WAL → verified raw archive → ClickHouse leaf insert
→ durable feed marker → atomic local completion checkpoint. The previous
Sprint02 file-only checkpoint cannot satisfy database delivery. Database failure
leaves the WAL available for retry. The separate feed fact survives leaf TTL and
distinguishes a previously fed but now empty source from one never fed.

Logical identity is envelope UUID + leaf position. PostgreSQL arbitrates immutable
envelope ownership claims before either concurrent writer can publish records.
ClickHouse `ReplacingMergeTree` sort keys end in logical identity; every public
reader uses `FINAL`, so replay copies disappear from results before a merge.
Separate HTTP exports remain separate logical records even with equal bytes.

Metric sort prefix is `(owner_group, metric_name, resource_id, ts)`; trace prefix
is `(owner_group, service_name, start_time, trace_id)`. Both partition by the
integer event-time day. Full typed OTLP JSON leaves and indexed fields coexist:
integer strings, nonfinite doubles, nested attributes, all five metric types,
exemplars, parents, events and links survive the typed read path.

Wire nanos remain UInt64, including zero and UInt64.MaxValue. Query bounds are
integral decimal nanos in `[0, UInt64.MaxValue+1]`, with a half-open interval and
maximum 31-day span. Thus the top unsigned timestamp can be queried without a
DateTime conversion. Spans filter by start time, not overlap.

Logical expiry is `eventTime + capturedRetention <= queryAsOf`. Physical cleanup
uses a ceiling-to-second DateTime projection and can lag this rule. Timestamps
beyond the physical DateTime TTL range are retained physically until a supported
cleanup mechanism is used; the logical filter still governs every result. This
avoids rounding a future wire timestamp into prematurely expired data.
[ClickHouse TTL documentation](https://clickhouse.com/docs/concepts/features/operations/delete/ttl)
describes merge-driven deletion and conditional TTL; it does not provide instant
logical expiry.

All product reads go through `IScopedQuery` and its existing PostgreSQL audit
sink. Owner filters intersect authorized scope. Trace detail includes only
authorized spans, even when another owner uses the same trace ID. Outside counts
carry a distinct Failed/null result when unavailable. Denied scope exposes no
content or counts. Public errors omit credentials and raw leaves.

Lists and summaries default to 100, allow at most 1000 rows/groups and 4 MiB.
Continuation tokens bind the complete query and effective scope. A partial
response carries a cursor; a single oversized leaf returns ResultTooLarge.
Pagination is stable keyset enumeration for an unchanged dataset, without a
cross-request snapshot-isolation claim. Replays retain identities and do not
create a new pagination position.

Metric summaries separate name, type, unit, temporality, monotonic flag, resource,
instrumentation scope/schema and typed point attributes. They return logical
count, first/last event time and the full last point. They do not average buckets,
quantiles or rates. Trace summaries group service, kind and status. Every public
query attempts one bounded audit write, including failures, validation and
cancellation; an audit failure cannot return successful data.

## Restore set and downgrade

Back up the verified objects named by each manifest under `otlp/v1/<signal>/`
(including `<envelope-id>.<content-hash>.json.zst` upgrade objects) **and** the
corresponding telemetry `manifests/*.json`, along with the deployed format and
migration versions. The object namespace remains stable; an object may contain
a v1 or v2 envelope. Manifest hashes cover the complete compressed envelope,
including the owner and retention metadata. Objects alone cannot reconstruct a
trusted restore manifest. PostgreSQL ownership history/claims should also be
backed up for ongoing admissions and conflict arbitration.

To restore after WAL retention: migrate a fresh database, restore the verified
objects and manifest directory into a new signal state directory, and run the
production `Bizigo.Api --replay-signals` path with that directory and restored
object store configured. No old processed directory or mutable current inventory
is required to recover v2 ownership. Claims are recreated from verified bindings.
Missing or inconsistent v2 owner metadata fails visibly. Do not edit a manifest
to make an unverified object appear accepted.

Legacy v1 exports have no historical owner snapshot. This implementation upgrades
them to a durable `legacy-owner-unknown` assignment under `_unassigned`, preserves
raw bytes and logical identity, and archives that decision before DB publication.
The upgrade writes a separate object whose key includes its content hash, verifies
it, then atomically replaces the local manifest. It never overwrites the object
referenced by the old manifest. A crash before manifest publication leaves the
old restore set usable; retry can finish the same upgrade. Retain old objects
while backup manifests may reference them. An interrupted upgrade may leave an
unreferenced object; this path does not garbage-collect restore data. Archive
backup/restore follows each manifest's exact object key, not a reconstructed key.
Blank/whitespace legacy source candidates are skipped; all-empty candidates use
`_unknown`. This source identity choice grants no inventory ownership.
It never infers their owner from today's inventory. Explicit audited
reclassification is a separate product operation, not an automatic replay step.

The SQL change is additive and existing log/change tables remain intact.
Automatic destructive PostgreSQL downgrade is refused. An older binary may
ignore the new ClickHouse tables, but must not consume a v2 telemetry state
directory: it does not understand the ownership format. Preserve the restore set
and use a compatible reader/replayer instead of deleting unknown metadata.

## Verification ownership

Local build/unit checks are container-free. The Planner alone runs the real
PostgreSQL/ClickHouse/Testcontainers, Collector session and database mutation
gates under `tools/machine-resources.sh` claim/check. The Evaluator sends its own
HTTP requests to the live session and records an independent oracle. Neither a
compiled fixture nor a resource-blocked run constitutes database PASS.

Planner runs the live session with:

```sh
python3 tools/telemetry-storage-smoke.py --serve --session-dir /tmp/bizigo-telemetry-session
```

After `session.json` exists, each verifier uses a new evidence directory:

```sh
python3 tools/telemetry-storage-probe.py --session /tmp/bizigo-telemetry-session/session.json --evidence-dir /tmp/bizigo-telemetry-independent
```

The session starts the real `Bizigo.Api` entry point, the shipped
`deploy/otel/collector.yaml`, and a loopback-only query adapter in
`Bizigo.OtlpFixture`. Its server-owned fixture tokens define A, B, unassigned and
explicit admin scope; client filters cannot choose their access scope. Source
writes call the production `SourcesEndpoints` handlers. Query calls use
production `AddBizigoDataPlane` and `IScopedQuery`, including PostgreSQL audit.
The fixture query routes are not shipped API endpoints.

The probe sends its own source IDs, sender exports and scoped requests. It
compares typed leaves with the sender oracle and raw bytes with the independently
captured, decompressed API ingress. It also kills the owned query child after
database acknowledgement but before its local checkpoint, then verifies replay.
Each probe gets a fresh crash directory; a prior probe's restored root, processed
files or audit rows cannot satisfy the next probe's assertions. Real integration
tests additionally restore only objects/manifests into a new process **and empty
ClickHouse database**, with an empty owner-claim registry and current inventory B.

Planner stops only this session's processes/containers/volumes:

```sh
python3 tools/telemetry-storage-smoke.py --stop --session-dir /tmp/bizigo-telemetry-session
```

`cleanup.json` and `query-cleanup.json` record completion. Database mutation gates
are also Planner-only: `tools/telemetry-storage-mutation-gate.py` uses an isolated
source copy, compiles twelve variants covering eleven families (single and CSV
history are separate), requires named failing TRX results and passing positive
controls, then rebuilds and runs the restored related suites. A missing compiler,
zero tests, skipped tests or resource failure is never a killed mutant.
