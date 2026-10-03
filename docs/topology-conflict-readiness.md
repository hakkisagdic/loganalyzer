# Topology conflict attribution and readiness

`topology_span_conflicts` is arbitrated before any owner-scope selection. New
markers include `candidate_context_json`: every distinct authoritative typed
fingerprint (plus children referring to the conflicted anchor) carries the
admission-captured owner, source, node, event time and captured expiry. The
durable `topology_projection_batches` manifest freezes the same row mapping for
crash/replay. Public readers never reconstruct this from payload claims or
current inventory. A published conflict removes prior observed edges from the
proof graph; only a conflict relevant to the authorized scope, observed time
window and requested route makes that read fail. An orphan conflict still makes
its related observed list/neighborhood/path unverifiable. Errors expose no
anchor, fingerprint, hidden owner, source or node.

## Preflight before serving observed topology

Apply `0011_topology_conflict_context.sql` before starting the new projector.
At the committed publication watermark call
`TopologyObservedSnapshotReader.CheckReadinessAsync(watermark)`. `Usable=false`
means at least one published context-free legacy marker remains. The public
observed read path then fails generically (REST 503, RCA Failed); declared-only
list/detail and node reads remain available. This is an explicit migration
readiness state, **not** a scoped conflict result. The count is operational
metadata only and must not appear in public responses or audit summaries.

A preexisting observed edge identifies that edge's published endpoints/time
and can invalidate its own proof. It does not reveal the alternative
fingerprint's captured owner/node/time, so by itself it cannot clear legacy
readiness. An orphan legacy marker has no published edge from which even that
partial attribution can be made. Do not drop either marker or infer context
from today's inventory. The normal read path must not scan all raw traces.

An authorized offline repair can use retained, checksummed typed
`trace_spans`/raw archive plus their immutable owner/topology admission
snapshots and durable publication manifests to reconstruct **all** candidate
histories for an anchor. A future explicit admin-only migration must verify
both fingerprints and the complete candidate set, write a context-bearing
replacement under the publication coordinator, and recheck readiness after
the legacy row is retired. The normal projector intentionally cannot perform
that migration. If the required raw/archive
or admission snapshot has been deleted or corrupted, there is no proof of the
missing owner/node/time: keep the marker unresolved and observed topology
unavailable until an explicit operator decision outside this read workflow.
No bounded repair CLI is introduced by this patch.

The normal projector rejects an attempt to overwrite a context-free legacy
marker with a newly reduced marker for the same anchor. Otherwise ClickHouse
background replacement could erase the old readiness evidence before an
authorized repair had proven the complete historical candidate set.

The watermark/epoch fence and signed cursor remain authoritative: a newly
published conflict or repaired marker changes the revision, so a continuation
restarts rather than mixing old proof with a new arbitration state.
