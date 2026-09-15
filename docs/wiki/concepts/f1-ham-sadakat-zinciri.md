---
title: Ham sadakat bir zincirdir
category: concepts
tags: [log-analiz, veri-deposu, kavram, bizigo]
aliases: [ham arşiv, byte fidelity, protocol none, encoding nop]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[skills/f1-veri-kaybeden-depoyla-tasarim]]"
    type: related_to
  - target: "[[skills/f1-deklaratif-parser-motoru]]"
    type: related_to
  - target: "[[projects/bizigo-loganalyzer/bizigo-loganalyzer]]"
    type: part_of
sources:
  - docs/epic/mimari-kararlar/index.md
  - docs/epic/f1-teknik-plan/index.md
  - docs/epic/f1-kapanis/index.md
  - docs/epic/tickets/ingest-boru-hatti/index.md
  - docs/epic/tickets/ham-arsiv/index.md
  - docs/epic/tickets/normalizasyon/index.md
  - docs/epic/tickets/replay/index.md
  - docs/epic/tickets/iskelet-ve-ci/index.md
  - docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md
source_digest: "sha256-12/v1 docs/epic/f1-kapanis/index.md=93aa551b9c35 docs/epic/f1-teknik-plan/index.md=8467cc3e3e94 docs/epic/mimari-kararlar/index.md=8b897734c68f docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md=7a1070c1c1da docs/epic/tickets/ham-arsiv/index.md=f7b6e1d7e3a0 docs/epic/tickets/ingest-boru-hatti/index.md=bc2d3270ace8 docs/epic/tickets/iskelet-ve-ci/index.md=b0acf06b322b docs/epic/tickets/normalizasyon/index.md=74d881911e1b docs/epic/tickets/replay/index.md=a6e378b9614b"
summary: Cihazdan replay'e uzanan bayt zincirinin her halkası ayrı bir kararla tutuluyor; bir halka koparsa zincirin tamamı değersiz oluyor ve kopuş hiçbir belirti üretmiyor.
provenance:
  extracted: 0.88
  inferred: 0.12
  ambiguous: 0.0
base_confidence: 0.84
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:30:09Z
updated: 2026-08-24T17:30:09Z
---

# Ham sadakat bir zincirdir

F1'in bitti tanımındaki cümlelerden biri şu: *"ham hali kaybolmadan saklanır;
parser'ı düzeltip son 7 günü yeniden işleyebilirim"*
(`docs/epic/f1-teknik-plan/index.md` §0). Bu tek cümle **beş ayrı katmanda**
karar gerektiriyor ve katmanlardan biri koptuğunda geri kalanların hepsi
anlamsızlaşıyor — üstelik kopuş genellikle sessiz.

```
cihaz → collector → OTLP → WAL → ham arşiv → replay
```

## Halka 1 · Collector mesajı ayrıştırmamalı

OTel syslog receiver'ında `preserve_to` benzeri **ham satır saklama seçeneği
yok**; RFC3164/5424 modunda mesaj alanlara ayrılıyor ve orijinal satır geri
alınamıyor. Bu bulgu K24'ü ve mimarinin tamamını bağladı: collector
`protocol: none` ile çalışıyor, syslog başlık ayrıştırması **bize** düşüyor
(`docs/epic/mimari-kararlar/index.md` §3.3).

Bunun yan etkisi aslında bir kazanç: ayrıştırmanın tamamı YAML motorunda
toplandığı için **replay'de birebir aynı kod koşuyor**. İki ayrı ayrıştırıcı
olsaydı replay ile canlı yol sessizce ayrışırdı.

Plandaki ifadeyle sevk edilenin ayrıştığı yerlerden biri de bu: plan
"collector RFC modunda ayrıştırsın" diyordu (`docs/epic/f1-kapanis/index.md`
sapma tablosu).

## Halka 2 · Kodlama — iki kez yanlış cevaplanmış bir soru

Burası deponun en öğretici düzeltme kaydı, çünkü **belgeler arasında hâlâ
görünür**:

