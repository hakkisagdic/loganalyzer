---
title: Şema kararı bir kez verilir
category: concepts
tags: [veri-deposu, log-analiz, kavram, bizigo]
aliases: [ORDER BY kararı, sparseGrams eşiği, keyset sayfalama]
relationships:
  - target: "[[concepts/f1-kapsam-kaynaktan-gelir]]"
    type: extends
  - target: "[[references/f2-kapanis]]"
    type: related_to
  - target: "[[concepts/f1-gecmis-biriktiren-sey-ertelenmez]]"
    type: related_to
sources:
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-kapanis/index.md
  - docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md
  - docs/epic/tickets/normalizasyon/index.md
  - docs/epic/tickets/api-uclari/index.md
  - docs/epic/t08-motor-geri-beslemesi/index.md
source_digest: "sha256-12/v1 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=85c9e2a69f47 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/t08-motor-geri-beslemesi/index.md=5ce87e36f831 docs/epic/tickets/api-uclari/index.md=4e2624c6db9a docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md=7a1070c1c1da docs/epic/tickets/normalizasyon/index.md=74d881911e1b"
summary: ClickHouse sıralama anahtarı, tam metin indeksi ve OCSF/OTel türetmesi geri alınması pahalı kararlar; üçü de ölçümle bağlandı ve ölçümler F2'nin arayüzünü doğrudan kısıtlıyor.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.85
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Şema kararı bir kez verilir

T02'nin ticket'ı kendi işini şöyle tanımlıyor: *"F1'in en geri alınamaz kararını
taşıyor: `ORDER BY` bir kez seçilir, sonra tablo yeniden yazılmadan değişmez"*
(`docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md`). Bu sayfa o sınıftaki
üç kararı — sıralama anahtarı, tam metin indeksi, şema türetmesi — ve her birinin
**ölçülmüş** bedelini birleştiriyor.

## 1 · Sıralama anahtarı: `(owner_group, source_id, ts)`

Üç aday tabloya bağlandı (`docs/epic/f1-teknik-plan/index.md` §6.2). Seçimin
belirleyicisi K17: her sorgu zaten `owner_group` filtresi taşıdığı için sıralama
anahtarının ön ekine onu koymak daima ön-ek taraması veriyor
([[concepts/f1-kapsam-kaynaktan-gelir]]). `(ts, owner_group, source_id)` adayı
"kapsam filtresi her sorguda tam tarama" anlamına geldiği için doğrudan elendi.

Ölçek büyürse değişecek tek şey bölümleme (`toYYYYMMDD` → `toStartOfHour`);
**sıralama anahtarı değişmez** — bu yüzden bir kez doğru seçilmesi gerekiyordu.

### Keyset sayfalama koşullu doğru çıktı

"Derin sayfada sabit süre" iddiası ölçülünce **sıralama anahtarının tam öneki
verildiğinde** doğru çıktı, genel olarak değil (`docs/epic/f1-kapanis/index.md`,
1M satır, yerel compose):

| Sorgu şekli | Sayfa 1 | Derin sayfa |
| --- | --- | --- |
| Filtresiz | 40,7 ms / 377k satır | 38,8 ms / **1M satır** |
| `owner_group` | 45,9 ms / 155k | 57,1 ms / 286k |
| `owner_group` + `source_id` | 17,8 ms / 57k | **13,7 ms / 57k** |

Kapsam kapısı `owner_group`'u zaten eklediği için kısmi fayda garanti; tam
sabitlik `source_id` istiyor. Offset'e üstünlük her hâlükârda net (38,8 ms vs
148,6 ms), ve T10 sayfalamayı keyset olarak yazdı
(`docs/epic/tickets/api-uclari/index.md`).

## 2 · Tam metin indeksi: çalışıyor, ama bir uzunluk eşiği var

`sparseGrams` tokenizer'ı, K4'ün "TR/AR/CJK'yi dile özel tokenizasyon olmadan
çöz" iddiasını taşıyordu. 1M satırla ölçüldü ve iddia **doğru** çıktı — ama
kırılan yer beklenmedik:

| Sorgu | Okunan satır |
| --- | --- |
| `açma` (4 karakter) | 1.000.000 — atlama yok |
| `kullanıcı` (9 karakter) | 1.000.000 — atlama yok |
| `oturum açma` (11 karakter) | 286.720 ✓ |
| `用户登录失败` (6 karakter) | 1.000.000 — atlama yok |
| `用户登录失败，请检查凭据` (12 karakter) | 286.720 ✓ |

**Eşik ~10–11 karakter ve alfabeden bağımsız** — yani CJK ayrıcalıklı bir sorun
değil. Kırılan şey kısa sorgular, ki bir log arama kutusuna yazılan şey tam
olarak odur. Eşleşmeyen sorgu **0 satır** okuyor (11 ms), yani indeks sağlam;
sorun seçicilik.

