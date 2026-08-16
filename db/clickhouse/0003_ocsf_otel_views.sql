-- OCSF ve OTel görünümleri (F1 §5, K8).
--
-- KARAR: türetme ClickHouse görünümünde, API katmanında DEĞİL.
--
-- Gerekçe: türetme API'de kalsaydı yalnızca bizim uçlarımız OCSF/OTel şeklini
-- görürdü. Oysa F3'te Sigma kuralları derleme zamanında ClickHouse SQL'ine
-- çevriliyor ve o SQL OCSF alan adlarına vuruyor; Grafana/HyperDX gibi doğrudan
-- SQL konuşan araçlar da aynı şekli görmek zorunda. Görünüm, "SQL konuşan herkes
-- aynı şemayı görür" özelliğini bedavaya veriyor.
--
-- Görünümler MATERIALIZED DEĞİL: materyalize etmek depolamayı ~2 katına ve
-- mapping bakımını iki katına çıkarırdı (K8). Bedel sorgu anındaki türetme
-- maliyeti; ölçümü entegrasyon testinde raporlanıyor.
--
-- Kapsam filtresi burada YOK. Görünümler `events` tablosunun şeklini değiştirir,
-- yetkisini değil; `owner_group` kolonu aynen taşınıyor ve filtreyi IScopedQuery
-- uyguluyor (K17). Görünüme filtre gömmek, kapsamın iki yerde tanımlanması
-- demek olurdu.

-- OCSF Network Activity / Authentication görünümü.
-- Ağ cihazı alanı (K2) için gereken alanlarla sınırlı; genişletme F3'te
-- detection ihtiyacına göre gelir.
CREATE VIEW IF NOT EXISTS events_ocsf AS
SELECT
    ts                                                   AS time,
    event_id                                             AS uid,
    owner_group,
    ocsf_class_uid                                       AS class_uid,
    ocsf_activity_id                                     AS activity_id,
    severity_num                                         AS severity_id,

    src_ip                                               AS src_endpoint_ip,
    src_port                                             AS src_endpoint_port,
    dst_ip                                               AS dst_endpoint_ip,
    dst_port                                             AS dst_endpoint_port,
    proto                                                AS connection_info_protocol_name,

    action                                               AS activity_name,
    outcome                                              AS status,
    user_name                                            AS actor_user_name,

    host                                                 AS device_hostname,
    vendor                                               AS device_vendor_name,
    product                                              AS metadata_product_name,
    parser_version                                       AS metadata_version,

    -- Parser'ın ürettiği ek OCSF alanları `attrs` içinde `ocsf.` önekiyle
    -- duruyor: yeni bir alan eklemek şema göçü değil, YAML değişikliği.
    attrs                                                AS unmapped,
    body                                                 AS raw_data,
    raw_ref
FROM events;

-- OpenTelemetry log görünümü (semconv adlandırması).
CREATE VIEW IF NOT EXISTS events_otel AS
SELECT
    ts                                                   AS Timestamp,
    ingested_at                                          AS ObservedTimestamp,
    event_id                                             AS LogRecordUID,
    owner_group,
    severity_num                                         AS SeverityNumber,
    body                                                 AS Body,

    host                                                 AS "host.name",
    source_id                                            AS "service.instance.id",
    product                                              AS "service.name",
    vendor                                               AS "device.manufacturer",

    src_ip                                               AS "source.address",
    src_port                                             AS "source.port",
    dst_ip                                               AS "destination.address",
    dst_port                                             AS "destination.port",
    proto                                                AS "network.transport",
    user_name                                            AS "user.name",

    attrs                                                AS Attributes,
    raw_ref
FROM events;