| Belge | Ne diyor |
| --- | --- |
| `docs/epic/f1-teknik-plan/index.md` §2.2 | `encoding: nop` **ZORUNLU** |
| `docs/epic/tickets/ingest-boru-hatti/index.md` | Sevk edilen `protocol: none` + `encoding: nop` |
| `docs/epic/mimari-kararlar/index.md` K27 | `nop` **denendi ve reddedildi**; doğrusu `iso-8859-1` |

`nop`'un neden düştüğü ölçülmüş: UDP yolunda varsayılan bir `line_end_pattern`
olduğu için açılışta hata veriyor; TCP yolunda bölücü `NoSplitFunc`'a düşüyor ve
**syslog çerçevelemesi kayboluyor** — çökme yok, TCP akışının tamamı tek kayda
dönüşüyor. Yani `nop` seçeneğinin *var olduğu* doğrulanmıştı, *çalıştığı* değil.

`iso-8859-1` üçünü birden veriyor: çerçeveleme korunuyor, bayt ↔ kod noktası
eşlemesi tersinir, telde geçerli UTF-8 taşınıyor. Tel kodlaması
`bizigo.wire_encoding` özniteliğiyle **açıkça** bildiriliyor; çözücünün tahmin
etmesi gerekmiyor.

Sonrasında .NET tarafındaki tespit sırası devreye giriyor
(`docs/epic/f1-teknik-plan/index.md` §2.4): envanterdeki `encoding` → UTF-8
doğrulaması → kaynağın yedek kod sayfası → `latin1`. `windows-1254` için
`Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` **zorunlu**;
depolamadan önce UTF-8 NFC uygulanıyor ve `encoding_detected` saklanıyor.

**Türkçe tuzağı iki yerden birden giriyor.** `tr-TR` kültüründe `ToLower()`
`I → ı` yapıyor ve aramayı sessizce bozuyor; bu yüzden T01 CA1304/CA1305/CA1310/
CA1311 kurallarını **hata** seviyesine çekti
(`docs/epic/tickets/iskelet-ve-ci/index.md`). Aynı sebep ClickHouse tarafında da
karar verdirdi: tam metin indeksine `lowerUTF8()` ön işlemcisi konmadı, çünkü
Türkçe `İ/ı`'da bayt uzunluğu değişiyor ve skip index'te bu **yanlış negatif**
demek (`docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md`).

## Halka 3 · Dayanıklılık sınırı WAL

Ack, ham batch WAL'a yazılıp fsync edildikten **sonra** veriliyor
(`docs/epic/f1-teknik-plan/index.md` §2.3). Parse başarısız olsa bile veri kayıp
değil — ham arşivin varlık sebebi bu. WAL doluysa `503 + Retry-After` dönüyor ve
backpressure zinciri collector'ın `file_storage` kalıcı kuyruğuna devrediliyor;
kendi kuyruğumuz yazılmıyor.

K28 bu halkayı bir sonrakine dikiyor: **WAL yükü = arşiv satırı.** Tek NDJSON
formatı, tek codec. Böylece yükleyici bir dönüştürücü değil **kopyalayıcı**
oluyor ve iki formatın sessizce ayrışması mümkün olmuyor. `owner_group` /
`source_id` alanları WAL aşamasında boş yazılıyor — alanın **varlığı** formatın
parçası, değeri değil.

## Halka 4 · Arşivde string değil bayt

Nesne biçimi NDJSON + ZSTD, kayıt içinde `raw_b64` **orijinal baytlar**
(`docs/epic/f1-teknik-plan/index.md` §7.1). Gerekçe tek cümle: kodlama tespiti
yanlış çıkarsa replay düzeltebilsin.

`raw_ref`'in ne taşıyacağı T04'te açık kaldı, T07'de kapandı ve cevap **bayt
konumu değil arşiv ön eki** oldu (K29). Sebep yapısal: ingest boru hattı ile
yükleyici bilinçli olarak bağımsız çalışıyor, yani olay ClickHouse'a yazılırken
nesne henüz oluşmamış ve `offset` bilinemiyor. Ayrı bir `raw_index` tablosu
**ikinci bir gerçek kaynak** doğururdu — manifest'in (bkz.
[[skills/f1-veri-kaybeden-depoyla-tasarim]]) önlemek için var olduğu hata
sınıfının aynısı. Bedeli dürüstçe yazılı: tek kaydı okumak için nesnenin açılıp
`event_id` taranması gerekiyor; replay zaten nesnenin tamamını okuduğu için ona
maliyeti yok.

