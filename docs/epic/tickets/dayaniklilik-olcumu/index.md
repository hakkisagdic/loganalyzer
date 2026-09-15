---
title: "T63 — Dayanıklılık kriterinin ÖLÇÜMÜ"
kind: ticket
status: 0
---

# T63 — "kill -9" taklit ediliyor, "RustFS durdurulur" hiç ölçülmüyor

F1'in dayanıklılık kabul kriteri şu:

> Süreç `kill -9` ile öldürülür; ack'lenen hiçbir olay kaybolmaz. RustFS
> durdurulur; ingest devam eder.

M19'un F1 taramasında ölçüldü: **iki yarısı da karşılandığı gösterilmemiş.** Bu
ticket ölçümü değil **ölçümün protokolünü** yazıyor — koşumu koordinatör yapacak
(§2: container gerektiren her şey onun tarafında).

## 1 · Bugünkü hâl — ölçüldü

### Birinci yarı: `kill -9` **taklit** ediliyor

`tests/Bizigo.UnitTests/WriteAheadLogTests.cs` bu kriteri kendi başlığında
sahipleniyor (satır 9: *"T03 kabul kriteri: `kill -9` altında ack'lenmiş hiçbir
olay kaybolmuyor"*). Ama satır 80 ne yaptığını **kendi yorumunda** yazıyor:

```
// kill -9 taklidi: son çerçevenin gövdesi yarıda kesilmiş.
```

Yani ölçülen şey **dosyanın şekli**: yarım yazılmış bir çerçeveyi okuyucunun
atlaması. Süreç öldürülmüyor.

**Bu, bekçinin var olduğu ama iddia ettiği şeyi ölçmediği bir hâl** — ve bu
depoda ölçülen en pahalı sınıf. Farkın nerede olduğu somut: yarım çerçeve, WAL
dosyasına **yazma çağrısının** kesilmesini taklit ediyor; gerçek `kill -9` ise
**fsync'in tuttuğu yeri** sınıyor. İkisi aynı değil, çünkü aradaki fark tam
olarak işletim sistemi tamponunda bekleyen ve diske hiç inmemiş veri — yani
*"ack verdik ama veri diskte değil"* hâli. Ürünün en merkezî iddiası
(**ham veri her şeyden önce gelir**) tam orada duruyor.

### İkinci yarı: hiç ölçülmüyor

*"RustFS durdurulur; ingest devam eder"* — bunu ölçen **hiçbir test yok**.
Arandı: `tests/` altında object storage'ı durdurup ingest'in devam ettiğini
gösteren bir koşum geçmiyor. `RawArchiveTests` nesne **kaybını** taklit ediyor
(`FakeObjectStoreOverS3.Hide()`), ama depo **erişilemez** olduğunda ingest'in ne
yaptığını değil.

Bu ayrım kritik: ürünün dayanıklılık hikâyesi *"RustFS 1.0-rc olduğu için
dayanıklılık sınırı bilinçli olarak WAL'da"* diyor. O cümle, depo düştüğünde
ingest'in **durmadığını** varsayıyor. Varsayım ölçülmemiş.

## 2 · Kapsam

**İçinde:**

- Gerçek bir süreci gerçekten öldürüp WAL'dan kurtarmayı ölçen bir koşum.
- Object storage erişilemezken ingest'in devam ettiğini ölçen bir koşum.
- Ölçümün **hangi sayaçtan** okunacağının belirlenmesi.

**Dışında:**

- `WriteAheadLogTests`'i **silmek**. O test yarım çerçeve okumasını ölçüyor ve o
  gerçek bir şey; yapılacak şey başlığındaki iddiayı daraltmak, testi kaldırmak
  değil.
- WAL formatını ya da fsync stratejisini değiştirmek. Bu ticket **ölçüyor**,
  tasarımı tartışmıyor. Ölçüm bir kayıp gösterirse o ayrı bir kalem olur.

## 3 · Ölçüm protokolü — birinci yarı (`kill -9`)

Sıra ve her adımın **neyi** kanıtladığı:

1. **Ingest süreci ayrı bir süreç olarak kalkacak.** Aynı süreç içinden ölçmek
   mümkün değil: `kill -9` yakalanamaz ve test koşucusunu da öldürür. Yani bu
   ölçüm bir alt süreç (`Process.Start`) istiyor ve ölçüm kodu onun **dışında**
   duruyor.
2. **Bir batch gönderilecek ve ack BEKLENECEK.** Ack'in alınması ölçümün
   başlangıç noktası: kriterin sözü *"ack'lenen olay"* hakkında. Ack alınmamış
   bir batch'in kaybolması kriteri ihlal etmiyor.
3. **Öldürme anı ack'ten SONRA, ama başka hiçbir şey beklenmeden.** Burada bir
   yarış var ve ölçümün değeri ona bağlı: ack ile öldürme arasına konan her bekleme,
   ölçümü kolaylaştırır ve sorulan soruyu değiştirir. En sıkı hâli: ack'i okuyan
   satırın hemen ardından `SIGKILL`.
4. **`SIGKILL`, `SIGTERM` DEĞİL.** `SIGTERM` düzgün kapanışı tetikler ve o yol
   zaten fsync ediyor; ölçüm o hâlde ürünün graceful shutdown'ını ölçer,
   dayanıklılığını değil.
5. **Süreç yeniden başlatılacak ve olay SORGUYLA aranacak** — WAL dosyasına
   bakılarak değil. Dosyada olması yeterli değil: kriterin sözü olayın
   **kaybolmaması**, ve kaybolmadığının kanıtı okunabilir olması.
6. **Sayı karşılaştırılacak, varlık değil.** *"Olay var"* zayıf bir iddia; ack
   verilen batch'teki **her** satırın sayısı eşleşmeli. Bir satırın kaybı, "var
   mı" sorusuyla görünmez.

**Ölçümün kendisi de ölçülecek (§6):** fsync çağrısı üretim kodundan
kaldırıldığında bu koşum **kırmızı yanmalı**.

## 3.1 · ÖLÇÜLDÜ — ve bu beklenti YANLIŞ çıktı

Protokol koşturuldu (`tools/t63-wal-kill-olcumu.py`, konak
`tools/t63-wal-harness/`). Gerçek alt süreç, ack'ten hemen sonra gerçek
`SIGKILL` (süreç **grubuna**, yoksa `dotnet run` sarmalayıcısının çocuğu yaşar
ve `Dispose` fsync eder).

| Koşum | ack | diskte | kayıp |
| --- | --- | --- | --- |
| `FlushToDisk = true` (üretim) | 8 | 8 | **0** |
| `FlushToDisk = false` (§6 kusuru) | 8 | 8 | **0** |

**Kriter geçiyor. Ama kusurlu koşum da geçiyor** — yani yukarıdaki §6 beklentisi
karşılanmıyor, ve sebebi ölçümün zayıflığı **değil**:

> **`kill -9` fsync'i ölçemez.** Süreci öldürmek, yazılan baytları işletim
> sisteminin sayfa önbelleğinden **silmiyor** — `write()` çağrıldığı anda baytlar
> çekirdeğin sorumluluğunda ve süreç ölse de diske iniyorlar. fsync'in koruduğu
> şey **sürecin** ölümü değil, **makinenin** ölümü: elektrik kesintisi, çekirdek
> paniği, VM'in zorla sıfırlanması.

Yani F1'in kriteri (*"süreç `kill -9` ile öldürülür; ack'lenen hiçbir olay
kaybolmaz"*) **fsync olmadan da karşılanıyor**. Kriter, ürünün dayanıklılık
hikâyesinin (*"ack, ham batch fsync edildikten sonra verilir"*) sandığı şeyi
ölçmüyor.

**Bu, benim bu ticket'ta yazdığım bir beklentinin düzeltilmesi.** İki arıza kipi
tek cümleye sıkıştırılmıştı:

| Arıza kipi | Ne koruyor | `kill -9` ölçer mi | Ölçmek için ne gerekir |
| --- | --- | --- | --- |
| **Süreç ölümü** | Uygulamanın ack'ten **önce** yazmış olması | **Evet** — ölçüldü, geçiyor | Bu koşum. `AppendAsync` dönmeden ack veren bir hata burada yanar |
| **Makine ölümü** | fsync — baytların gerçekten diskte olması | **Hayır** | Host düzeyi arıza enjeksiyonu: VM'i zorla sıfırlamak, ya da `dm-flakey` gibi bir katmanla yazmaları yutmak |

İkinci satır **bu ticket'ın kapsamı dışında** ve bilerek: bir VM'i zorla
sıfırlayan bir koşum, bu deponun bugün taşıdığı hiçbir altyapıya benzemiyor ve
CI'da yeri olmaz. Ama **yazılı olması** şart — yoksa yeşil yanan bir `kill -9`
koşumu, fsync'in ölçüldüğü sanısını üretir. Tam olarak bu ticket'ın doğmasına yol
açan hata sınıfı.

**`kill -9` koşumunun kendi değeri duruyor ve küçük değil:** ack'i yazmadan önce
veren bir hatayı yakalıyor — yani sıralama iddiasını ölçüyor. Ölçmediği şey
sıralamanın **dayanıklılık** kısmı.

## 4 · Ölçüm protokolü — ikinci yarı (depo erişilemez)

1. **Depo compose'dan durdurulacak** (`docker compose stop rustfs`), taklit
   edilmeyecek. Taklit, birinci yarının düştüğü tuzağın aynısı: erişilemez bir
   ağ servisi ile `null` dönen bir arayüz aynı şey değil — biri zaman aşımı,
   bağlantı reddi ve yeniden deneme üretiyor, diğeri anında cevap veriyor.
2. **Depo dururken batch gönderilecek ve ack BEKLENECEK.** Kriter *"ingest devam
   eder"* diyor; devam etmenin gözlemlenebilir hâli ack'in gelmesi.

### 4.1 · HANGİ sayaç, HANGİ değer — rakamla

Bu, ölçümün kalbi. Kendi cümlemiz şunu gerektiriyor: *"hata log'u yok"* kanıt
değil, sessizce duran bir boru hattı da hata basmaz. O yüzden okunacak şey bir
**sayaç**, ve hangisi olduğu keyfî değil.

`IngestStats` (`src/Bizigo.Ingest/Pipeline/IngestStats.cs`):

| Sayaç | Depo DURURKEN beklenen | Neden bu değer |
| --- | --- | --- |
| **`AcceptedBatches`** | **artmaya devam eder** — gönderilen her batch için +1 | `Accepted()` **WAL yolunda** çağrılıyor, arşiv yüklemesinden **önce**. Depo, WAL'ı değil **yükleyiciyi** (`RawArchiveUploader`) etkiliyor |
| **`AcceptedRecords`** | gönderilen kayıt sayısı kadar artar | Batch başına değil kayıt başına; batch sayısı eşitken kayıt kaybını görünür kılıyor |
| **`RejectedFull`** | **0** — WAL kapasitesi dolana kadar | Doldu demek ack'in **durduğu** an demek; bu sayaç 0'dan çıktığı anda *"ingest devam ediyor"* artık doğru değil |
| `RejectedInvalid` | 0 | Değişirse ölçüm depo kesintisini değil bozuk girdiyi ölçüyor |
| `ProcessedRecords` | **BU SAYAÇ OKUNMAYACAK** | Ayrı eksen: parse + ClickHouse yazımı. ClickHouse da düşükse burası durur ama `AcceptedBatches` artmaya devam eder — yani **yanlış sayacı okumak yanlış hüküm verir** |

**Somut kabul:** N batch × M kayıt gönderildiğinde,
`AcceptedBatches` **+N**, `AcceptedRecords` **+(N×M)**, `RejectedFull` **0**.

### 4.2 · İddianın bir SON TARİHİ var ve kriterde yazılı değil

*"Ingest devam eder"* **süresiz değil**. WAL sınırlı:
`WalOptions.MaxTotalBytes` varsayılanı **8 GiB**
(`MaxSegmentBytes` **128 MiB**). Depo dururken segmentler **birikiyor**, çünkü
yükleyici onları boşaltamıyor. Kapasite dolduğunda `WalFullException` atılıyor,
`RejectedFull` artıyor ve **ack durmaya başlıyor**.

Yani kriterin doğru hâli: **ingest, WAL kapasitesi dolana kadar devam eder.**
Süre = `8 GiB ÷ (ingest hızı)` — ve **payda ölçülmemiş**, yani bu bir kabul
kriteri değil bir **kapasite ölçümü** (B01–B05 ailesi).

### 4.3 · Kapasite ayarı testten VERİLEBİLİYOR — 8 GiB doldurmak gerekmiyor

**Ölçüldü (M21), iki yol da açık:**

| Yol | Nasıl | Kanıt |
| --- | --- | --- |
| Birim testi | `o.MaxTotalBytes = 32` | `IngestGatewayTests.WAL_dolunca_503_ve_Retry_After_donuyor` bunu **zaten** yapıyor |
| Gerçek süreç / compose | `Ingest__Wal__MaxTotalBytes=<bayt>` | `IngestServiceCollectionExtensions:34` — `services.Configure<WalOptions>(configuration.GetSection("Ingest:Wal"))` |

**Ve zincirin çoğu ZATEN ölçülüyordu.** `WalFullException` →
`_stats.RejectFull()` → `IngestResult(Full, 0, …, RetryAfterSeconds)` → 503
(`IngestGateway.cs:103–115`). Bunun **davranış** tarafı bir birim testiyle
çivili: sonuç `Full`, ipucu yapılandırmadan geliyor.

> ⚠️ **Ölçülmeyen taraf SAYACIN KENDİSİYDİ** — yani bu ölçümün **girdisi**.
> Hiçbir test `IngestStats.RejectedFull`'a bakmıyordu. `RejectFull()` çağrısı
> düşse **davranış aynı kalırdı** (istemci yine 503 alır) ve depo yarısı
> *"ingest durdu mu"* sorusuna **0** okuyup *"hayır"* derdi.
>
> M21'de kapatıldı: `WAL_dolunca_RejectedFull_sayaci_artiyor_ve_kabul_sayaci_artmiyor`
> ve karşı-kanıtı `Normal_kabulde_AcceptedBatches_ve_AcceptedRecords_artiyor`
> (+1 batch / +3 kayıt — protokolün `+N / +(N×M)` beklentisinin birim
> karşılığı). §6 ile kırmızı yanabildiği ölçüldü.
>
> Ders: **bir davranışın ölçülmesi, o davranışı bildiren sayacın ölçülmesi
> değil.**

Yani **depo yarısının koşumu artık daha küçük bir soru soruyor**: WAL'ın dolması
ve ack'in durması birim düzeyinde çivili; compose koşumunun kanıtlaması gereken
şey yalnızca **depo erişilemezken ack'in DEVAM ettiği** ve segmentlerin
biriktiği.

3. **Depo geri kaldırılacak ve arşivin YETİŞTİĞİ ölçülecek.** Bu yarı olmadan
   ölçüm eksik: ingest'in devam etmesi, biriken segmentlerin sonunda arşive
   inmesi anlamına gelmiyor. `raw_manifest` satırlarının `verified_at`'i
   dolmalı.
4. **Doğrulanmamış segmentin silinmediği ölçülecek.** Ürünün sözü bu
   (*"doğrulanmamış segment asla silinmez"*) ve depo kesintisi o sözün en
   olası kırılma anı.

## 5 · Kabul kriterleri

1. ✅ **KARŞILANDI (ölçüldü).** Gerçek bir süreç `SIGKILL` ile öldürülüyor ve
   ack verilen **her** çerçeve diskte bulunuyor (8/8, kayıp 0). Koşum:
   `tools/t63-wal-kill-olcumu.py`.
2. ❌ **KARŞILANAMAZ — ve sebebi bu ticket'ta düzeltildi (§3.1).** fsync
   kaldırıldığında koşum kırmızı yanMIYOR, çünkü `kill -9` fsync'i ölçemez:
   süreci öldürmek baytları sayfa önbelleğinden silmiyor. fsync'in koruduğu şey
   makinenin ölümü, sürecin değil. Bu kriter host düzeyi arıza enjeksiyonu
   istiyor ve **kapsam dışına** alındı.
3. Object storage durdurulmuşken ack alınıyor ve devam bir **sayaçtan** okunuyor.
4. Depo geri geldiğinde arşiv yetişiyor ve `verified_at` doluyor.
5. ✅ **YAPILDI.** `WriteAheadLogTests`'in başlığındaki iddia **daraltıldı**: o
   test yarım çerçeve okumasını ölçüyor, `kill -9`'u değil. Test kaldı, iddia
   küçüldü, ve gerçek `kill -9`'un T63'te olduğu yazıldı.

6. **F1 kriterinin metni de düzeltilecek** (koordinatörde): *"süreç `kill -9` ile
   öldürülür; ack'lenen hiçbir olay kaybolmaz"* **fsync olmadan da** karşılanıyor,
   yani kriter ürünün dayanıklılık iddiasını ölçmüyor. Kriter iki cümleye
   ayrılmalı: sıralama (ölçüldü, geçiyor) ve dayanıklılık (ölçülmedi, host
   düzeyi arıza gerektiriyor).

## 6 · Bilinen sınırlar

- **Bu koşum yavaş ve container istiyor.** CI'da her PR'da koşması muhtemelen
  istenmez; hangi kapıda duracağı (nightly mi, etiketli mi) koordinatörün kararı.
- **`kill -9` zamanlaması doğası gereği yarışlı.** Ölçüm bir kez yeşil yanınca
  *"her zaman yeşil"* demiyor; tekrarlı koşum (N kez) ile tek koşum arasındaki
  fark kararı verilmesi gereken bir kalem.
- **Bu ticket F1'i "kapandı"dan geri almıyor.** F1'in **ölçülmemiş kalemi**
  olarak yazılı — ve yazılı olmasının sebebi M18/M19'da iki kez görülen şey:
  bir kriterin *"karşılandı"* diye durması, karşılandığının ölçüldüğü anlamına
  gelmiyor.

## 7 · Nereden çıktı

M19, F1 kabul kriteri tablosunu kriter kriter taradı. Yedi kriterden üçü boşluk
gösterdi: kriter 3 (*"7 günlük veri"* — sayı hiç ölçülmemiş), kriter 5 (bekçi
yok, metin de yanlış — M19'da kapatıldı) ve **bu** kriter. Kriter 4 tek turda
kapanmayacak kadar büyük olduğu için ticket'a döndü.
