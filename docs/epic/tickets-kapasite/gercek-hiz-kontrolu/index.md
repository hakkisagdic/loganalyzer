---
title: "B01 — Gerçek hız kontrolü ve üretecin kendi tavanı"
kind: ticket
status: 2
---

# B01 — Ayarın uygulandığını kim söylüyor

**Bağımlılık:** — · **Sonraki:** B03 (`auto` keşfi), B05 (güvenlik devre kesici)

Plan: [Kapasite ölçümü](../kapasite-olcumu/index.md) §2, §5.

## Sorun — ölçüldü, iki kusur, ikincisi sessiz

Koordinatör `SyslogEmitter`'ın `RatePerMinute` ayarını verip **gerçekleşen**
hızı saydı (`TimeSpan.FromMinutes(1.0/rate)`, 200 satır, macOS/arm64):

| İstenen | Gerçekleşen | |
| --- | --- | --- |
| 10 | 10 | tutuyor |
| 100 | **91** | %9 geride |
| 1 000 | **824** | %18 geride |
| 10 000 | **pacing YOK** | ayar anlamsız |

**Birinci kusur sapma:** gecikme satırın kendi işini saymıyor, hedef
yükseldikçe geriye düşülüyor.

**İkinci kusur sessiz anlamsızlaşma** ve bu deponun adını koyduğu sınıf:
hesaplanan gecikme zamanlayıcı çözünürlüğünün altına indiğinde `Task.Delay`
**anında dönüyor**, döngü soket hızında basıyor. Hata yok, sayaç yok, uyarı
yok. Yani **10 000 EPS *istemek* mümkün, *elde etmek* değil, ve ikisi ayırt
edilemiyor.**

> **Bir ayarın var olması, uygulandığını söylemiyor.**

En pahalı yönü ölçümün kendisinde: kapasite bu pacer'la ölçülürse
*"10 000 EPS'de kayıp yok"* denir, oysa üretilen hız 10 000 değildir ve ölçülen
şey **ölçüm aracının kendi tavanı** olur.

## Yapılan

### Token kovası — eşiği kaldırmıyor, **ölçülebilir yapıyor**

`TokenBucketPacer`. Hiçbir yazılım kovası makinenin tavanını yükseltmiyor; fark
şu ki kova **borcunu biriktiriyor** (`Debt`): bir tur geç kalındığında jeton
havuzda kalıyor, sonraki turda harcanıyor, ve üretilen toplam hedefin arkasında
kaldığında **bunu bilen bir yer** oluyor. Eski modelde bu sayı hiç
hesaplanmıyordu — %18 geride koşmak ile hedefe ulaşmak aynı çıktıyı veriyordu.

`TryAcquire()` **beklemiyor**, ne kadar beklenmesi gerektiğini döndürüyor.
İki gerekçe: kova içinde `Task.Delay` çağırmak yerine geçtiği modelin kusurunu
tipin içine gömmek olurdu; ve **toplu yazma** ancak çağıran beklemeyi
yönetiyorsa mümkün.

Kapasite tavanı (`burstCapacity`) borcu **silmiyor**, yalnızca tek seferde
boşaltılmasını engelliyor: `Issued` tavandan bağımsız birikiyor, dolayısıyla
`Debt` taşan jetonları da sayıyor. Tavan olmasaydı uzun bir duraksamadan sonra
biriken borcun tamamı tek seferde boşalır ve ölçülen şey profil değil
**duraksama** olurdu.

### Hüküm — `GENERATOR-LIMITED`

`GeneratorAttainment`, ve alınan şey syslog-bench'in **kodu değil hükmü**:
ölçüm kendi kusurunu ölçtüğünde suçu hedefe yıkmayı reddediyor.

