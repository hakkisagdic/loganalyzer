---
title: "F1-D1 — Dayanıklılık kriterinin ÖLÇÜMÜ"
kind: ticket
status: 0
---

# F1-D1 — "kill -9" taklit ediliyor, "RustFS durdurulur" hiç ölçülmüyor

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
kaldırıldığında bu koşum **kırmızı yanmalı**. Yanmıyorsa ölçüm `kill -9`'u değil
başka bir şeyi ölçüyor — ve bu ticket'ın var olma sebebi tam olarak o durumun bir
kez gerçekleşmiş olması.

## 4 · Ölçüm protokolü — ikinci yarı (depo erişilemez)

1. **Depo compose'dan durdurulacak** (`docker compose stop rustfs`), taklit
   edilmeyecek. Taklit, birinci yarının düştüğü tuzağın aynısı: erişilemez bir
   ağ servisi ile `null` dönen bir arayüz aynı şey değil — biri zaman aşımı,
   bağlantı reddi ve yeniden deneme üretiyor, diğeri anında cevap veriyor.
2. **Depo dururken batch gönderilecek ve ack BEKLENECEK.** Kriter *"ingest devam
   eder"* diyor; devam etmenin gözlemlenebilir hâli ack'in gelmesi.
3. **Devam ettiği HANGİ sayaçtan okunacak:** `IngestStats`'in ack sayacı ve WAL'a
   yazılan segment sayısı. *"Hata log'u yok"* bir kanıt değil — sessizce
   duran bir boru hattı da hata basmaz.
4. **Depo geri kaldırılacak ve arşivin YETİŞTİĞİ ölçülecek.** Bu yarı olmadan
   ölçüm eksik: ingest'in devam etmesi, biriken segmentlerin sonunda arşive
   inmesi anlamına gelmiyor. `raw_manifest` satırlarının `verified_at`'i
   dolmalı.
5. **Doğrulanmamış segmentin silinmediği ölçülecek.** Ürünün sözü bu
   (*"doğrulanmamış segment asla silinmez"*) ve depo kesintisi o sözün en
   olası kırılma anı.

## 5 · Kabul kriterleri

1. Gerçek bir süreç `SIGKILL` ile öldürülüyor ve ack verilen **her** satır
   yeniden başlatma sonrası sorguyla bulunuyor.
2. fsync kaldırıldığında (1)'in koşumu **kırmızı** yanıyor — ölçümün ölçüm
   olduğunun kanıtı.
3. Object storage durdurulmuşken ack alınıyor ve devam bir **sayaçtan** okunuyor.
4. Depo geri geldiğinde arşiv yetişiyor ve `verified_at` doluyor.
5. `WriteAheadLogTests`'in başlığındaki iddia **daraltılıyor**: o test yarım
   çerçeve okumasını ölçüyor, `kill -9`'u değil. Test kalıyor, iddia küçülüyor.

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
