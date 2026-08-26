---
title: Feature parity — hedef ve bugünkü karşılığı
category: references
tags: [log-analiz, mimari, kaynak-ozeti, bizigo]
aliases: [feature parity, pazar araştırması, MVP/v2/Diff]
relationships:
  - target: "[[concepts/kapsam-yazmama-karari-ve-kayan-sinir]]"
    type: extends
  - target: "[[references/kapsam-nerede-kalindi]]"
    type: related_to
  - target: "[[references/f2-kapanis]]"
    type: related_to
sources:
  - docs/epic/pazar-arastirmasi-ve-feature-parity/index.md
  - docs/epic/is-envanteri/index.md
  - docs/epic/fs-simulatorler/index.md
  - docs/epic/t36-devir-notu/index.md
  - CLAUDE.md
source_digest: "sha256-12/v1 CLAUDE.md=dd011df5a833 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048 docs/epic/pazar-arastirmasi-ve-feature-parity/index.md=5ce5d91ed46b docs/epic/t36-devir-notu/index.md=5027982dbb85"
summary: 2026-08-14 parity matrisinin MVP/v2/Diff sınıflaması ve bugünkü envanterde karşılığı — hangi MVP indi, hangi v2 erken geldi, hangi Diff hâlâ kanıtsız.
provenance:
  extracted: 0.75
  inferred: 0.25
  ambiguous: 0.0
base_confidence: 0.72
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Feature parity — hedef ve bugünkü karşılığı

2026-08-14 tarihli `docs/epic/pazar-arastirmasi-ve-feature-parity/index.md`
§2 bir aracın "ciddiye alınması" için gereken yetenekleri üç sınıfa böldü: **MVP** (ilk
sürümde şart), **v2** (ikinci dalga), **Diff** (farklılaşma alanı). Bu sayfa o
sınıflamayı tekrarlamak yerine **envanterle çaprazlıyor** — hangi satırın
bugün karşılığı var, hangisi yok, hangisi beklenenden erken geldi.

Sınıflamanın kendisi bir kapsam aracı: `Diff` satırları ürünün kimliğini,
`MVP` satırları ise "yoksa ürün sayılmaz" eşiğini tarif ediyor.

## İnen MVP satırları

| Parity satırı | Bugünkü karşılığı |
| --- | --- |
| Syslog (RFC 3164/5424, TCP/UDP) | Collector yapılandırılmış — TCP 5140 / UDP 5141 (`fs-simulatorler` §1) |
| Deklaratif YAML parser + grok | `catalog/parsers`, `ParserYamlLoader`, parser editörü ve taslak yayın akışı (T18–T19) |
| Ortak şema (OTel semconv / OCSF) | T07 normalizasyon; T16 olay detayında OCSF/OTel sekmeleri |
| Tam metin arama + SQL analitik | T15 arama ekranı, ClickHouse; kısa sorgu eşiği ve keyset kısıtları ekranda |
| Eşik/oran alert + bildirim kanalları | T21 alarm motoru, T22 dört kanal (AES-256-GCM, redaksiyon), T23 yönetim |
| Backpressure + disk buffer + at-least-once | WAL + `fsync`, ham arşiv, dispatcher kademeleri — **ama aşağıdaki çekince ile** |

**At-least-once satırının yarısı eksik.** `IngestRetryWindowTests` şunu
sabitliyor: batch WAL'a yazılıp `fsync` edildikten **sonra** dolu kanalda
bekliyor; o pencerede zaman aşımına düşen istemci ikinci çerçeveyi yazdırıyor.
İki kayıt **birbirine bağlanamıyor**, çünkü `EventId` her çözümlemede yeniden
üretiliyor. Parity tablosunda tek satır olan yetenek, pratikte bir *tekrar*
değil bir **kimlik** sorunu (`is-envanteri` §5).

## Erken gelen v2 satırları

Üç satır planlanandan önce indi ve üçünün de sebebi aynı: kapsam/güvenlik
primitifi oldukları için sonradan eklenemiyorlar. ^[inferred]

