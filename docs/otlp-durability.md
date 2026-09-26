# OTLP metrics and traces: admission, archive and replay

`POST /v1/metrics` and `POST /v1/traces` require the existing `ingest` policy.
Both accept `application/x-protobuf` and OTLP `application/json`, with identity
or gzip content encoding. The shared log/webhook body reader bounds compressed
and expanded sizes, validates complete gzip members, and rejects malformed
compression. The expanded payload limit comes from `Ingest:MaxRequestBytes`.
Gzip has a separate wire cap: expanded limit plus the larger of 64 KiB and
12.5% of that limit, allowing stored-deflate overhead while bounding excessive
headers or empty concatenated members. Both caps are checked while reading;
neither trusts Content-Length.

JSON uses lowerCamelCase names, numeric enums, hex trace/span IDs and standard
base64 for ordinary bytes values. Unknown fields are ignored; snake_case names
are unknown rather than aliases. Integer fields accept OTLP string/numeric wire
forms without a floating-point intermediate, including exact exponent forms;
fractional or out-of-range integer values are rejected. Duplicate JSON property
names, including unknown fields, return a controlled protocol error. The
vendored metric/trace schema is OpenTelemetry protobuf v1.11.0.

Each accepted HTTP export has a version-1 envelope with a new ID, signal,
normalized content type, UTC receive time, payload length, SHA-256, and exact
decompressed request bytes. JSON whitespace and unknown protobuf fields survive.
Its versioned admission decision records accepted resource/scope/leaf positions
and the rejected leaf count. Replay checks that decision against the raw payload.

Validation rejects points of unnamed metrics, inconsistent histogram bucket
counts/bounds, bucket totals that differ from count, and malformed exemplar IDs.
A histogram may omit both distribution arrays. Traces reject invalid/all-zero
required IDs, invalid optional parent/link IDs, or end times before start times.
Partial success counts rejected datapoints or spans, never resource/metric groups.
An empty export returns empty success; a fully rejected nonempty export records
its raw envelope and rejection decision. Optional metric fields remain optional.

Admission reserves a bounded queue slot **before** writing WAL. Full queue or
WAL returns `503` and positive `Retry-After` without a new frame. Every new signal
ACK follows a real `Flush(true)`, regardless of the older log benchmark flag
`Ingest:Wal:FlushToDisk`. Failed append faults the writer until reopen/recovery.
An observed 2xx therefore has a durable frame. A process crash after fsync but
before its response may leave a replayable export that the client did not see
acknowledged. Separate HTTP retries get separate envelope IDs; no global payload
hash deduplication or source-independent exactly-once promise is made.

Signal WAL lives in `Ingest:Signals:Directory`, defaulting to a `telemetry`
subdirectory of the log WAL directory. `ChannelCapacity` defaults to 128.
Signal recovery trims an incomplete EOF frame, but a complete integrity failure
quarantines the original segment in place, preserving its suffix and refusing
new admission. `/internal/ingest/signals` reports readiness and failure; it
requires the ingest policy and contains no metric/span content. Corruption needs
operator investigation; silently discarding acknowledged suffixes is forbidden.

Archive objects use the internal S3 prefix `otlp/v1/{metrics|traces}/<id>.json.zst`.
Each is a compressed raw envelope, independently read back and SHA-256 verified.
Local durable manifests bind envelope, signal, payload checksum, compressed-object
checksum and WAL segment. These manifests are separate from the raw-log control
plane table: mixed-owner signal exports cannot appear in existing raw-log read
or sample endpoints. `RawStore:SegmentRetention` applies only after every envelope
in the segment has a verified object and a durable processed result. Verification
failure retains WAL and is visible; retry can repair the object from retained WAL.
The service sweeps on `Ingest:Signals:RetryInterval` even without new work, so
expired verified segments release a full WAL and allow Collector retries again.
Sweeps serialize with processing/replay and never delete an unverified result.

Processed files retain the complete typed metric datapoint/span, resource/scope,
schema URLs and a server-resolved source/owner. Candidate priority is
`bizigo.source_key`, `service.instance.id`, `host.id`, `host.name`, `service.name`.
Unknown or ambiguous keys remain `_unassigned`; payload `owner_group` never grants
authority. Replay intentionally resolves against the current inventory snapshot.

Each envelope writes one atomic result file; envelope ID plus leaf position is
its stable logical identity. The result itself is the checkpoint, committed with
temp-file fsync and rename. Replaying the same envelope replaces that result
atomically without creating another logical event. No ClickHouse metric/trace
query or owner-scoped product surface is introduced here.

To replay archived signals, stop the API instance owning the same directory,
then run its normal configuration with the local operator flag:

```sh
dotnet src/Bizigo.Api/bin/Release/net10.0/Bizigo.Api.dll \
  --contentRoot src/Bizigo.Api --replay-signals
```

The command uses production DI, inventory and S3 configuration. It recovers WAL,
replays verified archive envelopes, writes a Completed/Failed/Cancelled report
and exits nonzero on error (130 on cancellation). An exclusive writer lease
prevents two owners of the directory.
Restart the API afterwards. Cancellation/failure never advances a separate early
checkpoint; a later replay can retry. Keep the manifests and processed directory
on persistent storage alongside WAL; losing all local metadata after WAL retention
is outside the process-crash guarantee. Unknown envelope versions fail closed.
Older releases must not consume this directory as log NDJSON; downgrade requires
preserving it for a compatible reader. Physical device/power-loss guarantees and
production throughput are not established by these tests.

Local checks are `tools/otlp-durability-smoke.py`, `tools/otlp-mutation-gate.py` and
`tools/otlp-verify-trx.py`. Docker belongs to the coordinator: it starts
`tools/otlp-collector-smoke.py --serve --session-dir <empty directory>` and stops
the owned session with `--stop`. The live session uses real `Bizigo.Api` and the
shipped Collector YAML. An independent evaluator runs
`tools/otlp-collector-probe.py --session <session.json> --evidence-dir <directory>`;
that tool uses HTTP and read-only artifact/S3 access, with no Docker calls.
Its typed oracle is the producer fixture; its raw-byte oracle is a transparent
capture of the decompressed request sent by Collector to the API.
