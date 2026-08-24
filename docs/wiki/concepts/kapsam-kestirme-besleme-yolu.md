---
title: Kestirme besleme yolu canlı yolu ölçüsüz bırakır
category: concepts
tags: [log-analiz, veri-deposu, kavram, bizigo]
aliases: [S02a, seed golden kestirmesi, kayıp satırlar]
relationships:
  - target: "[[concepts/sessiz-yanlis-davranis]]"
    type: implements
  - target: "[[concepts/kapsam-olcumun-sinirini-yazmak]]"
    type: related_to
  - target: "[[references/kapsam-nerede-kalindi]]"
    type: related_to
sources:
  - docs/epic/fs-simulatorler/index.md
  - docs/epic/is-envanteri/index.md
  - docs/epic/devir-notu-kota-kesintisi/index.md
source_digest: "sha256-12/v1 docs/epic/devir-notu-kota-kesintisi/index.md=769f322740d7 docs/epic/fs-simulatorler/index.md=22bb02d19d29 docs/epic/is-envanteri/index.md=c1ee55fd6048"
summary: Boru hattını test verisiyle besleyen kestirme yol (doğrudan ClickHouse'a yazan seed) canlı yolu yıllarca ölçüsüz bıraktı; ilk gerçek syslog koşumu 385 satırın 372'sinin sessizce kaybolduğunu gösterdi. S02a ile kök neden bulundu ve kapandı.
provenance:
  extracted: 0.9
  inferred: 0.1
  ambiguous: 0.0
base_confidence: 0.82
lifecycle: draft
lifecycle_changed: 2026-08-24
tier: core
created: 2026-08-24T17:05:00Z
updated: 2026-08-24T17:05:00Z
---

# Kestirme besleme yolu canlı yolu ölçüsüz bırakır

Bir boru hattını **kısa devre yapan** bir test aracı, kısa devrenin atladığı
her kademeyi ölçüm dışı bırakır — ve o kademeler yeşil görünmeye devam eder,
çünkü hiç koşmamışlardır. Bu depoda bu kalıp üç kez ölçüldü; en pahalısı
simülatör fazının ilk gününde çıktı.

## Kestirme

`docs/epic/fs-simulatorler/index.md` §1: boru hattını besleyen tek araç
`bizigo seed golden`'dı ve o **doğrudan ClickHouse'a** yazıyordu — CLI'ın tek
bağlantı seçeneği `--clickhouse`. Sonuç tek cümlede yazılı: kodlama tespiti,
WAL, ham arşiv ve dispatcher kademeleri **ölçüm verisiyle hiç koşmuyordu**.

## S02'nin ilk koşumu — 372 kayıp

`fw-ankara-01` ve `lb-web-01` profillerinden collector'a (syslog TCP 5140)
**385 satır** basıldı. Sayılar aynı anda alındı:

```
API  /internal/ingest/stats : accepted_records 385 · processed_records 385
ClickHouse events           : 13 satır (2 ok · 11 failed)
```

Aradaki **372 kayıt hiçbir yerde yok**: API'de hata yok, collector bir şey
loglamıyor, `ParsingSink` tek kayıt düşürmüyor, sink 10.000 satırda ya da 2
saniyede boşaltıyor — 90 saniye beklenerek "henüz yazılmadı" açıklaması da
elendi. Hata yok, sayaç yok, belirti yok:
[[concepts/sessiz-yanlis-davranis]]'ın tam ortası.

## S02a — kök neden ve kapanış

**Şüphelilerin hiçbiri değildi.** `Ingest:WorkerCount` ve sink'in flush yolu
ölçüldü, ikisi de temiz çıktı. Kayıp boru hattında değil, **ClickHouse'a
yazıldıktan sonra**:

`events` tablosunda `TTL toDateTime(ts) + INTERVAL 90 DAY` var. Parser `ts`'yi
satırın kendisinden çıkarıyor ve `catalog/parsers/*/samples/` altındaki vendor
örnekleri 2015–2022 tarihleri taşıyor. ClickHouse süresi dolmuş satırı parçayı
oluştururken atıyor — **ama istemciye "yazdım" diyor**.

| Aşama | Ne diyordu |
| --- | --- |
| Basıcı | 100 satır |
| `accepted_records` / `processed_records` | +100 / +100 |
| ClickHouse `query_log` | `INSERT … written_rows = 3` |
| `events` tablosu | **+0** |
| Hata / log / sayaç | **hiçbiri** |

Kesin kanıt: tek bir `INSERT` içinde 2020 ve 2026 tarihli iki satır yazıldı,
yalnızca ikincisi tabloya girdi, dönen sayı ikisini de saydı.

**En sinsi ayrıntı:** hangi satırın yaşadığını **parse'ın başarısı**
belirliyordu. Parse başarısız olunca `ts` alınma zamanına düşüp satır
kurtuluyor; başarılı olunca gerçek damga geliyor ve satır ölüyor.

### Neden ürün hatası, test verisi sorunu değil

Örnek dosyalar düzeltilse bile mekanizma duruyor. Sahadaki üç tetikleyicisi
yazılı: **saati yanlış cihaz**, retention'dan eski bir arşivin **replay**'i, ve
`ts` alanını yanlış seçen bir **parser**. Sonuncusu en kötüsü — parser hatası
*görünür yanlış değer* yerine *görünmez veri kaybına* dönüşüyor.

### Düzeltme: say + logla, satır yine düşsün

`ts`'yi pencereye çekmek kaybı **başka bir sessiz yanlışla** değiştirirdi:
saati yanlış cihazın olayları bugüne yığılır ve korelasyon sessizce bozulur.
Ham arşiv durduğu için bu, K12 replay ile geri kazanılabilir bir kayıp —
zincirin halkaları: [[concepts/f1-ham-sadakat-zinciri]].

- `ClickHouseEventSink.Expired` — pencere dışı satırları sayıyor, boşaltma
  başına tek uyarı logluyor.
- **`Written` düzeltildi**: sayaç *gönderileni* değil hayatta kalanı söylüyor.
  Düzeltilmeseydi tabloda olmayan satırlar sayılırdı ve düzeltmenin tamamı
  anlamsız olurdu.
- `/internal/ingest/stats` artık `stored { written, expired, dropped,
  buffered }` ve hesaplanan **`unaccounted`** veriyor — `processed_records`
  her zaman *çözüleni* sayıyordu, depolananı değil.
- Basıcı örnek satırın damgasını şimdiye kaydırıyor (`SyslogEmitter.WireLine`);
  kaydırıcı yeniden yazılmadı, var olan `SampleTimeRewriter` ortak eve taşındı.

Uçtan uca sonuç: **öncesi** 100 basıldı → 0 tabloya; **sonrası** 30 basıldı →
30 tabloya, tamamı `parse_status = ok`, `time_source = parsed`.

### Bekçinin kendisi de kaybetti

Dört bekçinin dördünün de kırmızı yanabildiği ölçülüp geri alındı. Ama
öğretici olan şu: simülatör bekçisi ilk hâlinde `SampleTimeRewriter`'ı
**doğrudan** çağırıyordu; basıcıdan kaydırma kaldırıldığında **yeşil
kalıyordu** — koruduğunu iddia ettiği şeyi korumuyordu. Basıcıya tek bir dikiş
yeri açılıp bekçi oradan geçirilince kırmızı yandı. Ayrıca çıplak yıl arayan
ilk sürüm dört yanlış pozitif verdi (`10.10.10.10/1985` bir UDP portu,
`2001:db8::` ve `2001:470::` IPv6 önekleri).

## Aynı kalıbın iki kardeşi

`docs/epic/is-envanteri/index.md` iki örnek daha kaydediyor — ikisinde de
ölçüm, ürünün gerçekte koşan yolundan **başka bir şeyi** besliyordu:

- **İki korpus.** Korpus `catalog/sigma/rules/`'a terfi ettirildi, düzeltme
  `prototypes/`'ta yapıldı; `measure.py` birini, Kapı 3 diğerini okuyordu. "Aynı
  kural setinin iki ölçümü" diye okunan iki sayı iki farklı korpustandı.
  Sürüklenme kapısı bunu **göremez** ve görmemesi doğru — çıktıyı girdiye
  tutuyor, girdinin kendi kopyasından ayrışmasını değil.
- **Yanlış veri.** Sigma kapsam ölçümü `%0` üretti; sebep eşleme değil
  **veri** — o oranın paydası ayrıca dört kez oynadı
  ([[concepts/f3-oranin-paydasi]]) — ClickHouse'daki 1M satır tek-vendor'lu sentetik benchmark
  verisiydi (D5).

## Alınacak kural

Bir ürünün **canlı yolu**, ölçüm verisiyle en az bir kez uçtan uca koşmadıkça
çalıştığı varsayılmaz. Kestirme besleme araçları hızlıdır; hızlarının bedeli
atladıkları kademelerin **ölçüm dışı** kalmasıdır ve bu bedel görünmez.
Ölçümün neyi kapsamadığını yazma disipliniyle birlikte okunmalı:
[[concepts/kapsam-olcumun-sinirini-yazmak]].

## Yapılmayanlar (kayıt)

- `EventRetentionChainTests` **yazıldı, koşturulmadı** — Testcontainers
  gerekiyor, ölçüm anında swap %89'daydı. Koşturulduğunda kanıtlayacağı şey
  sayaç-sayaç değil **sayaç-tablo** eşitliği.
- API imajı yeni sayaçlarla yeniden kurulmadı; `stored`/`unaccounted` canlı
  yığında henüz görünmüyor.

## Kaynaklar

- `docs/epic/fs-simulatorler/index.md` — §1 ölçülen boşluk, §10.5 S02/S02a
- `docs/epic/is-envanteri/index.md` — aynı girdinin iki kopyası, D5
- `docs/epic/devir-notu-kota-kesintisi/index.md` — §6 hata sınıfları