- **Multi-tenancy + RBAC** — `owner_group`, Keycloak, `idp_group_mapping`,
  K17 kapalı-başarısızlığı ([[concepts/f1-kapsam-kaynaktan-gelir]],
  [[concepts/f2-kapsam-tek-kapi]]). Araştırma "tasarımda baştan düşünülmeli"
  notunu düşmüştü; öyle oldu.
- **PII maskeleme / redaction** — `catalog/masks`, T26'nın sır maskelemesi ve
  simülatörün `sir-dondu` senaryosu ("maskeleme siliyor değil **maskeliyor**").
- **Replay** — araştırmada v2, F1'de T11 olarak indi. S02a'da bunun bedeli
  geri döndü: TTL penceresi dışında kalan satırlar için "ham arşiv duruyor,
  yani K12 replay ile geri kazanılabilir bir kayıp" cümlesi ancak replay
  varken kurulabiliyor.

## Diff satırları — ürünün kimliği, kanıtı en zayıf yer

| Diff satırı | Durum |
| --- | --- |
| **Sigma kural desteği** (MVP olarak işaretliydi) | F3'te hâlâ açık: T30 prototip, T31 pipeline, T32 derleme + üç kapı, T33 kural yönetimi. Kapsam oranının **paydası dört kez oynadı** ([[concepts/f3-oranin-paydasi]]) |
| **RCA yardımı** | En çok yol alan Diff: T34 kanıt sözleşmesi, T35 beş korelasyon, T36 kanıt paketi + deterministik rapor, T37 ekran. LLM yorumu F4'e bırakıldı ([[concepts/f3-kanit-once-akil-sonra]]) |
| **Log template mining (Drain3)** | Sidecar olarak çözüldü — araştırmanın öngördüğü iki seçenekten ikincisi |
| **Otomatik format tespiti** | Keşif tarafı var (sidecar, `DiscoveryWorker`), ama bu dilimin belgelerinde ölçülmüş bir kapsam sayısı yok ^[ambiguous] |
| **Schema-on-read** (Sumo FER modeli) | Bu dilimin belgelerinde karşılığı **aranmış ve bulunamamıştır** ^[ambiguous] |
| **Çok dilli mesaj gövdesi** | Kodlama tarafı ürüne girmiş (`bizigo.wire_encoding`, `bozuk-kodlama` senaryosu); yerelleştirilmiş tarih/sayı tarafı görünmüyor |

Sigma satırı özellikle öğretici: parity tablosunda **MVP** işaretliydi ve
"binlerce hazır kural bedavaya gelir" gerekçesine dayanıyordu. Envanterin
ölçtüğü şey bedavanın koşullu olduğu — `compiled=24, runs=14`, on kural var
olmayan kolonlara SQL üretiyor, ve dört kural **vendor'ın sözlüğünü değil
bizim varsayımımızı** arıyor. Hazır kural seti ancak kendi eşleme tablonuz
kadar hazır. ^[inferred]

## Envanterde karşılığı görünmeyenler

Bu dilimin belgelerinde arandı, bulunamadı — yokluğu değil, **bakılan yerde
görünmediği** kaydediliyor:

- **Windows Event Log (EVTX)** — parity'de MVP ve ".NET avantajı" notlu.
- **Kafka / kuyruk, cloud (S3/CloudTrail), veritabanı polling** — üçü de v2.
- **Hot/warm/cold tiering**, **sıkıştırma/hacim azaltma** — v2.
- **Threat intel / GeoIP zenginleştirme**, **istatistiksel anomali tespiti** —
  ikincisinin komşusu var (baseline penceresi ölçüldü, taban **çıkmadı**).

## Kaynaklar

- `docs/epic/pazar-arastirmasi-ve-feature-parity/index.md` — §2 parity
  matrisi, §2.5 plugin modelleri, §3 agentic katman
- `docs/epic/is-envanteri/index.md` — §1 biten iş, §2 borç, §3 doğrulanmamışlar
- `docs/epic/fs-simulatorler/index.md` — §1, §6, §7
- `docs/epic/t36-devir-notu/index.md` — §2 `RankedEvidence` ne değildir
- Fazın kendi özeti ayrı sayfada: [[references/f3-detection-ve-rca-kaniti]]
