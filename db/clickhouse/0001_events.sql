-- ============================================================================
-- events — normalize olay tablosu
--
-- Yöneten kararlar: K8 (OCSF+OTel mapping), K17 (owner_group kapsamı),
--                   F1 teknik plan §6.1 ve §6.2.
--
-- ⚠️ ORDER BY BİR KEZ SEÇİLİR. Tabloyu yeniden yazmadan değişmez.
--    Üç aday F1 §6.2'de tabloya bağlandı; seçilen: (owner_group, source_id, ts).
--    Gerekçe: her sorgu API'den zorunlu owner_group filtresiyle geliyor (K17),
--    yani daima ön-ek taraması. owner_group kardinalitesi düşük (onlarca), bu
--    yüzden sonraki kolonların sıkıştırmasını bozmuyor. source_id gruplaması
--    sıkıştırmayı belirgin iyileştiriyor.
--    "Son 15 dk, tüm grubum" sorgusunun bedeli idx_ts (minmax) ile kapatılıyor:
--    ts kaynak içinde monoton arttığı için minmax granül atlamada çok etkili.
-- ============================================================================

CREATE TABLE IF NOT EXISTS events
(
    -- Zaman
    ts                DateTime64(3, 'UTC')   CODEC(Delta, ZSTD(1)),
    ingested_at       DateTime64(3, 'UTC')   CODEC(Delta, ZSTD(1)),

    -- Kimlik ve kapsam
    event_id          UUID,                              -- ham arşive geri bağ
    owner_group       LowCardinality(String),            -- K17; kaynaktan gelir, olaydan değil
    source_id         LowCardinality(String),
    host              LowCardinality(String),
    vendor            LowCardinality(String),
    product           LowCardinality(String),

    -- Ayrıştırma kökeni — replay ve teşhis için
    parser_id         LowCardinality(String),
    parser_version    LowCardinality(String),
    parse_status      Enum8('ok' = 1, 'partial' = 2, 'failed' = 3),
    parse_generation  UInt32          DEFAULT 1,         -- replay kuşağı (T11)
    encoding_detected LowCardinality(String),
    template_id       LowCardinality(String) DEFAULT '', -- Drain3 (T12); F3'ün ilk-görülen imzası buna dayanıyor

    -- Şema sınıflandırması (K8) — yalnızca filtrede ucuz olması gereken iki alan saklanıyor
    severity_num      UInt8           DEFAULT 0,
    ocsf_class_uid    UInt32          DEFAULT 0,
    ocsf_activity_id  UInt16          DEFAULT 0,

    -- core — sıcak kolonlar. Sorguların ~%90'ı bunlara vuruyor (F1 §5).
    src_ip            IPv6            DEFAULT toIPv6('::'),   -- IPv4 → ::ffff:a.b.c.d
    dst_ip            IPv6            DEFAULT toIPv6('::'),
    src_port          UInt16          DEFAULT 0,
    dst_port          UInt16          DEFAULT 0,
    proto             LowCardinality(String) DEFAULT '',
    action            LowCardinality(String) DEFAULT '',
    outcome           LowCardinality(String) DEFAULT '',
    user_name         String          DEFAULT '',

    -- Geri kalan alanlar + gövde
    attrs             Map(LowCardinality(String), String),
    body              String          CODEC(ZSTD(3)),
    raw_ref           String          DEFAULT '',        -- <object_key>#<offset>:<length>

    -- ts sıralama anahtarının sonunda; bölüm içi zaman dilimlemesini bu indeks kurtarıyor
    INDEX idx_ts        ts             TYPE minmax        GRANULARITY 4,

    -- attrs['x'] IS NOT NULL türü filtrelerde granül atlama
    INDEX idx_attr_keys mapKeys(attrs) TYPE bloom_filter  GRANULARITY 4,

    -- Tam metin (K4, F1 §2.4). sparseGrams değişken uzunlukta n-gram üretiyor;
    -- TR/AR/CJK'de dile özel tokenizasyon gerektirmeden alt dizi araması veriyor.
    -- (min_length, max_length, min_cutoff_length) — varsayılan max 100 log için
    -- fazla büyük indeks üretir; 20 seçildi.
    -- ⚠️ AÇIK KALEM: bu üç sayı gerçek gövdelerle ölçülüp indeks boyutuna göre
    --    kesinleştirilecek (F1 §15 kalem 4). Ayrıca büyük/küçük harf duyarsız
    --    arama için preprocessor kararı: lowerUTF8() Türkçe İ/ı'da bayt uzunluğu
    --    değiştiği için hatalı olabiliyor — bu yüzden şimdilik preprocessor YOK,
    --    normalizasyon .NET tarafında tek elden yapılıyor.
    INDEX idx_body      body           TYPE text(tokenizer = sparseGrams(3, 20, 5)) GRANULARITY 4
)
ENGINE = MergeTree
PARTITION BY toYYYYMMDD(ts)
ORDER BY (owner_group, source_id, ts)
TTL toDateTime(ts) + INTERVAL 90 DAY
SETTINGS index_granularity = 8192;
