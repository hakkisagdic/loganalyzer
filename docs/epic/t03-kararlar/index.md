---
kind: spec
title: "T03 — Ingest boru hattı: kararlar ve açıkta kalanlar"
---

# T03 — OTLP girişi, WAL ve kodlama

> ⚠️ **Bu belge geriye dönük yazıldı.** Kaynağı kod, commit geçmişi ve F1
> kapanışı. Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan
> gerekçeler kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen
> alternatifler kayıtta yok.

**Yöneten kararlar:** K24, K4, K5 · **Ticket:** `tickets/ingest-boru-hatti`

## Ne yaptı

Verinin içeri girdiği **tek kapı** ve ürünün **dayanıklılık sınırı**. Bu
ticket'tan sonra "veri kayboldu mu" sorusunun cevabı tek bir yerde aranıyor:
WAL'a yazıldı mı, yazılmadı mı.

Henüz parse yok — bu ticket ham baytı diske indirmekle bitiyor.

## Kodda görünen kararlar

### Ack **WAL'dan sonra**, işleme **ack'ten sonra**

`IngestGateway` sıralamayı yorumunda pazarlığa kapatıyor. Tersi olsaydı parse
hatası ya da ClickHouse kesintisi veri kaybına dönerdi — ham arşivin varlık
sebebi tam olarak bu.

Sonucu risk ifadesini değiştiriyor: RustFS çökse, parser patlasa, ClickHouse
dolsa bile en kötü durum *"işlenmemiş veri birikti"*, *"veri gitti"* değil.

### Her eklemede ayrı `fsync` — grup commit bilerek yok

