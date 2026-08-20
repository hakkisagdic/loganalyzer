---
title: "Sigma → ClickHouse: §3.1 kararının F3 öncesi doğrulaması"
kind: spec
---

# Sigma → ClickHouse derlemesi

[Mimari kararlar §3.1](../mimari-kararlar/index.md) Sigma kurallarının **derleme zamanında** SQL'e
çevrilip üretilen SQL'in repoda versiyonlanmasına karar vermişti. Seçilen araç
`pySigma-backend-clickhouse`, ve o sırada "Testing" statüsünde olması açık bir
risk olarak bırakılmıştı. Bu belge o riski kapatıyor.

**Sonuç: §3.1 sağlam, araç değişmiyor.** Kalan tek belirsizlik dar ve ölçülebilir —
aşağıdaki prototipte.

<user_quoted_section>Ölçümler bir araştırma ajanı tarafından üretildi ve bu oturumda bağımsızolarak yeniden koşulmadı. Prototip adımı zaten aynı ölçümü projenin kendişemasıyla tekrarlayacak; sayılar orada doğrulanmış olacak.</user_quoted_section>

## Backend'in durumu

| Alan | Değer |
| --- | --- |
| Repo | [clicksiem/pySigma-backend-clickhouse](https://github.com/clicksiem/pySigma-backend-clickhouse) |
| Son sürüm | 1.1.1 (2026-08-10) · 3 ayda 12 sürüm |
| Son commit | 2026-08-10 |
| SigmaHQ statüsü | `testing` |
| Lisans | LGPL-3.0 |
| Yıldız / açık issue | 2 / 0 |
| Python kısıtı | **`>=3.13`** |

Olgunluk durumunun yetkili kaynağı README değil,
[`SigmaHQ/pySigma-plugin-directory`](https://github.com/SigmaHQ/pySigma-plugin-directory)
içindeki `pySigma-plugins-v1.json`.

**"Testing" etiketi yanıltıcı okunabilir.** Dizindeki 31 backend arasında pySigma
**1.x** çekirdeğine geçmiş birkaç taneden biri; `sqlite` ve `surrealql` gibi
alternatifler hâlâ `~=0.11`'e pinli. Yani etiket olgunlaşmamışlığı değil,
gençliği anlatıyor.

Korelasyon kuralları destekleniyor (`event_count`, `value_count`, `temporal`,
`temporal_ordered`, `value_sum`) ve ClickHouse deyimleriyle: `uniqExact()`,
`groupArray()` + `arrayStringConcat()` (ClickHouse'ta `GROUP_CONCAT()` yok),
`toUnixTimestamp()`. Kopyala-yapıştır bir SQLite türevi değil.

## Uyumluluk

Yayınlanmış bir uyumluluk metriği **yok** — ne backend deposu ne SigmaHQ böyle bir
sayı veriyor. Araştırmada ölçülen (SigmaHQ `3c0d3518`, backend 1.1.1, pySigma 1.5.0):

| Konfigürasyon | Başarılı | Oran |
| --- | --- | --- |
| Pipeline'sız | 3752 / 3754 | %99,95 |
| `ocsf_pipeline` ile | 3752 / 3754 | %99,95 |

Düşen iki kural çözülmemiş `%known_cdcs%` placeholder'ı taşıyor — backend kusuru
değil, placeholder değeri verilmediğinde **her** backend'de olan standart pySigma
davranışı.

**Bir tuzak:** `clicksiem/sigma_rules` deposundaki dosya sayıları (3728/3728) %100
uyum gibi görünüyor ama **yanıltıcı** — dönüşüm başarısız olduğunda eski çıktı
dosyası depoda kalıyor. Dosya sayımına değil CI log'una bakılmalı.

## Alan eşleme — en önemli bulgu

### OCSF: hazır geliyor

[`SigmaHQ/pySigma-pipeline-ocsf`](https://github.com/SigmaHQ/pySigma-pipeline-ocsf)
— **MIT**, `state: stable`, son commit 2026-07-24. Plugin dizini `~=0.11` diyor
ama PyPI metadata'sı `pysigma>=1.0.0,<2.0.0`; yani ClickHouse backend'iyle sürüm
çakışması yok ve ikisi birlikte kuruluyor.

Kompozisyon fiilen denenmiş:

```sql
-- pipeline YOK:
SELECT * FROM logs WHERE (Image ILIKE '%\\whoami.exe' OR OriginalFileName='whoami.exe')
                     AND CommandLine ILIKE '%/priv%'

-- ocsf_pipeline İLE:
SELECT * FROM logs WHERE type_uid=100701
  AND ((`process.name` ILIKE '%\\whoami.exe' OR `process.file.internal_name`='whoami.exe')
   AND `process.cmd_line` ILIKE '%/priv%')
```

Alan eşlemesini yapıyor, OCSF sınıf ayırıcısını (`type_uid`) ekliyor ve noktalı
alan adlarını ClickHouse için backtick'liyor. 36 logsource kategorisi, ~235 alan
eşlemesi — ama `products` listesinde yalnız `windows` var, yani **ağırlıklı**
**Windows/Sysmon**.

### OpenTelemetry semconv: yok

SigmaHQ plugin dizininde `opentelemetry` / `otel` / `semconv` **hiç geçmiyor**.
Mevcut beş pipeline: `ocsf`, `ossem`, `rclinuxedr`, `sysmon`, `windows`.

**Bunun maliyeti:** [K8](../mimari-kararlar/index.md)'in "ikisi birden" kararı
gereği OCSF **bedava**, **OTel tarafını proje yazacak ve bakacak.** İyi haber:
`ocsf.py` (831 satır, MIT) birebir şablon; asıl iş Sigma taxonomy → OTel semconv
tablosunu doldurmak.

## Asıl risk teknik değil

Backend **üç aylık, iki yıldızlı, tek geliştiricili** (bus factor 1). Ama §3.1'in
build-time tercihi bu riski beklenmedik biçimde zararsızlaştırıyor: **üretilen SQL**
**zaten repoda versiyonlanacak**, dolayısıyla proje terk edilse bile mevcut kurallar
çalışmaya devam eder. Kaybedilen şey yalnızca *yeni* kural derleme yeteneği, ve
LGPL-3.0 fork'a izin veriyor.

Bu, §3.1'i yeniden gerekçelendiriyor: karar "runtime'da Python istemiyoruz" diye
verilmişti, sonra "sıcak yolda belirsizlik istemiyoruz"a dönmüştü; şimdi üçüncü
bir gerekçe kazandı — **tedarik zinciri riskini soğuruyor.**

## F3 öncesi yapılacaklar

**Prototip (yarım gün, F3'ü bloklamaz).** Kapatılacak tek belirsizlik: OCSF
pipeline'ı Windows ağırlıklı olduğuna göre, Linux/cloud/proxy kurallarında
muhtemelen no-op kalıyor. O zaman derleme **başarılı olur ama SQL var olmayan**
**kolonlara referans verir** — asıl tehlike bu, derleme hatası değil.

1. Kataloğu `ocsf_pipeline` ile derle; çıktısı pipeline'sız hâliyle **birebir**
**aynı** kalan kuralları say → bunlar eşlenmemiş kurallar. Logsource'a göre kır.
2. Üretilen SQL'i projenin **gerçek** ClickHouse kolon adlarıyla karşılaştır
(pipeline OCSF 1.5'e göre eşliyor).
3. OTel için `ocsf.py`'yi şablon alıp iskelet çıkar; elle yazılacak kategori
sayısını ölç.

**Şimdi karara bağlanacaklar:**

| Konu | Karar |
| --- | --- |
| Sidecar imajı | **Karşılanıyor.** Backend `requires-python >=3.13` istiyor; `sidecar/Dockerfile` zaten `python:3.13-slim` üstünde. Yükseltme gerekmiyor — ama taban imaj düşürülürse pySigma sessizce kurulamaz hâle gelir |
| LGPL-3.0 | Build-time kullanım sorun değil (SQL çıktısı türev eser değil), hukuk tarafında bir kez teyit edilsin |
| Üretilen SQL | Repoda versiyonlanacak — bu artık yalnızca tekrarlanabilirlik değil, **terk edilme sigortası** |

## Alternatifler — hiçbiri kararı değiştirmiyor

### `rsigma` — varsayımımız yanlıştı, sonuç yine de aynı

[`timescale/rsigma`](https://github.com/timescale/rsigma) — MIT, 133 yıldız, Rust,
son sürüm v0.21.0 (2026-08-05), tempo yüksek. Bakım riski yok; risk ters yönde:
**pre-1.0 ve altı aylık.**

"Runtime eşleştirme motoru, dolayısıyla mimarimizle alakasız" varsayımı **yanlış**.
rsigma ikisini birden yapıyor: in-memory evaluator **ve** `rsigma-convert` içinde
pluggable `Backend` trait'i olan bir kural→sorgu derleyicisi. Postgres backend'i
tam da bizim modelimizi uyguluyor — bağımsız `SELECT` metni üretiyor, golden SQL
testleri var.

Ama **ClickHouse backend'i yok** (hedefler: `postgres`, `lynxdb`, `fibratus`,
`test`). Dahası, native backend'i olmayan hedefler için rsigma harici `sigma-cli`'ye
**subprocess olarak deleg ediyor** — yani ClickHouse istendiğinde Python
bağımlılığını kaldırmıyor, üstüne bir de Rust binary'si ekliyor. Backend'ler
binary'ye statik derlendiği için ClickHouse desteği fork ya da upstream PR
gerektirir: pySigma'nın %99,95 kapsamla bedava verdiği şeyin bakımını üstlenmek.

**Mimari olarak alakasız değil, pratik olarak gereksiz.**

### Ürünler

| Ürün | Sigma? | ClickHouse? | Yeniden kullanılabilir? |
| --- | --- | --- | --- |
| Uncoder.io / SOC Prime | ✅ 26 platform | ❌ yok (en yakını `athena`, `snowflake`) | — |
| HyperDX (ClickStack) | ❌ hiç geçmiyor | ✅ | — |
| OpenObserve | ❌ hiç geçmiyor | ✅ | — |
| SigNoz | ❌ hiç geçmiyor | ✅ | — |
| Matano | ✅ pySigma backend'i var | ❌ AWS/Iceberg | ⚠️ **19 aydır hareketsiz** |
| RunReveal | ✅ streaming detections | ✅ | ❌ kapalı runtime matcher |

RunReveal'in parser'ı açık (`runreveal/sigmalite`, Go, Apache-2.0) ama runtime,
SQL üretmiyor. ClickHouse şirketinin kendi iç SIEM'i de RunReveal üstünde koşuyor —
yani "ClickHouse şirketinin Sigma→SQL ürünü" diye bir şey **yok**.

### .NET tarafı — bulunamadı

NuGet'te Sigma kuralı ayrıştıran ya da çeviren **hiçbir paket yok**. GitHub'da dört
aday var, hiçbiri kullanılabilir kütüphane değil:

- `Saeros-Security/Saeros` — en ciddi native C# Sigma kodu (logsource/alan eşleme,
korelasyon tipleri), ama Sigma'yı **kendi iç formatına** çeviriyor, SQL üretmiyor;
converter NuGet paketine dahil değil ve lisans **AGPL-3.0-only**.
- `JPCERTCC/YAMAGoya` — gerçek bir ANTLR grameri var ama runtime değerlendirme,
lisans belirsiz (NOASSERTION), 9 aydır hareketsiz.
- `bbougot/Sonar` — açıklaması Sigma diyor, repoda tek eşleşme yok.
- `3CORESec/S2AN` — Sigma→MITRE ATT&CK eşlemesi, çevirici değil. ~3,7 yıl ölü.

**Python bağımlılığından .NET tarafına kaçış yolu yok.** Build-time modelinde bu
bağımlılık zaten runtime'a değil **CI'a** düşüyor, dolayısıyla maliyeti sınırlı.

## Kopyalanacak referans implementasyon

[`clicksiem/sigma_rules`](https://github.com/clicksiem/sigma_rules) planladığımız
şeyin **çalışan hâli**: günlük GitHub Actions cron'u, `sigconvert.py -b clickhouse`
ile SigmaHQ'nun üç kural dizinini derliyor ve üretilen **7.447 dosyayı repoya**
**commit'liyor**. Her dosya gömülü SQL taşıyor.

Kendi derleme akışımızı yazarken doğrudan iskelet alınabilir — ve
[uyumluluk bölümündeki tuzak](#uyumluluk) da buradan geliyor, dosya sayımına
güvenilmemeli.
