---
title: "T31 — Bizigo ProcessingPipeline"
kind: ticket
status: 2
---

# T31 — Bizigo `ProcessingPipeline`

**Bağımlılık:** T30 · **Sonraki:** T32

## Amaç

Sigma kurallarını **bizim** şemamıza eşleyen kalıcı pipeline. Kapsamı T30'un
ölçümü belirliyor.

## Kapsam

### İçinde

- pySigma `ProcessingPipeline` — sidecar imajında, `pySigma-backend-clickhouse`
ile birlikte.
- Alan adı eşlemesi: Sigma taxonomy → bizim `events_ocsf` kolon adlarımız
(düzleştirilmiş, noktalı değil).
- Değer dönüşümleri ve gerekiyorsa `class_uid` / `activity_id` ekleme.

  Ölçülmüş tek örnek: `fortigate_high_port_scan.yml` `proto: 6` yazıyor, kolon
  `LowCardinality(String)`. Kolon **var**, tip tutmuyor — eşleme boşluğundan
  ayrı bir sınıf, ve düzeltmesi de ayrı.
- `unmapped.X` → `unmapped['X']` (bizde `Map`, noktalı erişim çalışmıyor).

  **Bu madde artık ölçülmüş bir boşluğa karşılık geliyor.** T30 prototipi
  `UNMAPPED_FIELDS` diye 9 alan tespit etti ve `unmapped_expression()` yazdı,
  ama ikisini de **hiçbir dönüşüme bağlamadı**. Ölçüldü: örneklemin **8 kuralı**
  (`url` ×4, `dns_query_name` ×2, `query`, `http_method`, `user_agent`) bu
  yüzden ham Sigma adıyla SQL'e iniyor ve ClickHouse reddediyor. Yani
  `compiled=24 / runs=14` farkının çoğu şemanın değil prototipin eksikliği.
  Ayrıntı: [T30 ölçümü](../../t30-sigma-olcumu/index.md).
- Tablo adı: `FROM logs` yerine bizim görünümümüz.
- Kapsanan her logsource için **en az bir altın örnek** ve beklenen eşleşme.

### Dışında

- `ocsf_pipeline`'ı zincire koymak. Ölçüldü: bizim evrenimizde hiçbir şey
yapmıyor. Maliyeti sıfır ama faydası da sıfır; koyulacaksa gerekçesi yazılmalı.
- Windows/Sysmon aileleri — bizim şemamızın hedefi değil.

## Kabul kriterleri

- ✅ Derlenen her kuralın SQL'i canlı ClickHouse'ta **koşuyor**:
`compiled == runs == 21`, reddedilen kolon **yok**.

  Prototipte 24'ün 10'u ClickHouse'a çarpıyordu. Fark üç ayrı sebepten
  doğuyordu ve üçü de kapandı: ad alanlı `attrs` anahtarları, `IPv6` kolonunda
  metin operatörü, backend'in ifadeleri backtick'lemesi.

  ⚠️ O koşum `fw_chain` düzeltmesinden ve `VENDOR_EMPTY_COLUMNS`'tan **önce**
  alındı. Sonraki değişiklikler derleme sayısını düşürüyor (bekçi çalıştığı
  için) ama `runs == compiled` iddiası **yeniden ölçülmedi**.

- ⚠️ **KARŞILANMADI — `nginx` ailesi.** Kapı 3'ün beyanları:

  | Aile | `at_least_one` | `none` |
  | --- | --- | --- |
  | asa | 3 | 3 |
  | fortigate | 2 | 2 |
  | routeros | 2 | 2 |
  | **nginx** | **0** | 5 |

  nginx'in **hiçbir kuralı altın örnekle doğrulanmadı** ve sebebi eşleme
  değil **korpus**: altın örneklerimiz ağırlıklı `access-json` biçiminde,
  o biçim `core.host`'u doldurmuyor, ve `/admin` · `POST` · `sqlmap` ·
  `UNION SELECT` · `upload` örneklerde **sıfır kez** geçiyor.

  Kapatan şey bir pipeline satırı değil, **combined biçimli bir altın örnek**.
  Ayrıntı: [T30 ölçümü → korpus tasarımı kalemi](../../t30-sigma-olcumu/index.md).

- ✅ Eşlenemeyen kural sessizce geçmiyor. Dört ayrı sınıf, dördü de derlemeyi
düşürüyor ve sebebini taşıyor: şema boşluğu, tanınmayan alan, vendor'a göre
boş kolon, ve T32'nin kapıları.

## Kapanırken karşılanmayan

**nginx ailesinin altın örnek doğrulaması.** Bir korpus kalemi, eşleme kalemi
değil — ve bu ayrımın kendisi ölçülerek bulundu. `status:2` ile kapanıyor ama
kriter **açık**; kapatan şey T38'in altın küme işi.

## Notlar

Ölçülen tuzaklar T30'un notlarında. En sinsisi tırnaklama tutarsızlığı: aynı SQL
içinde hem backtick'li hem tırnaksız noktalı ad üretilmiş, ve tırnaksız hâli
ClickHouse'ta derlenmiyor. Kendi pipeline'ımız düzleştirilmiş ad ürettiği için
bu sorunu baştan atlıyor — ama `ocsf_pipeline` zincire konursa geri geliyor.
