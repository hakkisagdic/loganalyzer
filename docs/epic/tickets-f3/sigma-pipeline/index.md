---
title: "T31 — Bizigo ProcessingPipeline"
kind: ticket
status: 0
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

- `ocsf_pipeline`'ı zincire koymak. **Karar aynı, gerekçesi düzeltildi (T30).**

  Eski gerekçe *"maliyeti sıfır ama faydası da sıfır"* diyordu. Faydası
  gerçekten sıfır — ama **maliyeti sıfır değil, negatif.** T30 prototipi
  yazılırken ölçüldü: `ocsf_pipeline` OCSF sınıf ayırıcısını **`type_uid`**
  üzerinden ekliyor ve o kolon bizde **yok**. K8 gereği kolona yazılan tek OCSF
  alanı `class_uid` ve `activity_id`; gerisi `attrs` içinde önekli duruyor.

  Sonuç, zincire konduğunda **derlenen ama koşmayan SQL**: pySigma başarıyla
  çevirir, ClickHouse "böyle bir kolon yok" der. Yani zararı "boşuna bir adım"
  değil, T30'un aramak için var olduğu sessiz yanlış sonuç sınıfının ta kendisi —
  derleme yeşil, sorgu kırık, hiçbir sayaç artmıyor.

  <b>Bu ayrım neden önemli:</b> yanlış gerekçeyle verilmiş doğru bir karar, bir
  sonraki kişinin kararı yanlış sebeple geri almasına açık kapı bırakır. "Faydası
  yok" diye dışarıda tutulan bir şey, biri bir fayda bulduğunda içeri alınabilir;
  "koştuğu anda SQL'i kırıyor" diye dışarıda tutulan bir şey alınamaz.

  Zincire konması hâlâ tartışılabilir, ama ancak `type_uid` görünüme eklendikten
  <b>sonra</b> — ve o, K8'in "yazılan tek gerçek `core`" kararını yeniden açar.
- Windows/Sysmon aileleri — bizim şemamızın hedefi değil.

## Kabul kriterleri

- T30'un önerdiği kapsamdaki kuralların hepsi derleniyor **ve** üretilen SQL
canlı ClickHouse'ta koşuyor.
- Her logsource ailesi için altın örnekle doğrulanmış en az bir kural.
- Eşlenemeyen kural **sessizce geçmiyor**: derleme hattı onu işaretliyor
(T32'nin kapısı).

## Notlar

Ölçülen tuzaklar [T30 ölçümü](../../t30-sigma-olcumu/index.md)'nde; beşi de
orada tabloya bağlı.

En sinsisi tırnaklama tutarsızlığı: aynı SQL içinde hem backtick'li hem tırnaksız
noktalı ad üretilmiş, ve tırnaksız hâli ClickHouse'ta derlenmiyor. Kendi
pipeline'ımız düzleştirilmiş ad ürettiği için bu sorunu baştan atlıyor — ama
`ocsf_pipeline` zincire konursa geri geliyor.

**Bu ikisi aynı sonucun iki yolu:** tırnaklama da, `type_uid` de zincire
konduğunda derlenen ama koşmayan SQL üretiyor. "Dışında" maddesindeki gerekçe bu
yüzden "faydasız" değil "zararlı" — ikisi ayrı ayrı küçük görünen ama aynı
sessiz kırılmaya çıkan bulgular.