## Halka 5 · Replay aynı kodu koşar

`ALTER TABLE … REPLACE PARTITION`, gün granülerliğinde ve atomik
(`docs/epic/tickets/replay/index.md`). Zincirin bu halkasını ayakta tutan dört
karar:

1. **Kuru koşu ile gerçek çalıştırma aynı akışı koşuyor**, tek fark son adımın
   atlanması. İki ayrı yol yazmak, raporun gerçeği öngörmediği bir gün getirirdi
   *"ve o günü kimse fark etmezdi, çünkü raporu kontrol etmenin yolu raporun
   kendisi."*
2. `ingested_at` ve `parse_generation` fark **sayılmıyor** — her replay'de
   değişirler, sayılsalardı rapor "her satır değişti" derdi.
3. Sürümsüz parser sabitlemesi **reddediliyor** (`400`): "en güncel parser" ile
   koşmak, aynı komutun iki ay sonra farklı sonuç vermesi demek.
4. Varsayılan **kuru koşu**; `dryRun` gönderilmezse yazma yapılmıyor.

Filtreli replay'de tuzak açıkça bir teste bağlanmış: filtre dışı satırlar gölge
tabloya **değiştirilmeden kopyalanmazsa** `REPLACE PARTITION` onları siliyor.

## Zincir ölçüldü: 103 bayt girdi, 103 bayt çıktı — ve yol beş kez kırıktı

F1 kapanışının doğrulama turu bu sayfanın asıl dersi. Uçtan uca bayt sadakati
sonunda **birebir sha256** ile doğrulandı, ama oraya varmak için beş ayrı katman
düzeltildi ve *her biri bir öncekini düzeltmeden görünmüyordu*
(`docs/epic/f1-kapanis/index.md`).

Sonuncusu bu sayfa için en anlamlısı: manifest **mikrosaniye**, ClickHouse
`DateTime64(3)`. Kırpılan `ts` daima `ts_from`'dan küçük kaldığı için **tek
olaylı nesne hiç bulunamıyor**, ve 404 *"henüz yüklenmemiş olabilir"* diyerek
yanlış yere yönlendiriyordu. Zincir sağlamdı; onu okuyan sorgu yanlıştı.

Kapanışın kendi ifadesi: **doğrulanmamış her katman kırıktı ve hiçbiri kendini
belli etmedi** — yani zincirin tamamı [[concepts/sessiz-yanlis-davranis]]
sınıfına açık.

## Açık kalan

- `protocol: none` hâlâ "experimental" etiketli; yedek plan (`rfc3164` + OTTL ile
  gövde kopyalama) **bir kez bile doğrulanmadı**
  (`docs/epic/tickets/ingest-boru-hatti/index.md`).
- Replay sırasında canlı ingest'in bozulmadığı yük altında sınanmadı; kuru koşu
  ile gerçek çalıştırmanın aynı sonucu verdiği uçtan uca tek testte
  gösterilmedi (`docs/epic/f1-kapanis/index.md`).

## Kaynaklar

- `docs/epic/mimari-kararlar/index.md` — K24, K27, K28, K29, §3.3
- `docs/epic/f1-teknik-plan/index.md` — §0, §2.2, §2.3, §2.4, §7.1, §7.2
- `docs/epic/tickets/ingest-boru-hatti/index.md` — WAL, kodlama, collector
- `docs/epic/tickets/ham-arsiv/index.md` — nesne biçimi, `raw_ref` açık kalemi
- `docs/epic/tickets/normalizasyon/index.md` — K29'un kapanışı
- `docs/epic/tickets/replay/index.md` — dört tasarım noktası, filtreli replay
- `docs/epic/tickets/iskelet-ve-ci/index.md` — Türkçe kültür lint kuralı
- `docs/epic/tickets/depolama-ve-kapsam-kapisi/index.md` — `lowerUTF8()` ön işlemcisinin reddi
- `docs/epic/f1-kapanis/index.md` — beş kırık, mikrosaniye/milisaniye bulgusu
