-- Observed rows are inserted first with their publication_seq. They become
-- publicly eligible only after this single server-controlled watermark moves.
-- ReplacingMergeTree keeps crash/retry writes idempotent; FINAL is mandatory on
-- reads so background merge timing cannot expose an older committed revision.
CREATE TABLE IF NOT EXISTS topology_publication_watermark
(
    id UInt8,
    committed_sequence UInt64
)
ENGINE = ReplacingMergeTree(committed_sequence)
ORDER BY id;

INSERT INTO topology_publication_watermark (id, committed_sequence)
SELECT 1, 0
WHERE NOT EXISTS
(
    SELECT 1 FROM topology_publication_watermark FINAL WHERE id = 1
);