`WriteAheadLog` sınıf yorumunda yazılı: grup commit (N istek tek `fsync`'te)
katlarca hızlandırır ama dayanıklılık sınırını inceltir. **Ölçmeden yapılmaz**
notu düşülmüş; bugünkü hâl en güvenli olan.

Bu, bu depoda az rastlanan bir kayıt biçimi: reddedilen alternatif kodun içinde
duruyor.

### Çerçeve başlığında magic **ve** CRC

`[magic][len][crc][payload]`. CRC tek başına bozuk gövdeyi yakalıyor; magic'in
işi farklı: **uzunluk alanının kendisi yırtılmışsa** çılgın bir uzunluk okunur
ve kurtarma çöker. Magic, çerçeve sınırının gerçekten orada başladığını
doğruluyor.

`kill -9` sonrası yarım çerçeve **hata değil, beklenen durum** olarak
işleniyor: ack verilmemiş bir yazmanın kalıntısı, gönderen yeniden gönderecek.
Kurtarma o noktadan itibaren **buduyor** — bırakılsa sonraki yazma çöp baytların
ardına eklenir ve segment kalıcı olarak okunamaz hale gelirdi.

### Sınır kontrolü kilidin **içinde**

```csharp
// Sınır kontrolü kilidin İÇİNDE: dışarıda yapılırsa eşzamanlı istekler
// sınırı birlikte aşar ve disk dolar.
```

Klasik TOCTOU; kod onu doğru tarafta çözmüş ve niçin olduğunu yazmış.

### Kanal `Wait`, `DropWrite` değil

`IngestChannel`'ın gerekçesi bu deponun kural kitabına giren cümle:
*"Beklemek yavaşlatır, düşürmek yalan söyler."* `DropWrite` seçilseydi yük
altında veri **sessizce** düşerdi ve fark etmenin yolu olmazdı (§7).

Kapasitenin **sınırlı** olması da ayrı bir karar: sınırsız kanal backpressure'ı
kaldırmıyor, bellek tükenmesine erteliyor.

### Kendi kuyruğumuzu yazmıyoruz

WAL doluysa `503 + Retry-After`; yeniden deneme collector'ın `file_storage`
kalıcı kuyruğunda. Zincir: kanal dolu → kapı bekliyor → WAL büyüyor → 503 →
collector tutuyor.

### Collector `protocol: none` **ve** `encoding: iso-8859-1`

`deploy/otel/collector.yaml` bu ticket'ın en iyi belgelenmiş kararını taşıyor,
çünkü ilk akla gelen çözümün **neden çalışmadığı** yazılı:

- `nop` UDP'de `line_end_pattern` ile çakışıp açılışta hata veriyor.
- `nop` TCP'de bölücüyü `NoSplitFunc`'a düşürüyor ve syslog **çerçevelemesi**
  kayboluyor — akışın tamamı tek kayda dönüşüyor. Sessiz ve çok daha kötü.
- Varsayılan `utf-8` geçersiz baytı `U+FFFD` ile değiştiriyor: windows-1254 bir
  FortiGate satırı **bize ulaşmadan** geri dönülemez biçimde bozuluyor.

`iso-8859-1` seçildi çünkü `0x00-0xFF` → `U+0000-U+00FF` eşlemesi **tersinir**,
satır bölme çalışmaya devam ediyor ve orijinal baytlar bizde
`Encoding.Latin1.GetBytes(body)` ile aynen geri alınıyor.

### Kodlama zinciri kayıpsız bitiyor

Sıra: BOM → bildirilen/envanterdeki → UTF-8 doğrulaması → kaynağın yedek kod
sayfası → `latin1`. Son adım **başarısız olamaz**; yani metne çevirme hiçbir
girdide durmuyor.

Yanlış tahminin bedeli kalıcı değil: orijinal baytlar ham arşivde, replay
düzeltebiliyor (K12). Karar bu güvenceye **yaslanıyor**.

İki ayrıntı ayrıca korunmuş:
- `WasDeclaredHonored` — envanterdeki `encoding` yanlışsa sessiz kalmıyor;
  aksi hâlde yıllarca yanlış çözülürdü.
- Kodlama adı **ordinal** eşleşiyor. Kültür duyarlı `ToLower()` tr-TR'de
  `I → ı` yapıp `ISO-8859-9`'u tanınmaz hale getirirdi.

### `RegisterCodePages` zorunlu ve **iki yerden** çağrılıyor

.NET Core legacy kod sayfalarını yüklemiyor; çağrılmazsa `windows-1254`
**çalışma anında** patlar. Statik kurucu çağırıyor, ayrıca barındırıcının erken
çağırabilmesi için açık bırakılmış.

### OTLP/JSON bilinmeyen alanı reddetmiyor

`WithIgnoreUnknownFields(true)`. Gerekçe kodda: collector sürümü bizden önde
olabilir ve *"yeni bir alan yüzünden ingest durursa bu, veri kaybıdır."*

### Boş export WAL'a yazılmıyor

Collector sağlık yoklaması `200` alıyor ama disk yazmıyor. Küçük ama zinciri
gürültüden koruyan bir karar.

### WAL'daki format ham arşivinkiyle **aynı**

Batch NDJSON olarak yazılıyor; yükleyici (T04) dönüştürmüyor, **kopyalıyor**.
İki formatın ayrışması, arşivdeki baytın telde gelenden farklı olması demekti.

### gRPC (`:4317`) yok

Ticket'ın kendi ifadesiyle **kaçış kapısı**. HTTP tek başına yeterli; ikinci
taşıma ikinci bir çözüm yolu ve ikinci bir hata yüzeyi demekti.

## Bugün duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `WriteAheadLogTests` (9) | Geri okuma, yarım yazmanın budanması, **budamadan sonra doğru konuma yazma**, kapasite, segment döndürme, yeniden açılışta toplamın geri hesaplanması |
| `IngestGatewayTests` (5) | Kabul edilen batch'in WAL'da olduğu, 503 + `Retry-After`, bozuk gövdenin WAL'a **yazılmadığı**, boş export, NDJSON satır düzeni |
| `EncodingDetectorTests` (10) | Zincirin her adımı, BOM'lar, NFC, latin1 tur-gidişinin kayıpsızlığı, TR/CJK |
| `OtlpBodyReadTests` (6) | gzip açma, zip bomb sınırı (**açılmış** boyuttan), bilinmeyen kodlama reddi |
| `OtlpLogsDecoderTests` | Protobuf/JSON eşdeğerliği, kaynak anahtarı adayları |

`WriteAheadLogTests`'in üçüncüsü dikkate değer: budamanın *"çöp baytların
ardına değil, budanmış konuma"* yazdığını sınıyor. Budamayı yapıp konumu yanlış
bırakmak, kurtarmayı kâğıt üstünde doğru gösteren sessiz bir kusur olurdu.

## F1'de kırılan şey

Beş hatadan **dördüncüsü** bu ticket'ın alanındaydı:

> API gzip açmıyor. OTLP dışa aktarıcısı **varsayılan olarak** gzip gönderiyor →
> `"invalid wire type"`, ve mesaj sıkıştırmadan hiç bahsetmiyor.

Kalıp tanıdık: uç doğru yazılmıştı, gönderenin varsayılanı bilinmiyordu. Hata
mesajı protobuf'u işaret ediyordu, sorun taşımadaydı. `OtlpBodyReadTests` şimdi
hem açmayı hem sınırı tutuyor — ve zip bomb sınırını **açılmış** boyuttan
ölçüyor, sıkıştırılmıştan değil.

## Açıkta kalanlar

| # | Ne | Durum |
| --- | --- | --- |
| 1 | **`kill -9` altında ack'lenmiş olay kaybolmuyor** | Ticket'ın ikinci kabul kriteri ve bir **entegrasyon testi** isteniyordu. Bugün sınanan şey çerçeve düzeyi: yarım yazma elle üretiliyor, gerçek bir süreç öldürülmüyor. F1 doğrulama turunun yedi iddiası arasında da yok |
| 2 | **503 sonrası collector yeniden deniyor, veri kaybı yok** | Kapının 503 döndüğü ölçülü; zincirin **collector tarafı** ölçülmedi. `file_storage` kuyruğunun dolması hâlinde ne olduğu da |
| 3 | **Zaman aşımı → çift yazma** | 200, kanal batch'i kabul ettikten sonra dönüyor; WAL yazımı ondan önce bitmiş oluyor. İstemci bu arada zaman aşımına düşüp yeniden gönderirse aynı batch WAL'a **ikinci kez** yazılır — `AppendAsync`'te tekilleştirme anahtarı yok. Bunun bilinçli kabul mü yoksa gözden kaçan bir durum mu olduğu **kayıtta yok** |
| 4 | **Grup commit** | Kod yorumunda "ölçmeden yapılmaz" diye duruyor; ölçüm yapılmadı |
| 5 | **`MaxTotalBytes = 8 GB`, `RetryAfterSeconds = 5`** | İkisi de yapılandırmada, ikisinin de **gerekçesi kayıtta yok**. 8 GB'ın kaç dakikalık akışa karşılık geldiği ve 5 saniyenin collector'ın yeniden deneme aralığıyla ilişkisi ölçülmemiş |
| 6 | **Gerçek cihazla syslog akışı** | İlk kabul kriteri. F1 uçtan uca turu bunu **collector üzerinden** doğruladı; gerçek bir cihaz/logger kaydı yok |

Üçüncüsü, §7'nin tarif ettiği şekle en yakın olanı: belirti üretmiyor, sayaç
yok, ve ham arşivde yalnızca tekrarlanmış bir batch olarak görünür.
