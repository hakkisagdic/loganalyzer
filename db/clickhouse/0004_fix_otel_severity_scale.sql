-- `events_otel.SeverityNumber` ölçek düzeltmesi (T08 geri beslemesi, madde 9).
--
-- HATA: 0003 aynı `severity_num` kolonunu iki farklı ölçekmiş gibi okuyordu —
-- `events_ocsf.severity_id` OCSF ölçeği (0-6), `events_otel.SeverityNumber` ise
-- OTel ölçeği (1-24). Kolona yazılan değer OCSF ölçeğinde olduğu için OTel
-- görünümü yanlış değer veriyordu: `severity_num=5` (OCSF Critical) OTel'de
-- DEBUG anlamına geliyordu.
--
-- Sessiz sınıftan bir hata: sorgu çalışıyor, sayı dönüyor, yalnızca anlamı
-- yanlış. Gerçek vendor logu yazılırken fark edildi.
--
-- `severity_num` kolonunun anlamı bundan sonra kesin: **OCSF ölçeği**. Parser
-- kataloğu zaten OCSF yazıyor (`ocsf_class_uid`/`ocsf_activity_id` ile tutarlı).
--
-- Eşleme neden veri dosyasında değil view'da: `catalog/mappings/` **vendor**
-- değerlerini taşıyor (FortiGate'in `action=` sözcükleri gibi) — onlar sık
-- değişir ve kod değişikliği gerektirmemeli. Buradaki ise iki standardın
-- birbirine eşlenmesi; OCSF ve OTel spesifikasyonları sabit, eşleme de sabit.

DROP VIEW IF EXISTS events_otel;

CREATE VIEW events_otel AS
SELECT
    ts                                                   AS Timestamp,
    ingested_at                                          AS ObservedTimestamp,
    event_id                                             AS LogRecordUID,
    owner_group,

    -- OCSF severity_id (0-6) → OTel SeverityNumber (1-24).
    --   OCSF: 0 Unknown · 1 Informational · 2 Low · 3 Medium · 4 High
    --         5 Critical · 6 Fatal
    --   OTel: 1-4 TRACE · 5-8 DEBUG · 9-12 INFO · 13-16 WARN · 17-20 ERROR
    --         21-24 FATAL
    -- Bilinmeyen (0) OTel'de de 0: "belirtilmemiş" ile "düşük" aynı şey değil.
    multiIf(
        severity_num = 1, 9,      -- Informational → INFO
        severity_num = 2, 13,     -- Low           → WARN
        severity_num = 3, 14,     -- Medium        → WARN2
        severity_num = 4, 17,     -- High          → ERROR
        severity_num = 5, 19,     -- Critical      → ERROR3
        severity_num = 6, 21,     -- Fatal         → FATAL
        0                         -- Unknown / eşlenmemiş
    )                                                    AS SeverityNumber,

    -- Ham OCSF değeri de taşınıyor: eşlemenin kaybettiği ayrıntıyı geri
    -- kazanmanın tek yolu bu (Critical ile High aynı ERROR bandına düşüyor).
    severity_num                                         AS "bizigo.ocsf_severity_id",

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
