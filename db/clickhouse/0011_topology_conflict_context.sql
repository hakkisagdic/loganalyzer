-- B03 conflict attribution is captured at admission and durable in the
-- publication manifest. Empty means a legacy marker with unknown context;
-- readers must not guess owner/node/time from current inventory.
ALTER TABLE topology_span_conflicts
    ADD COLUMN IF NOT EXISTS candidate_context_json String DEFAULT '';
