-- Observed topology events are evidence, not a replacement for declared edges.
-- A row is published only when publication_seq is at or below the committed
-- watermark in 0009. All readers must also check expires_nano against their
-- fixed read clock; ClickHouse TTL is only delayed physical cleanup.
CREATE TABLE IF NOT EXISTS topology_edges_observed
(
    owner_group LowCardinality(String), -- child_owner_group index copy, not authorization
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
    INDEX idx_parent_anchor parent_semantic_anchor TYPE bloom_filter GRANULARITY 4,
    INDEX idx_child_anchor child_semantic_anchor TYPE bloom_filter GRANULARITY 4
)
ENGINE = ReplacingMergeTree(publication_seq)
PARTITION BY intDiv(child_event_time_nano, 86400000000000)
ORDER BY (child_owner_group, from_node_id, to_node_id, last_seen, edge_id)
TTL ttl_at DELETE WHERE ttl_supported = 1
SETTINGS index_granularity = 8192;

-- Arbitration is global, before scope filtering. Once an anchor has two
-- fingerprints, previously published edges touching it cease to be proof.
-- Only committed markers are eligible to affect public reads.
CREATE TABLE IF NOT EXISTS topology_span_conflicts
(
    semantic_anchor FixedString(64),
    first_fingerprint FixedString(64),
    conflicting_fingerprint FixedString(64),
    publication_seq UInt64
)
ENGINE = ReplacingMergeTree(publication_seq)
ORDER BY semantic_anchor;