| Hüküm | Anlamı | Kayıp yorumlanabilir mi |
| --- | --- | --- |
| `Unspecified = 0` | Hiçbir şey söylenmedi | — |
| `Attained` | Üreteç hedefe ulaştı | ✅ |
| `GeneratorLimited` | Üreteç ulaşamadı — **eksik olay hedefin kaybı sayılamaz** | ❌ |
| `Unmeasured` | Gerçekleşen hız okunamadı | ❌ |
| `NoTarget` | Profilin hedefi yok (`max`) | ✅ |

**Beş hâl ve dördü zorunlu bir ayrım taşıyor.** `Unmeasured` ile
`GeneratorLimited` ayrı: biri *"üreteç yetişemedi"*, öteki *"yetişip
yetişmediğini bilmiyorum"*. İkincisini birincisine indirmek bir arıza
uydurmak, `Attained` demek ise arızayı gizlemek olurdu. `NoTarget` de ayrı:
`max`'ta ulaşılan bir şey yok ama ölçüm **yapıldı**.

`Unspecified = 0` bilinçli — varsayılan `Attained` olsaydı hiç ölçülmemiş bir
koşum *"üreteç hedefe ulaştı"* diye okunurdu.

Bu ayrımın bu depodaki **altıncı** örneği: T36 `Measured=false`, T47
`Unspecified`, T51 `dropped_sentence_ratio` payda sıfırken `null`, T54
`RcaModelBoundary`, B02 `LedgerReading.Value == null`. B02 ile aynı ayrıma
**bağımsız** varılması kararı sağlamlaştırıyor.

### Tavan sayısı ölçümden geldi, seçilmedi

`MinimumAttainment = 0.95`. Bugünkü `Task.Delay` modeli 1 000 EPS'de **%82**,
100 EPS'de **%91** ulaşıyor — %95 tavanı **ikisini de reddediyor**. Bir tavanın
işe yaradığının kanıtı, mevcut davranışı kutsamamasıdır.

**Neden %99 değil:** kova ile gerçek soket arasındaki normal jitter kapıyı
gürültüyle kırmızı yakardı ve tavan **rutin olarak yükseltilen bir sabite**
dönerdü — M02'de ölçülüp kaldırılan hâl.

### `max` bir hedef almıyor — ve alması **derlenmiyor**

`PaceProfile` kapalı bir hiyerarşi; `Max` alt tipinde hız alanı **yok**.
Plan §B01 şartı *"bir sayı girdisi almayacak, çünkü aldığı an ölçtüğü şey o
sayı olur"* bir çalışma anı reddine bırakılmadı: `double? Rate` + reddetmek
aynı şeyi söyler ama **unutulabilir** olurdu. Kalıp T41'in `RedactedPrompt`'u
ve T54'ün `RcaModelBoundaryStamp`'i.

`Max.TargetAt` `null` dönüyor, `0` değil: *"hedefim yok"* ile *"hiç basma"*
farklı cümleler ve ikisini aynı değere indirmek bu profili sessizce
durdurulmuş bir koşuma çevirirdi.

`Soak` de `Fixed`'den ayrı bir tip, hız modeli aynı olmasına rağmen: ayrı olma
sebebi süre değil **soru** — burada aranan kayıp oranı değil *zamanla bozulma*.
Ayrı tip olmasa raporda `fixed` diye görünür ve *"neden 6 saat koştu"*
sorusunun cevabı hiçbir yerde yazılı olmazdı.

### Duvar saati nerede — §6'nın gerilimi

Bir pacer testi doğası gereği zaman ölçüyor, oysa §6 *"bir testin geçme
sebebinin duvar saatiyle ilgisi olmamalı"* diyor. Sınır şöyle çizildi:

| Katman | Duvar saati | Konteyner |
| --- | --- | --- |
| Token kovası | **yok** — `TimeProvider` enjekte, `FakeTimeProvider` ile sınanıyor | ❌ |
| Hüküm | **yok** — saf, sayılardan karar veriyor | ❌ |
| Gerçek basma ve gerçekleşen EPS | **var** — bu bir ölçüm, bekçi değil | ❌ — loopback TCP yeterli |

