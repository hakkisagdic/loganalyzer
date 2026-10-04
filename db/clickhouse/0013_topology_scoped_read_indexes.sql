-- V07: the committed-version read still reduces every version of a candidate
-- edge ID. These skip indexes only narrow physical candidate discovery; they
-- are never authorization or arbitration substitutes.
ALTER TABLE topology_edges_observed
    ADD INDEX IF NOT EXISTS idx_topology_parent_owner parent_owner_group TYPE set(1000) GRANULARITY 4;

ALTER TABLE topology_edges_observed
    ADD INDEX IF NOT EXISTS idx_topology_last_seen last_seen TYPE minmax GRANULARITY 4;

ALTER TABLE topology_edges_observed
    ADD INDEX IF NOT EXISTS idx_topology_publication publication_seq TYPE minmax GRANULARITY 4;

ALTER TABLE topology_edges_observed
    ADD INDEX IF NOT EXISTS idx_topology_edge_id edge_id TYPE bloom_filter GRANULARITY 4;
