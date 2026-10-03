-- A batch is durable before its publication sequence is reserved in PG.
-- Retain the exact immutable payload until an operator can prove no pending
-- publication refers to it; a time-based TTL would break crash recovery.
CREATE TABLE IF NOT EXISTS topology_projection_batches
(
    publication_key FixedString(64),
    payload_sha256 FixedString(64),
    rowset_sha256 FixedString(64),
    edge_count UInt32,
    conflict_count UInt32,
    payload_json String
)
ENGINE = MergeTree
ORDER BY publication_key;
