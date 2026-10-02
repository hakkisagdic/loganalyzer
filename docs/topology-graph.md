# Topology graph ownership, visibility, and publication

Topology identity is a server-issued, type-qualified `kind:uuid`. Display names,
OTLP attributes, source hostnames, VLANs, and aliases are lookup/display data;
they never merge identities. PostgreSQL is authoritative for nodes, bindings,
owner history, declared edges, and the read epoch. ClickHouse stores observed
trace edges and their physical two-endpoint ownership snapshots.

## Owner history

Owner changes append `topology_owner_history` with the node ID, old/new owner,
node version, actor, and timestamp in the same PostgreSQL transaction as node
history, audit, and epoch advancement. A failed audit therefore cannot leave a
committed transfer. A concurrent transfer and mutation use the node version as
an optimistic boundary; one writer receives a retryable 409. The old owner's
historical interval remains readable at its event time, while events at and
after the half-open transfer boundary belong to the new owner.

Payload `owner_group` values do not grant authority. `_unassigned` is a negative
resolution bucket, not an authoritative owner that registry writes may assign.
Only explicit system scope can read unresolved topology snapshots.

## Cross-owner visibility

Every observed edge physically records both endpoint owner snapshots. Full edge
content requires authorization for both endpoints. A reader authorized for only
one visible anchor receives a distinct external-neighbor count, but no hidden
node/edge ID, name, attributes, trace/span evidence, cursor material, or audit
response content. Multiple proofs or provenances reaching the same hidden node
count once. Hidden-to-hidden components do not contribute.

An endpoint-owner snapshot inconsistent with the node history fails closed; the
server never chooses a convenient current owner or trusts the request payload.
Unknown and hidden detail use the same 404 response.

## Two-store publication

Observed projector writes are tagged with a server-controlled
`publication_seq`. Inserted rows remain ineligible until the single-row
`topology_publication_watermark` advances contiguously. Publication order is:

1. write every idempotent ClickHouse projection row using the supplied sequence;
2. commit that sequence and advance the PostgreSQL read epoch under the
   publication advisory lock;
3. acknowledge the same sequence in the ClickHouse committed watermark.

A crash before step 2 leaves public results unchanged and replay reuses the
same next sequence. A crash between steps 2 and 3 leaves PostgreSQL ahead by
exactly one; reads return 409/restart and the next publisher repairs the missing
acknowledgement before allocating another sequence. Gaps and skipped sequences
are rejected. Queries filter observed rows at or below the committed watermark,
capture `(PG epoch, CH watermark)` before work, and compare it again before
returning. A mismatch is 409, never a mixed 200 response.

Signed opaque cursors bind route, normalized filters, effective identity/scope,
resolved as-of time, window, PostgreSQL epoch, ClickHouse watermark, last stable
key, and `validUntil`. `validUntil` is the earlier of normal cursor expiry and
the earliest eligible evidence expiry. Scope/tamper errors are 400; changed
revision or `clock >= validUntil` is 409/restart.

## Retention, replay, and operations

Observed rows are excluded at the exact effective-expiry boundary even if a
physical TTL merge has not run. Replay uses the admission-captured owner,
binding, and retention metadata; current inventory, aliases, or a later longer
retention setting cannot reclassify or extend old evidence. Declared edges do
not use observed TTL.

The restore set must include raw signal archive/manifest/owner metadata and the
immutable node, alias, and history registry snapshot. Missing or corrupt
required metadata is a controlled unresolved/error result, not a current-name
fallback. Automatic downgrade of topology identity or owner-transfer history is
refused because it would destroy authorization provenance; archive those tables
and use an explicit downgrade procedure.

Operational checks should alert on PG/CH publication mismatch, a watermark gap,
owner-history corruption, unresolved projector backlog, and repeated 409s.
There is no cross-database atomic transaction: correctness comes from the
idempotent callback, contiguous watermark, and read fence. Raw OTLP separate
HTTP retries remain separate raw occurrences; topology-only semantic dedup does
not claim global ingest exactly-once behavior.

## Verification ownership

Unit tests cover history, endpoint visibility, distinct hidden-neighbor counts,
publication fences, cursor revision/TTL, and negative `_unassigned` behavior.
The coordinator runs real PostgreSQL/ClickHouse/Collector tests and compiled DB
mutants serially; an unrun container test is not reported as passing. OpenAPI
generation/check, generated TypeScript exactness, and live probe cleanup remain
required release gates.
