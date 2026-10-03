-- O03: a missing/ambiguous parent is an explicit admitted decision, not an
-- absence inferred by a public reader. Later parent arrival publishes a new
-- resolved decision for the same child fingerprint at a committed watermark.
CREATE TABLE IF NOT EXISTS topology_parent_resolution
(
    child_anchor FixedString(64),
    child_fingerprint FixedString(64),
    reason LowCardinality(String),
    captured_context_json String,
    publication_seq UInt64
)
ENGINE = ReplacingMergeTree(publication_seq)
ORDER BY (child_anchor, child_fingerprint);
