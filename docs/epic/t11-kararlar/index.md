---
kind: spec
title: "T11 — Replay: kararlar ve açıkta kalanlar"
---

# T11 — Replay ve kuru koşu fark raporu

> ⚠️ **Bu belge geriye dönük yazıldı.** Kaynağı kod, commit geçmişi ve F1
> kapanışı. Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan
> gerekçeler kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen
> alternatifler kayıtta yok.

**Yöneten karar:** K12 · **Ticket:** `tickets/replay`

## Ne yaptı

Parser düzeltildiğinde geçmişi yeniden işleme. Ham arşivden okuyup **sabitlenmiş
parser sürümüyle** yeniden ayrıştırıyor, gölge tabloya yazıyor, ve
`ALTER TABLE … REPLACE PARTITION` ile değiştiriyor. Granülerlik bir gün.

Yanına iki şey geliyor: değiştirmeden önce ne olacağını gösteren **kuru koşu
fark raporu**, ve arşivde eksik nesne varsa duran bir **manifest doğrulaması**.

## Kodda görünen kararlar

### `REPLACE PARTITION`, `FINAL` değil

Alternatif `ReplacingMergeTree` + `FINAL` olurdu: satır bazında değiştir, okuma
tarafında birleştir. Seçilmedi çünkü `FINAL` **her sorguya** maliyet bindiriyor
ve replay nadir bir işlem — nadir bir işlemin bedelini sıcak yola yaymak.

`REPLACE PARTITION` bölümü tek işlemde değiştiriyor ve sorgu tarafı hiçbir şey
ödemiyor. Bedeli bölüm bütünlüğü: bölümün **tamamı** yazılmak zorunda, yani
filtreli replay'de filtre dışı satırların da gölgeye kopyalanması gerekiyor.

### Filtre dışı satırlar gölgeye kopyalanıyor

Ticket bunu "çözülmeli" diye açık bırakmış; kod kopyalama yönünde çözmüş
(`ApplyPartitionsAsync`). Kopyalanan satır sayısı raporda `CopiedUnchanged`
olarak duruyor — yani yapılan iş görünür.

Bir entegrasyon testi bu davranışı **belgeliyor**:
`Filtre_disi_satirlar_kopyalanmazsa_kayboluyor`. Testin işi doğruluğu sınamak
değil, tuzağı göstermek — `REPLACE PARTITION` kopyalanmayan satırı sessizce
siliyor.

### Parser sürümü sabitleniyor, "en güncel" kullanılmıyor

Uç `parser_id` verildiyse `parser_version`'ı da zorunlu kılıyor. Gerekçe kodda
yazılı: sürümsüz sabitleme, aynı komutun iki ay sonra farklı sonuç vermesi
demek — replay **tekrarlanabilir** olmak zorunda.

### Eksik nesne bir hata değil, bir duruş

Manifest'te olup arşivde bulunamayan nesne varsa uç **409** dönüyor ve
`ContinueOnMissingObjects` isteniyor. Gövde eksik nesneleri **listeliyor**:
kullanıcı "devam edeyim mi" kararını hangi nesnelerin eksik olduğunu görmeden
veremez.

Bu, manifest'in (K25 koruma #4) var olma sebebi: onsuz replay "yedi gün yerine
beş gün" döner ve kimse fark etmez.

### Kuru koşu varsayılan

`dry_run` alanı gönderilmezse rapor üretiliyor, yazma yapılmıyor. Varsayılanı
"uygula" yapmak, bir alan unutulduğunda üretim verisini değiştirmek olurdu.

## Bugün duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `ReplayStoreTests` (entegrasyon) | Gölge tablo ve `REPLACE PARTITION` davranışı; filtre dışı satırın kopyalanmazsa kaybolduğu |
| `ReplayOpenPartitionTests` (birim) | Açık bölümün replay dışında kaldığı, gün dönümünde sınırın kaydığı, bayrakla dâhil edilebildiği |
| `ProducesContractTests` | Ucun üç yolunun da (200/400/409) yanıt tipi bildirdiği |

## F1'in kapalı sandığı kapı — açıkmış

Ticket'ın kabul kriterlerinden biri şuydu:

> Replay sırasında canlı ingest bozulmuyor; sorgular tutarlı sonuç veriyor.

F1 kapanışı bunu *"`REPLACE PARTITION` atomik olduğu için beklenen doğru
davranış, ama yük altında sınanmadı"* diye bıraktı. **Ölçmemiş, mantık
yürütmüştü.**

T27'de bakıldığında iddia **yanlış** çıktı, ve sebebi atomikliğin ne söylediğiyle
ilgili. Motor önce mevcut satırları okuyup gölge tabloyu kuruyor
(`LoadExistingAsync`), sonra bölümü değiştiriyor. O iki adım arasında canlı
ingest'in **aynı bölüme** yazdığı her satır gölgede yok — ve değiştirme onu
sessizce siliyor.

Atomiklik *"yarım bölüm görünmez"* diyor. *"Anlık görüntüden sonra geleni
korurum"* demiyor. F1 birinciden ikincisini çıkarmıştı.

**Kapatma biçimi:** açık bölüm (bugünün bölümü) varsayılan olarak replay'in
dışında, ve atlandığı **rapora yazılıyor** (`SkippedOpenPartitions`). Sessiz
veri kaybı görünür bir karara çevrildi; ingest'i durdurduğunu bilen operatör
`allow_open_partition` ile bugünü de kapsayabiliyor.

Kapatma **yapısal**, ölçümle değil — yük altında hâlâ ölçülmedi.

## Açıkta kalanlar

| # | Ne | Durum |
| --- | --- | --- |
| 1 | **Kuru koşu gerçek çalıştırmayla aynı sonucu veriyor mu** | Ticket'ın ilk kabul kriteri. Parçalar ayrı sınandı, uçtan uca tek testte gösterilmedi. `F2FlowTests`'te `Skip` iskeleti ve numaralı adımlar duruyor; Postgres manifest satırları **ve** S3 nesneleri gerekiyor |
| 2 | **Replay yük altında** | Yapısal kapatma var, ölçüm yok |
| 3 | **CLI komutu** | F1 kapanışında bilerek atlandı: CLI'nin tüm DI grafiğini (ClickHouse + Postgres + S3 + katalog) barındırması gerekiyordu, uç aynı yeteneği veriyor |
| 4 | **İş durumu izleme** (kuyruk, ilerleme) | Ticket kapsamında yazıyor; kodda yok. Uç senkron koşuyor ve raporu dönüyor. Gerekçesi **kayıtta yok** |
| 5 | **İdempotanslık** | Kabul kriterinde var; `ApplyPartitionsAsync` eski gölgeyi düşürüp yeniden kuruyor, yani aynı replay iki kez koşabiliyor. Ama bunu sınayan bir test **yok** |

## F3'e not

Replay'in asıl vaadi `FailedToOk` sayısı: kaç satır `failed`'dan `ok`'a döndü.
`OkToFailed` sıfırdan büyükse yeni parser bir **gerileme** getirmiş demektir ve
bu sayı raporda duruyor — ama onu okuyan bir ekran yok.
