---
title: "T12 — Python sidecar ve devre kesici"
kind: ticket
status: 2
---

# T12 — Python sidecar (Drain3 + pySigma)

**Bağımlılık:** T03, T05 · **Sonraki:** —
**Yöneten belgeler:** [F1 §9](../../f1-teknik-plan/index.md) ·
[K14, §3.2](../../mimari-kararlar/index.md)

## Amaç

Bilinmeyen format keşfinin altyapısı. **Sıcak yolda değil** — sidecar ölürse ingest
çalışmaya devam eder. Bakımsız bir kütüphaneye bağlanmayı kabul edilebilir kılan
tek şey bu.

## Kapsam

### İçinde

1. **Tek Python imajı** — `drain3` + `pysigma`.
**Sürüm:** PyPI `drain3` 0.9.11 (Tem 2022) **kullanılmaz** — `logpai/Drain3`
git SHA'sına sabitlenir (repo Şub 2025'e kadar typing + modern Python desteği aldı).
2. **HTTP sözleşmesi** ([F1 §9](../../f1-teknik-plan/index.md))
     ```
         POST /v1/mine/batch    { source_key, messages:[{id,text}] }
                             →  { results:[{id, template_id, template, params[], is_new}] }
         POST /v1/mine/match    (öğrenmeden eşleştirir)
         GET  /v1/clusters/{source_key}
         POST /v1/sigma/compile { rule_yaml, target:"clickhouse" } → { sql, warnings }
         GET  /healthz  /readyz
     ```
3. **Miner durumu** — kaynak sınıfı **başına ayrı miner** (temiz küme + doğal
sharding). Redis persistence (dosya değil). `max_clusters` **zorunlu** ayarlanır
(LRU) — sınırsız bırakılırsa ağ loglarında bellek sızıntısı gibi davranır.
4. **.NET istemci kuralları**
  - Çağrı yolu: yalnızca `parse_status=failed` olaylar + örneklenmiş trafik
  - Sınırlı kuyruk, **dolunca düşür** — asla ingest'i bloklama
  - Devre kesici: ardışık N hata → 5 dk kapalı; sağlık ucunda görünür
  - Zaman aşımı 2 sn
  - Sürüm uyumsuzluğu → devre kesici açık
5. **Maskeleme sinerjisi** — Drain3'ün mask regex'leri ile grok pattern kütüphanesi
**tek kaynaktan** üretilir; ortak tanım tek dosyada tutulur ve sidecar imajına
oradan enjekte edilir. F4'teki format keşfi senaryosunun çıktı kalitesi buradan
geliyor.
6. **`template_id`'nin olay tablosuna yazılması** — F3'ün "ilk-görülen imza"
korelasyonu buna dayanıyor.

### Dışında

Format keşfi senaryosunun kendisi (F4), Sigma kural yönetimi ve derlenmiş SQL
kataloğu (F3 — bu ticket yalnızca `compile` ucunu sağlıyor).

## Kabul kriterleri

Sidecar durdurulduğunda ingest etkilenmiyor — throughput ölçülüp doğrulanıyorDevre kesici açılıyor, 5 dk sonra kapanıyor, sağlık ucunda görünüyorKuyruk dolduğunda istekler düşüyor, ingest bloklanmıyorYeniden başlatmada Redis'ten öğrenilen ağaç korunuyormax_clusters sınırı çalışıyor; bellek sınırlı kalıyor (uzun koşu testi)drain3 sürümü git SHA'sına sabitlenmiş; pip install drain3 kullanılmıyorMask tanımları ile grok pattern'leri aynı kaynaktan üretiliyor (tek dosya)sigma/compile bir örnek kuralı ClickHouse SQL'e çeviriyor

## Notlar

- **Kritik tasarım kısıtı:** sidecar sıcak yolda değil. Sert bağımlılık kurulursa
([risk #3](../../mimari-kararlar/index.md)) Drain3'ün upstream durgunluğu gerçek
bir risk haline gelir. Kod küçük (~28 KB), MIT — gerekirse fork edilir.
- Sigma derlemesi F3'te **build-time** kalacak; bu ticket'ın `compile` ucu F3'teki
"bu kuralı derle ve önizle" akışı için. Sıcak yolda Python yok.
