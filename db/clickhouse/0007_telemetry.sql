-- Exact OTLP nanos remain UInt64: DateTime64(9) cannot represent the entire
-- wire range. ttl_at is only a bounded physical-cleanup projection; readers
-- always enforce expires_nano against one query-time clock before returning data.
-- FINAL reduces replay copies immediately, independently of background merges.
CREATE TABLE IF NOT EXISTS metric_points
(
    owner_group LowCardinality(String),
    metric_name String,
    resource_id String,
    ts UInt64 CODEC(Delta, ZSTD(1)),
    logical_id String,
    envelope_id UUID,
    service_name String,
    kind LowCardinality(String),
    unit String,
    temporality Int32,
    monotonic UInt8,
    trace_id String,
    span_id String,
    status Int32,
    summary_key String,
    record_version UInt16,
    record String CODEC(ZSTD(3)),
    record_sha256 FixedString(64),
    expires_nano Decimal(21,0),
    ttl_at DateTime('UTC'),
    ttl_supported UInt8,
    INDEX idx_ts ts TYPE minmax GRANULARITY 1
)
ENGINE = ReplacingMergeTree
PARTITION BY intDiv(ts, 86400000000000)
ORDER BY (owner_group, metric_name, resource_id, ts, logical_id)
TTL ttl_at DELETE WHERE ttl_supported = 1
SETTINGS index_granularity = 8192;

CREATE TABLE IF NOT EXISTS trace_spans
(
    owner_group LowCardinality(String),
    service_name String,
    start_time UInt64 CODEC(Delta, ZSTD(1)),
    trace_id String,
    logical_id String,
    envelope_id UUID,
    resource_id String,
    metric_name String,
    kind LowCardinality(String),
    unit String,
    temporality Int32,
    monotonic UInt8,
    span_id String,
    status Int32,
    summary_key String,
    record_version UInt16,
    record String CODEC(ZSTD(3)),
    record_sha256 FixedString(64),
    expires_nano Decimal(21,0),
    ttl_at DateTime('UTC'),
    ttl_supported UInt8,
    INDEX idx_start_time start_time TYPE minmax GRANULARITY 1
)
ENGINE = ReplacingMergeTree
PARTITION BY intDiv(start_time, 86400000000000)
ORDER BY (owner_group, service_name, start_time, trace_id, logical_id)
TTL ttl_at DELETE WHERE ttl_supported = 1
SETTINGS index_granularity = 8192;

-- This durable fact intentionally outlives the point/span TTL. A formerly fed
-- source is Empty after expiry, not NeverFed. No payload is duplicated here.
CREATE TABLE IF NOT EXISTS telemetry_feed_history
(
    owner_group LowCardinality(String),
    resource_id String,
    signal UInt8
)
ENGINE = ReplacingMergeTree
ORDER BY (owner_group, resource_id, signal);
