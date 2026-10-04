-- Publication-version retention is a maintenance cutover, not an online ALTER.
-- These templates are never served to readers. The PG-aware repair runner
-- creates a fresh per-attempt copy, validates immutable manifests and the
-- non-TTL lifecycle authority, and swaps canonical names only while the
-- durable PG phase is Repairing. Applying this file alone does NOT mark Ready.
-- Do not edit 0008/0011 or the frozen v2/v4 batch hashes.
CREATE TABLE IF NOT EXISTS topology_edges_observed_v0014_template
(
    owner_group LowCardinality(String),
    child_owner_group LowCardinality(String),
    parent_owner_group LowCardinality(String),
    from_node_id String,
    to_node_id String,
    relation LowCardinality(String),
    directed UInt8,
    provenance LowCardinality(String),
    confidence Float32,
    edge_id FixedString(64),
    trace_logical_id String,
    parent_span_logical_id String,
    span_logical_id String,
    parent_semantic_anchor FixedString(64),
    child_semantic_anchor FixedString(64),
    parent_fingerprint FixedString(64),
    child_fingerprint FixedString(64),
    parent_source_id String,
    child_source_id String,
    parent_node_binding_revision Int64,
    child_node_binding_revision Int64,
    parent_owner_history_revision Int64,
    child_owner_history_revision Int64,
    parent_node_history_revision Int64,
    child_node_history_revision Int64,
    parent_event_time_nano UInt64,
    child_event_time_nano UInt64,
    first_seen UInt64,
    last_seen UInt64,
    parent_occurrence_ids Array(String),
    child_occurrence_ids Array(String),
    evidence_occurrence_ids Array(String),
    parent_trace_expiry Decimal(21,0),
    child_trace_expiry Decimal(21,0),
    observed_expiry Decimal(21,0),
    expires_nano Decimal(21,0),
    fingerprint FixedString(64),
    publication_seq UInt64,
    ttl_at DateTime('UTC'),
    ttl_supported UInt8,
    physical_row_sha256 FixedString(64),
    INDEX idx_parent_anchor parent_semantic_anchor TYPE bloom_filter GRANULARITY 4,
    INDEX idx_child_anchor child_semantic_anchor TYPE bloom_filter GRANULARITY 4
)
ENGINE = ReplacingMergeTree(publication_seq)
PARTITION BY intDiv(child_event_time_nano, 86400000000000)
ORDER BY (child_owner_group, from_node_id, to_node_id, last_seen, edge_id, publication_seq)
TTL ttl_at DELETE WHERE ttl_supported = 1
SETTINGS index_granularity = 8192;

-- Conflict variants must remain physically inspectable even when the same
-- anchor and publication sequence are retried with a divergent context.
-- Unlike the old table this is plain non-TTL MergeTree: a reader reduces
-- exact duplicates but treats any same-anchor/seq disagreement as unavailable.
CREATE TABLE IF NOT EXISTS topology_span_conflicts_v0014_template
(
    semantic_anchor FixedString(64),
    first_fingerprint FixedString(64),
    conflicting_fingerprint FixedString(64),
    candidate_context_json String,
    publication_seq UInt64
)
ENGINE = MergeTree
ORDER BY (semantic_anchor, publication_seq);

-- This table is plain MergeTree without TTL or replacement. It is an
-- admission-captured decision ledger, not a cache of surviving physical rows.
-- A newer expired version suppresses an older long-retention row even after
-- ClickHouse has physically removed the newer edge from the TTL table.
CREATE TABLE IF NOT EXISTS topology_edge_lifecycle_v0014_template
(
    child_owner_group LowCardinality(String),
    parent_owner_group LowCardinality(String),
    from_node_id String,
    to_node_id String,
    first_seen UInt64,
    last_seen UInt64,
    parent_event_time_nano UInt64,
    child_event_time_nano UInt64,
    edge_id FixedString(64),
    publication_seq UInt64,
    expires_nano Decimal(21,0),
    physical_row_sha256 FixedString(64),
    INDEX idx_lifecycle_edge_id edge_id TYPE bloom_filter GRANULARITY 4,
    INDEX idx_lifecycle_parent_owner parent_owner_group TYPE set(1024) GRANULARITY 4,
    INDEX idx_lifecycle_to_node to_node_id TYPE bloom_filter GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY intDiv(child_event_time_nano, 86400000000000)
ORDER BY (child_owner_group, from_node_id, to_node_id, last_seen, edge_id, publication_seq)
SETTINGS index_granularity = 8192;

-- The empty canonical name allows an Atomic-database EXCHANGE on first repair.
-- Its presence alone is not authority; PG Ready plus a four-table live
-- certificate is mandatory before any observed/mixed read.
CREATE TABLE IF NOT EXISTS topology_edge_lifecycle AS topology_edge_lifecycle_v0014_template;

-- Parent decisions are v4 manifest material, including MissingParent. They
-- are rebuilt from frozen manifests into a fresh per-attempt table rather
-- than trusted from whatever survived in the old canonical table.
CREATE TABLE IF NOT EXISTS topology_parent_resolution_v0014_template
(
    child_anchor FixedString(64),
    child_fingerprint FixedString(64),
    reason LowCardinality(String),
    captured_context_json String,
    publication_seq UInt64
)
ENGINE = MergeTree
ORDER BY (child_anchor, child_fingerprint, publication_seq);