Bedeli de ölçüldü: `idx_body` **13,32 MiB**, tablo 29,41 MiB — indeks tablonun
**%45'i**. `min_cutoff_length`'i düşürmek kısa sorguları seçici yapar ama zaten
büyük olan indeksi daha da büyütür.

İki ilgili karar aynı yerden çıktı:

- **`lowerUTF8()` ön işlemcisi konmadı** — Türkçe `İ/ı`'da bayt uzunluğu
  değiştiği için skip index'te yanlış negatif üretebiliyor. Arama şimdilik
  büyük/küçük harf **duyarlı**; duyarsız arama kararı ölçümle verilecek
  (`docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md`).
- **`attrs` için `Map(LowCardinality(String), String)`** seçildi, `JSON` değil:
  F1'de ayrıştırılan alanlar zaten string, tipli sıcak alanlar `core`
  kolonlarında. Karar gerçek kardinalite verisiyle yeniden değerlendirilecek —
  şema göçü ucuz olduğu için bilinçli olarak açık bırakıldı.

## 3 · OCSF/OTel: türetilir, materyalize edilmez

K8'in mapping katmanı üç seçenekten birini gerektiriyordu; seçilen **türetme**:
`core` + `attrs` üzerinden hesaplanıyor, saklanan tek OCSF alanı
`ocsf_class_uid` + `ocsf_activity_id` (`docs/epic/f1-teknik-plan/index.md` §5).
Gerekçe: iki şemayı da tam materyalize etmek depolamayı ~2 katına, mapping
bakımını iki katına çıkarırdı.

Türetmenin **nerede** olacağı T07'de kapandı ve cevap API katmanı değil,
ClickHouse görünümü oldu (K30). Belirleyici gerekçe F3'e bakıyor: Sigma kuralları
derleme zamanında ClickHouse SQL'ine çevriliyor ve OCSF alan adlarına vuruyor —
API'de türetme bunu **imkânsız** kılardı
(`docs/epic/tickets/normalizasyon/index.md`). Görünümler materialized **değil**.

### K30'un bedeli de ölçüldü

"OCSF pipeline'ı bedava geliyor" değerlendirmesi ölçümle çürüdü
(`docs/epic/mimari-kararlar/index.md` §3.1): SigmaHQ'nun OCSF pipeline'ı kataloğun
%80'ine dokunuyor ama bizim `events_ocsf` görünümümüze karşı **0 kural** olduğu
gibi çalışıyor; yalnızca ad normalizasyonuyla 3, tam anlamsal eşlemeyle 59
(%1,57). Sebeplerden biri doğrudan görünüm tasarımı: pipeline noktalı yol
üretiyor (`dst_endpoint.ip`), K30'un görünümü düzleştirilmiş ad kullanıyor
(`dst_endpoint_ip`).

Bir de tutarsızlık kayıtta: `severity_num` tek kolon ama görünümler onu iki farklı
ölçekle okuyor (OCSF 0..6 ve OTel 1..24). Katalog OCSF ölçeğini yazıyor, yani
`events_otel.SeverityNumber` yanlış; düzeltme `db/clickhouse/0004` ile
yapıldı. ^[ambiguous: geri besleme belgesi maddeyi "kapandı" işaretliyor, ancak
görünüm tanımının bugünkü hâli bu dilimdeki belgelerden okunamıyor]

## Bu ölçümler F2'yi kısıtlıyor

F1 kapanışı bunları "iyi bilgi" değil **tasarım kısıtı** olarak devretti:

1. **Arama kutusu kısa sorguda tabloyu tarıyor** — ya minimum sorgu uzunluğu
   dayatılmalı, ya `sparseGrams` parametreleri yeniden ölçülüp indeks büyümesi
   göze alınmalı. Sessizce bırakmak, kullanıcının yazdığı her kısa kelimede tam
   tarama demek.
2. **Kaynak filtresi teşvik edilmeli** — keyset sabitliği `source_id` istiyor.

Kısıtların F2'de nasıl karşılandığı [[references/f2-kapanis]] sayfasında.

## Kaynaklar

- `docs/epic/f1-teknik-plan/index.md` — §5, §6.1, §6.2, §6.4
- `docs/epic/mimari-kararlar/index.md` — K8, K30, §3.1, tam metin ölçümü
- `docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md` — `Map` kararı, tokenizer
- `docs/epic/tickets/normalizasyon/index.md` — görünüm kararı ve gerekçesi
- `docs/epic/tickets/api-uclari/index.md` — keyset sayfalama, serbest SQL yasağı
- `docs/epic/f1-kapanis/index.md` — ölçüm tabloları, F2'ye devredilen iki kısıt
- `docs/epic/t08-motor-geri-beslemesi/index.md` — §9, `severity_num` ölçek çelişkisi
