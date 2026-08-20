---
title: "T07 — Normalizasyon: core → OCSF/OTel"
kind: ticket
status: 2
---

# T07 — Normalizasyon: `core` → OCSF / OTel

**Bağımlılık:** T05 · **Sonraki:** T08
**Yöneten belgeler:** [F1 §5](../../f1-teknik-plan/index.md) · [K8](../../mimari-kararlar/index.md)

## Amaç

Ayrıştırılmış alanları iki standart şemaya bağlamak — **ikisini de materyalize**
**etmeden**. K8'in "mapping bakımı sürekli iş çıkarır" riskini tek yere hapsetmek.

## Kapsam

### İçinde

1. **`core` alan kümesi** — yazılan tek gerçek: `ts, host, src_ip, dst_ip, src_port,  dst_port, proto, action, user_name, severity_num, outcome`. Sorguların ~%90'ı
bunlara vuruyor.
2. **OCSF/OTel türetmesi** — `core` + `attrs` üzerinden **hesaplanır**, ayrı
saklanmaz. Saklanan tek OCSF alanı `ocsf_class_uid` + `ocsf_activity_id`
(filtre için ucuz ve gerekli).
3. **Eşleme tabloları** — `ocsf_network_activity` gibi değer eşlemeleri veri
dosyasında durur, kodda değil. YAML `map` bloğu bunlara `{ from: …, table: … }`
ile bağlanır.
4. **IP normalizasyonu** — IPv4 → `::ffff:a.b.c.d` olarak `IPv6` kolonuna.
5. **Zaman ve yerelleştirme** — `date` adımının çıktısının UTC'ye çevrilmesi;
`timezone_field` ve `default_timezone` semantiği; yerelleştirilmiş tarih
biçimleri (K4).
6. **OCSF/OTel görünüm sorguları** — ClickHouse `VIEW` veya API katmanında türetme;
hangisi seçilirse gerekçesi yazılır.

### Dışında

Vendor parser'larının `map` blokları (T08), OCSF class kataloğunun tamamı — F1'de
yalnızca ağ cihazı için gereken sınıflar (Network Activity, Authentication).

## Kabul kriterleri

Aynı olay hem OCSF hem OTel görünümünden okunabiliyorcore alanları doğru tiplerde ClickHouse'a yazılıyorIPv4 ve IPv6 adresleri aynı kolonda doğru sorgulanıyorFarklı saat dilimindeki iki cihazın olayları UTC'de doğru sıralanıyorEşleme tablosuna yeni bir değer eklemek kod değişikliği gerektirmiyorTüretme yolunun maliyeti ölçüldü; sorgu başına ek yük raporlandı

## Uygulama sonucu

| Parça | Nerede |
| --- | --- |
| `ParsedEvent` → `LogEvent` | `src/Bizigo.Normalization/EventNormalizer.cs` |
| Sözleşme tipleri | `src/Bizigo.Normalization/ParsedEvent.cs` (ingest ve depolama arasında) |
| Toplu yazım | `src/Bizigo.Storage.ClickHouse/ClickHouseEventSink.cs` — 10k satır / 2 sn |
| Periyodik boşaltma | `EventSinkFlushService.cs` |
| Görünümler | `db/clickhouse/0003_ocsf_otel_views.sql` |

**İki karar verildi:**

1. **`raw_ref` = arşiv ön eki, bayt konumu değil** (K29). T04'ten devreden açık
kalem kapandı. Ayrı bir indeks tablosu ikinci bir gerçek kaynak doğururdu;
ön ek yazma anında hesaplanabiliyor ve manifest sorgusuyla örtüşüyor.
2. **Türetme ClickHouse görünümünde, API katmanında değil** (K30). Belirleyici
gerekçe F3: Sigma kuralları derleme zamanında ClickHouse SQL'ine çevriliyor ve
OCSF alan adlarına vuruyor. API'de türetme bunu imkânsız kılardı. Görünümler
**materialized değil** — materyalize etmek K8'in kaçındığı 2x depolama ve 2x
mapping bakımını geri getirirdi.

**Görünümlere kapsam filtresi gömülmedi.** `owner_group` kolonu aynen taşınıyor,
filtreyi `IScopedQuery` uyguluyor. Görünüme filtre koymak kapsamı iki yerde
tanımlamak olurdu (K17).

**Doğrulama:** derleme 0 uyarı; birim testleri **231/231** (17'si yeni:
tip çevrimi, IPv4→v6 eşleme, zaman önceliği, `raw_ref` ön eki, kapsamın
olaydan değil kaynaktan gelmesi). Görünüm testleri (OCSF/OTel okuma, IPv4/IPv6
aynı kolon, saat dilimi sıralaması, türetme maliyeti ölçümü) entegrasyon
paketinde — CI'da koşuyor.

## Notlar

- İki şemayı da tam materyalize etmek depolamayı ~2 katına, mapping bakımını iki
katına çıkarırdı. Türetme kararının bedeli sorgu anındaki küçük ek yük — bu
ticket'ta ölçülmeli, varsayılmamalı.
- OCSF sınıf seçimi ağ cihazı alanına (K2) göre dar tutulur. Genişletme F3'te
detection ihtiyacına göre gelir.