### Gerçek taşıma yolu da bağlı

Model yalnız bırakılmadı. `CapacityEmitter`, profil örneklerini gerçek TCP/UDP
taşımasına basıyor; TCP'de bağlantı havuzu ve toplu yazma, UDP'de olay başına
datagram kullanıyor. `raw` gövdeyi koruyor, `tagged` ise `bizigo_run_id`,
`bizigo_seq` ve `bizigo_send_ns` ekliyor. Özet dizgiden değil, seçili kodlama
uygulandıktan sonraki **gerçek tel baytlarından** alınıyor.

CLI yüzeyi beş profili de açıyor ve manifest yolunu zorunlu tutuyor. Hedefli
bir koşum `%95` tabanının altında kalırsa süreç `GENERATOR-LIMITED` için ayrı
çıkış kodu döndürüyor; sayı hükümsüz ya da kayıtsız bırakılamıyor.

Yani bekçilerin tamamı saatsiz ve konteynersiz; duvar saatine bağlı olan tek
şey ölçümün kendisi ve sonucu bir sayı, bir kapı değil.

## Kapsam dışında — gerekçeleriyle

- **Üretecin nerede koşacağı bir karar değil, açık soru.** Plan §6'nın üç açık
sorusundan biri. Varsayılmadı: koşum kaydı *"üreteç ve hedef aynı makinede
mi"* alanını **taşıyor** ve hüküm o alan dolmadan verilemiyor. Cevap gelince
**değer** sabitlenir, mekanizma değişmez.
- **Paket yakalama yok** — plan bunu Linux'a çivili olduğu için dışarıda
bırakıyor; üç sayaç B02'de.
- **`auto` keşfi B03'te.** `Ramp` bilerek doğrusal ve kademesiz: basamaklı
tırmanma ikili aramanın işi ve ikisini karıştırmak, bulunan sayıyı basamak
genişliğine bağlardı.

## Ölçülen bir bekçi boşluğu — **kimlik deseni** (koordinatöre bildirildi)

`EpicStatusTests`'in dört okuyucusu kimliği **farklı desenlerle** arıyor ve
`B` ön eki üçünde yok:

| Okuyucu | Desen | `B01`'i görüyor mu |
| --- | --- | --- |
| `ReadRoadmap` (`tickets*` içindeki **bağlı** satırlar) | `[TSM]\d+` | **hayır** |
| `ReadMerged` (git dal adları) | `[tsmbTSMB]` | evet |
| `ReadDeclared` (her tablodaki satır) | `[TSMB]\d+` | evet |
| `ReportedOpen` (`kalan-is-raporu`) | `[TSM]\d+` | **hayır** |

Sonucu **tek yönlü değil, bölünmüş**:

- Kapı B (*"merge edilmiş her kimlik bir tabloda anılıyor"*) `Declared`'ı
okuduğu için **yeşil** kalıyor — `kapasite-olcumu` tablosunda bir `| B01 |`
satırı var.
- Kapı A (*"merge edilmiş bir iş `status: 0` görünmüyor"*) kimliği ticket
dosyasına `Roadmap` üzerinden bağlıyor ve `Roadmap` `B01`'i görmüyor. Yani
**B kalemlerinde mekanik koruma hiç çalışmıyor**: `b01-*` merge edilse ve dosya
`status: 0` kalsa kapı sessiz.
- Ve bu dosyanın kendisi `Her_ticket_bir_yol_haritasi_tablosunda_gorunuyor`
kapısını **kırmızı yakıyor** — çünkü o kapı ticket dosyalarını `Roadmap`'te
arıyor ve hiçbir tablo yerleşimi bunu düzeltemiyor: desen `[TSM]` olduğu sürece
bağlı bir `| B01 | [başlık](yol) |` satırı da görünmez.

Deseni **genişletmedim**: kimlik uzayının şekli koordinatörün kararı. İki yol
var ve ikisi de onun:

