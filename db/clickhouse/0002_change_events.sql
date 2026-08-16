-- ============================================================================
-- change_events — "ne değişti" akışı
--
-- RCA F3'te geliyor (K22) ama tablo F1'de açılıyor. Gerekçe ham arşivle aynı:
-- geçmiş birikmezse özellik boş bir kabukla doğar. Log "ne oldu"yu söyler,
-- "neden"i çoğu zaman söylemez — RCA'nın en güçlü sinyali bu tablo.
--
-- Yöneten kararlar: K21 (kanıt kapsamı), RCA raporu özelliği §3.
--
-- Hacim düşük (günde onlarca-yüzlerce satır), bu yüzden:
--   - aylık bölümleme
--   - ts sıralama anahtarında ERKEN — burada zaman aralığı sorguları baskın,
--     events tablosunun tersine
-- ============================================================================

CREATE TABLE IF NOT EXISTS change_events
(
    ts           DateTime64(3, 'UTC')  CODEC(Delta, ZSTD(1)),
    change_id    UUID,
    owner_group  LowCardinality(String),

    target_kind  Enum8('device' = 1, 'service' = 2, 'config' = 3,
                       'inventory' = 4, 'maintenance' = 5),
    target_id    String,
    change_kind  LowCardinality(String),   -- config_push, firmware, acl_change, deploy, window_open
    actor        String,
    summary      String,
    details      Map(LowCardinality(String), String),

    source       LowCardinality(String),   -- manual, api, git, netbox, ansible
    external_ref String                DEFAULT '',

    INDEX idx_target target_id TYPE bloom_filter GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY toYYYYMM(ts)
ORDER BY (owner_group, ts, target_id)
SETTINGS index_granularity = 8192;