1. Deseni `[TSMB]` yap — dört okuyucu tek desende buluşur.
2. `KnownDivergence`'a `yol-haritasi:tickets-kapasite/gercek-hiz-kontrolu`
gerekçesiyle bir satır yaz — kapı sessizleşir ama kalem listede kalır.

## Bekçiler

§6: her biri kırmızı yanabildiği ölçülerek teslim edilir. Hiçbiri konteyner
istemiyor, hiçbiri duvar saati okumuyor.

| Bekçi | Ne kanıtlar |
| --- | --- |
| `Hedefe_ulasan_kosum_attained` | Taban: hüküm çalışıyor |
| `Hedefin_gerisinde_kalan_kosum_generator_limited` | Asıl iddia — ölçülen hâl (824/1 000) taklit ediliyor |
| `Generator_limited_kaybi_yorumlanamaz_kiliyor` | Hükmün **sonucu** var, yalnızca etiket değil |
| `Olculemeyen_kosum_attained_sayilmiyor` | `Unmeasured` ≠ `Attained` |
| `Olculemeyen_kosum_generator_limited_de_sayilmiyor` | `Unmeasured` ≠ `GeneratorLimited` — arıza uydurulmuyor |
| `Max_profili_hedef_almiyor` | `NoTarget` ayrı bir hâl, ve `Debt` `null` |
| `Kova_borcu_biriktiriyor` | Sapmanın görünür olduğu tek yer |
| `Jeton_tukendiginde_bekleme_suresi_donuyor` | Hız kontrolü gerçekten uygulanıyor |
| `Profiller_hedefi_zamana_gore_veriyor` | `ramp` yükseliyor, `burst` yalnızca penceresinde |
| `Soak_raporda_fixed_diye_gorunmuyor` | Ayrı tip olmasının sebebi süre değil soru |
| `Bekci_bos_kume_uzerinde_donmuyor` | Hüküm kümesi ve profil adları boşalmıyor; tavan bugünkü hâli reddediyor |

### Ölçüm — `tools/b01-kirmizi-olcumu.py`, on bir kusur

Üç çift bilerek **çapraz** kurulu ve her biri bir kararı ayrı ayrı kanıtlıyor:

| Çift | Neyi ayırıyor |
| --- | --- |
| `Unmeasured` ↔ `GeneratorLimited` | Hükmü birine çökerten kusur ötekinin testini yeşil bırakıyor — ikisi ayrı iddia. Tek testte olsalar *"arıza uydurmak"* serbest kalırdı |
| Etiket ↔ **sonuç** | `GENERATOR-LIMITED` basmak ile kaybı yorumlanamaz kılmak ayrı şeyler; etiketi doğru basıp kararı ona bağlamamak bu depoda ölçülmüş bir hâl |
| Hedefi olmak ↔ olmamak | `max`'ın `NoTarget`'ı `Attained` değil |

Bir kusur özellikle **tavanı** sınıyor: taban %80'e çekilirse bugünkü ölçülmüş
davranış (824/1 000 = %82) **geçer**. Yani kapı, kaldırmak için var olduğu hâli
onaylar hâle gelir — bir tavanın işe yaradığının kanıtı mevcut davranışı
kutsamamasıdır.

Ek iki kusur değişken profillerin asıl riskini tutuyor: profil integralini son
an hızına indirmek ve `ramp` hükmünü koşum ortalaması yerine son hedefle kurmak.
İkisi de kendi bekçisini kırmızı yakarken sabit profil kontrollerini yeşil
bıraktı.

Gerçek TCP ölçümü iki bağlantı ve 16'lık batch ile 200 EPS / 400 kayıt bastı.
Alıcı 400 kaydın tamamını gördü; manifest 400 tel özeti taşıdı ve gerçekleşen
hız yazılı çift taraflı sapma sınırında kaldı: `%95..%105`. Yalnızca alt sınır
ölçülmüyor; pacing tümden kalkıp üreteç aşırı hızlansa da test kırmızı yanıyor.
