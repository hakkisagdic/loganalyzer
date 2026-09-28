---
title: "T47 — Kalite ölçümü"
kind: ticket
status: 2
---

# T47 — Kalite ölçümü

**Bağımlılık:** T44 · **F4'ün kabul sınavı** — cilalama değil: Karar 1'in sayacı
ölçülemezse faz kapanmıyor.

Ölçtüğü iki şey: **altın küme üzerinden atılan cümle oranı** ve **çelişen kanıt
tiyatrosu** (RCA riski #5).

---

## 1 · Ölçünün tamamı paydada

Bu ticket'ın her kararı tek bir soruya çıkıyor: **neye bölüyoruz?** Sayının
kendisi nadiren yanlış; bölen sık sık yanlış, ve yanlış bölen **inandırıcı bir
sayı** üretiyor.

| Ölçü | Payda | Paydanın dışında | Neden dışında |
| --- | --- | --- | --- |
| Doğruluk (T38) | `Total − Unknown` | `Unknown` | Zorunlu soruya "bilmiyorum" diyen, rapora kötü not vermiş olmaz |
| **Tiyatro** | `Sound + Trivial` | `NotPresent`, `Unspecified` | Bölüm yoksa uydurulacak bir şey de yok |
| **`accuracy@k`** | Rank'in **sorulduğu** incelemeler | Soru sorulmadan yazılmış kayıtlar | Sorulmamış bir soru sıfır cevap değil |
| **Atılan cümle** | Üretilen **cümle** | Raporu olmayan paket (A) | Koşmamış bir model ölçülemez |
| **Uydurma atıf** | **Atılan** cümle | Atılmamış cümleler | Uydurma atıf, atılanların bir alt kümesi |

Beşinde de payda sıfırken oran **`null`, asla `0.0`**.

### Tiyatro oranında ayrım daha keskin

Doğrulukta `0` kötü haber. Tiyatroda **`0` en iyi sonuç**. Yani ölçülmemiş bir
boyut `%0` yazarsa ekran onu *mükemmel* diye gösterir — göstergenin var olma
sebebinin tam tersi.

`NotPresent` paydaya girseydi, **çelişen kanıt bölümü hiç üretmeyen bir model en
iyi skoru alırdı.** Ölçü, alanı doldurmak için önemsiz bir şey uyduran modeli
yakalamak için var.

---

## 2 · Verilen kararlar

### KARAR · `ContradictingEvidenceVerdict.Unspecified = 0`

Varsayılan değer artık bir **anlam taşımıyor**. Önceki hâlde `NotPresent = 0`
idi, yani alanı doldurmayan bir çağıran sessizce *"bölüm yoktu"* diyordu — ve o
cümle tiyatro oranının paydasını etkiliyor.

Bu deponun en pahalı **önlenmiş** hatasının aynı sınıfı: EF'in ürettiği
`enabled → status` göçü her pasif kuralı sessizce açıyordu.

`Unspecified` paydaya girmiyor ve `NotPresent`'tan **ayrı** sayılıyor:
*"kimse söylemedi"* ile *"bölüm yoktu"* farklı şeyler.

⚠️ **Üretilen göç bu değişikliği göremedi.** `dotnet ef migrations add` yalnızca
yeni kolonu gördü; enum değerlerinin kaydığını görmedi, çünkü enum kodda da
veritabanında da bir sayı — şema açısından hiçbir şey değişmemiş görünüyor. Ama
`contradicting_evidence = 1` dün `Sound`, bugün `NotPresent`. Veri taşıması
**elle** yazıldı ve azalan sırada koşuyor (artan sırada aynı satır iki kez
yakalanır ve tablo tek değere çöker).

### KARAR · `accuracy@k` **sıra** ile ölçülüyor, metin eşleştirmesiyle değil

`GoldenReviewEntity.CorrectFindingRank`, nullable int, 1 tabanlı.
`accuracy@1` = `rank == 1`, `accuracy@3` = `rank <= 3` — **tek alandan**, ikinci
bir eksen yok.

`ActualRootCause` ile bulgu metinlerini eşleştirmek bir dizge işi olurdu ve bu
depoda o sınıfın dört örneği **kolonu ve sorgusu doğru olan** yerlerde çıktı.
İnsan zaten listeye bakıyor; ondan bir sayı istemek hem ucuz hem kesin.

`null` = **hiçbir bulgu doğru değildi** ve bu bir **ölçüm**, eksik veri değil.

### KARAR · "Soru sorulmadı" hâli **açık bir alandan** okunuyor

`CorrectFindingRank` `null` iki farklı şey olabilirdi: *"hiçbir bulgu doğru
değildi"* (bir ölçüm) ya da *"soru sorulmadı"* (ölçümün yokluğu).

**İlk tasarım ayrımı `SchemaVersion`'a bağlıyordu ve yanlıştı.** T38 o alanı
tam bu gün için taşımıştı — *"kolonun varlığı ile doldurulmuş olması ayrı
şeyler"* — ama sürüm burada yetmiyor, ve sebebi ancak **iki yakalama yolu da
bağlandığında** göründü:

| Yol | Bulguları gösteriyor mu | Soruyu sorabiliyor mu | Yazdığı sürüm |
| --- | --- | --- | --- |
| Rapor ekranı | ✅ | ✅ | 2 |
| **Alarm kapatma ekranı** | ❌ | ❌ | **2** |

`closeRequest`, `reviewRequest`'i paylaşıyor (doğru — §9, tek kurucu), yani
kapatma yoluyla yazılan her inceleme bugünkü sürümle yazılıyor ama soru hiç
sorulmuyor. Sürüme bağlansaydı o kayıtların hepsi paydaya girer ve
`accuracy@1`'i sessizce aşağı çekerdi.

Ayrım bu yüzden `CorrectFindingRankAsked` adlı **açık bir kolonda**. T36'nın
`Measured=false` ↔ `Unreliable=0` ayrımının aynısı: ölçümün **yapıldığı** ayrı
alan, **sonucu** ayrı. Sürüm yine 2'ye çıktı (kayıt biçimi gerçekten değişti)
ama payda ona bakmıyor.

**Tutarsız kombinasyon reddediliyor:** sıra verilmiş + sorulmamış → 400. Kabul
edilseydi kayıt **paya girer, paydaya girmezdi** — %100'ü aşabilen bir oran.

**Bu, yakalama yolunun aynı commit'te bağlanmasını zorunlu kıldı.** Ölçü
yazılıp form sorulmasaydı bütün kayıtların rank'i `null` olurdu ve
`accuracy@1` **%0** çıkardı — ölçülmemiş bir şey "ölçüldü, berbat" diye
okunurdu.

### KARAR · Sıfır ve negatif sıra reddediliyor

Sessizce kabul edilseydi kayıt paydaya girer, hiçbir `accuracy@k` kovasına
düşmezdi: oranı aşağı çeken, sebebi görünmeyen bir satır. *"Hiçbiri doğru
değildi"*nin ifadesi `null`.

### KARAR · Tiyatro oranına **eşik konmuyor**

Bu bir **ölçüm**, kapı değil. ARGUS'un %80–85 bandı doğruluk için referans;
tiyatro için karşılığı yok ve uydurulmuş bir sayı bu depoda *"bir gün kimsenin
bakmadığı rakam"*a dönüşür.

**F4'ün bitti tanımı için ölçüt sayının kendisi değil, ölçümün varlığı:**

> Tiyatro oranı altın küme üzerinde **hesaplanıyor**, paydası görünür, ve
> **kaydedilmiş bir sayısı var**.

Eşik, elimizde bir taban olduğunda konur; taban da ilk gerçek koşumdan gelir.
Bu satır *"eşik unutuldu"* diye okunmasın diye burada duruyor.

---

## 3 · Ölçülen bir bekçi kusuru — fixture kusuru ifade edemiyordu

Kapsam sızıntısı bekçisi, mutasyona rağmen **yeşil kaldı**. Sebep kodda değil
**fixture'daydı**: kapsam içinde `Trivial`, dışında yalnızca `Sound` vardı,
dolayısıyla `Trivial` sayacının kapsam filtresi kaldırıldığında sayı
değişmiyordu.

Bekçi doğru şeyi iddia ediyor ve **yanlış sebeple** geçiyordu.

Bunu ne iddia adımı ne de yeşil koşum gösterebilirdi — yalnızca mutasyon
gösterdi. İkisi ayrı sorular:

| Adım | Sorduğu soru |
| --- | --- |
| İddia (`assert 'KIRMIZI' in dosya`) | Kusur gerçekten uygulandı mı |
| Mutasyon | **Bekçi onu görüyor mu** |

Düzeltme: dış grupta **her cinsten** satır. Kural testin kendi yorumunda:
*buraya sayaç ekleyen fixture'ı da büyütmeli.*

---

## 4 · Bugünkü durum

| Parça | Durum |
| --- | --- |
| Çelişen kanıt sayaçları + tiyatro oranı | ✅ |
| `Unspecified` + veri taşımalı göç | ✅ |
| `CorrectFindingRank` + `accuracy@1` / `accuracy@3` | ✅ |
| Yakalama yolu (iki yazma ucu + rapor ekranındaki soru) | ✅ |
| Gösterge ekranı — paydalar oranların yanında | ✅ |
| **Atılan cümle oranı** (`reasoning` sözleşmesi) | ✅ |

### Altın küme ilk gün boş ve bu **beklenen**

Tohumlayıcı (`SeedCommandHandlers.Golden`) kanıt paketi ve örnek yazıyor,
**inceleme yazmıyor** — `GoldenReviewEntity`'yi yazan tek yol insan. Yani ilk
gün her iki payda da haklı olarak sıfır ve gösterge *"ölçülmedi"* diyor,
*"%0"* değil.

Bu satır yazılı olmasaydı boş payda bir kurulum hatası gibi okunabilirdi.

---

## 4b · Atılan cümle oranı — eksen ve üç hâl

### KARAR · Eksen **incelenmiş paket**, paket başına **son** rapor

Altın küme bir *(paket, gerçek kök neden)* çiftleri kümesi, dolayısıyla ölçümün
birimi paket. Paket başına son rapor sayılıyor ve sıralama
`RcaReportStore.LatestForAsync`'inkiyle **birebir aynı** — ayrışsalardı ekran
bir raporu gösterir, gösterge başkasını sayardı.

`rca_reports`'un kendi `owner_group` kolonu **yok**; kapsam pakete bağlı. O
yüzden hangi raporların ölçüleceği, kullanıcının görebildiği **incelemelerden**
türüyor. İkinci bir kapsam yolu açmak K17'nin dağıtılmasını yasakladığı şey.

⚠️ **Bilinen sınır:** bir paket incelendikten *sonra* yeniden koşturulursa
ölçülen rapor, insanın yargıladığı rapor olmayabilir. Bugün bunu ayırt edecek
bir bağ yok — inceleme pakete bağlı, rapora değil.

### KARAR · Oran **cümle başına**, rapor başına değil

Rapor başına oranların ortalaması alınsaydı iki cümle yazan bir rapor, iki yüz
cümle yazanla aynı ağırlığı taşırdı. Sorulan soru *"bu korpusta modelin yazdığı
cümlelerin kaçta kaçı desteksizdi"*, yani payda **cümle**.

### Üç hâl — ikisi toplamların içinde görünmüyor

| Hâl | Sayaç | Toplamlara katkısı |
| --- | --- | --- |
| **A** raporu yok | `reasoning_absent` | **paydada değil** |
| **B** koştu, üretmedi | `produced_nothing` | ikisine de 0 |
| **C** koştu, hepsi atıldı | `all_dropped` | ikisine de eşit |

B ve C ayrı sayılmasaydı toplamlar onları gizlerdi: ikisi de oranı **hareket
ettirmiyor** ama zıt şeyler söylüyor. C, modelin cümle uydurup hepsinin elendiği
hâl — F4'ün ölçmek istediği şeyin kendisi.

`measured_coverage` (`reports_measured / reviewed_bundles`) oranın yanında
duruyor: düşükse üstteki sayı altın kümenin küçük bir diliminden geliyor.

### Ölçülen kırmızı — ve fixture yine bir bekçiyi yanlış sebeple geçirdi

Altı mutasyonun beşi bir testi düşürdü. **Altıncısı — uydurma atıf paydasını
`dropped` yerine `produced` yapmak — yeşil geçti.**

Sebep kodda değil fixture'daydı: o örnekte `produced == dropped` idi, yani iki
payda **aynı sayıyı** veriyor. `10 üretildi / 4 atıldı / 2 uydurma` örneği
eklendi (doğru oran `0,5`, yanlış payda `0,2` verirdi) ve mutasyon artık
düşüyor.

Bu, aynı şeklin bu ticket'ta **üçüncü** tekrarı (kapsam bekçisi, sürüm tabanlı
payda, ve şimdi bu). Ortak ders: *bir bekçinin yeşil olması, kusuru gördüğü
anlamına gelmiyor — fixture'ın kusuru **ifade edebilmesi** gerekiyor.*

---

## 5 · Denetimde kalan sorular

### T44'ün bıraktığı üç tereddüt

1. Atıfsız cümle bir öncekinden **bağlam devralmıyor** (bilerek). İnsan gibi
   yazan bir modelde atılan cümle oranını yukarı çekebilir; çözüm eşik değil
   **prompt** ayarı olabilir. **→ ÖLÇÜLDÜ, §7'ye bakın.**
2. Kapı atfın **yerindeliğini değil varlığını** ölçüyor. Doğru kimliğe atıf
   yapan ama ilgisiz bir cümle bağlanmış sayılıyor — tiyatronun yaşadığı yer.
   Bu ticket'ın insan tarafı kuruldu (`Trivial` kararı); **ucuz makine vekili
   eklenmeyecek, §7'ye bakın.**
3. Cümle bölme **noktalama tabanlı**; kısaltma ve ondalık yanlış bölünebiliyor.
   **→ ÖLÇÜLDÜ, §6'ya bakın — iddianın yarısı yanlıştı.**

## 6 · Denetim (2026-09-15)

§4'ün tablosundaki **altı parçanın altısı** ✅. Ticket'ta *"şu kriter
koşturulmadı, o yüzden 1"* diye okunabilir bir satır yoktu; kalan iş §5'in üç
tereddüdü ve **üçü aynı sınıfta değil**. Denetimin ilk çıktısı o ayrım:

| Tereddüt | Sınıf | Neden |
| --- | --- | --- |
| 1 · Atıfsız cümle bağlam devralmıyor → oran yükselebilir | **canlı model** | Sorulan şey mekanizma değil **pratikteki oran**: insan gibi yazan bir modelin ürettiği metinde kaç cümle atıfsız kalıyor. Mekanizma zaten yazılı ve bilinçli bir karar; ölçülecek olan davranış |
| 2 · Kapı atfın yerindeliğini değil varlığını ölçüyor | **karar** | Makine tarafı bir **anlam** yargısı istiyor. Ucuz bir vekil (atıf ile cümle arasında sözcük örtüşmesi) yazılabilir ama o bir sezgi — ve bu depoda yanlış pozitifin bedeli yazılı. Yazılıp yazılmayacağı ürün kararı |
| 3 · Cümle bölme noktalama tabanlı | **şimdi yapılabilir → yapıldı, ve DÜZELTİLDİ** | Konteyner yok, model yok: bölme kuralına bilinen zor girdiler verildi, sonra kural düzeltildi |

### 3. tereddüdün ölçümü — ondalık GÜVENLİ, numaralı liste DEĞİL

Kural `(?<=[.!?])\s+|\r?\n+`, yani **noktalama + boşluk**. Bir nokta ancak
**ardından boşluk gelirse** sınır sayılıyor, ve buradan çıkan ayrım ticket'ın
cümlesinden keskin:

| Girdi | Bölünüyor mu | Not |
| --- | --- | --- |
| `3.14`, `10.0.0.1`, `net10.0`, `api.kurum.local` | **hayır** | noktadan sonra boşluk yok — *"ondalık yanlış bölünebiliyor"* iddiası **bugünkü kural için yanlış** |
| `vb.` · `örn.` · `bkz.` | **artık hayır** | kapalı kısaltma listesi; listede olmayan kısaltma hâlâ ölçülebilir biçimde bölünür |
| `1. Kök neden …` | **artık hayır** | satır başındaki `\d{1,3}\.` ayrı bir koruma; maddeler arasındaki satır sonu sınır kalır |

**Düzeltme kararı:** ölçü henüz bağlayıcı bir sayı üretmediği için bugün yapılan
değişiklik geçmişi yeniden tanımlamıyor; tanımı ilk kullanımdan önce doğru
kuruyor. Yarın aynı değişiklik geçmiş seriyi kırardı.

Bekçi: `SentenceSplitMeasurementTests` — ilk hâli bugünkü (yanlış) davranışı
çiviliyordu: **10 test**. Düzeltmeden sonra aynı dosya yeni davranışı çiviliyor:
**15 test**. Aşağıdaki karar o dönüşümü ve zamanlamasını anlatıyor.

### KARAR · Kural DÜZELTİLDİ, ve zamanlaması gerekçenin parçası

Yukarıdaki *"çiviliyor, düzeltmiyor"* kararı **aynı gün geri alındı** ve sebebi
gerekçenin kendi koşuluydu: değiştirmenin bedeli **korunacak bir geçmiş**
olduğunda doğar. Bu ölçü bugüne kadar **bağlayıcı tek bir sayı üretmedi** —
ticket açık, canlı model koşumu hiç yapılmadı. Yarın değiştirmek bir *tanım
değişikliği* olurdu; bugün değiştirmek tanımı **ilk kullanımdan önce doğru
kurmak**.

Ve düzeltme tanımı **gevşetmiyor, sadakatini artırıyor**: numaralı liste en sık
karşılaşılan biçim, bölünen parça atıfsız kalıyor, yani metrik model kötü
yazmadığı hâlde *"kötü yazdı"* diyordu.

**İki kural, çünkü tek sezgi ikisini kapatmıyor:**

| Koruma | Ne | Neden ayrı |
| --- | --- | --- |
| Kısaltma listesi | `vb·vs·örn·ör·bkz·age·sy·yy·çev·haz·Dr·Doç·Prof·Sn·Nu·no·etc·e.g·i.e·Fig·vol` | **Elle ve kapalı**; türetmek bu üründe kaybedilmiş bir bahis. Listede olmayan kısaltma hâlâ bölüyor ve bu **ölçülü** (`Listede_olmayan_kisaltma_hala_boluyor`) |
| Numaralı liste | satır başındaki `\d{1,3}\.` sınır değil | Küçük harf sezgisi bunu **kapatmıyor** — `1. Kök` büyük harfle devam ediyor |

`RegexOptions.CultureInvariant` **zorunlu**: `IgnoreCase` tek başına o anki
kültürle katlama yapıyor ve `tr-TR`'de `I`/`ı` eşlemesi bambaşka — deponun altı
analizör kuralıyla hata seviyesine çektiği tuzağın regex tarafı.

**Testler silinmedi, çevrildi.** Her testin yanında **eski hâlin neden yanlış
olduğu** duruyor, ve **yanlışlanan iddia da duruyor**: *"ondalık yanlış
bölünebiliyor"* cümlesi ölçülüp çürütüldü, testi kayıt olarak kaldı — bir sonraki
okuyan aynı endişeyi tekrar üretip gereksiz bir koruma yazmaya kalkarsa cevabı
orada.

**Yan yana ölçüm korundu ve anlamı değişti:** eskiden kısaltmalı `Dropped = 1` /
kısaltmasız `0` idi (*aynı bilgi, farklı yazım, farklı metrik*); şimdi ikisi de
`0`. Tek sayı yazılmıyor, çünkü `Dropped = 1` tek başına *"model kötü yazdı"*
diye okunurdu.

**Ters yön de ölçüldü:** koruma satır sonunu yutarsa bütün liste tek cümle olur
ve oran **yanlış yönde** iyileşir — atıfsız maddeler atıflı bir maddenin
arkasına saklanır. `Maddeler_arasi_sinir_duruyor` bunu tutuyor.

Kırmızı ölçümü: `tools/t47-kirmizi-olcumu.py` — dört kusur, ve **A/B çifti iki
korumanın bağımsız olduğunu** gösteriyor (kısaltma korumasını düşürmek numaralı
liste testini kırmızı yakmıyor, ve tersi).

**Bu denetimin aramadığı:** bölme kuralının Türkçe dışındaki dillerde davranışı,
ve modelin gerçekten hangi biçimde yazdığı (numaralı liste ne sıklıkta çıkıyor —
o sayı 1. tereddütle aynı koşumdan gelir).

---

## 7 · Kapanış ölçümü ve karar (2026-09-21)

### Canlı model tabanı

`LiveModelQualityMeasurement`, taklit HTTP işleyicisi kullanmıyor: K6
`ModelBoundaryGate`'inden geçen gerçek bir loopback uç, gerçek
`OpenAiCompatibleModelProvider` ve ürünün gerçek `SentenceBinder`'ı aynı
koşumda çalışıyor. Varsayılan test koşumunda ağ yok; ölçüm yalnız
`BIZIGO_RCA_LIVE_MODEL=1` ile açılıyor.

Koşumda Mozilla AI'nin `TinyLlama-1.1B-Chat-v1.0.Q5_K_M.llamafile` modeli
kullanıldı:

- model SHA-256:
  `6b58cd9ad698cc3072b53dc7e98597e2618805a4658f0eba7660520d05f65d1e`
- uç: `llamafile v0.9.0`, `http://127.0.0.1:8091/v1/`
- korpus: kaynak kodunda sabit üç sentetik kanıt paketi; gerçek müşteri/log
  verisi yok
- istem: iki kısa Türkçe cümle, her cümlede görünür bir kanıt kimliği

| Paket | Üretilen | Tutulan | Atılan | Uydurma atıflı atılan |
| --- | ---: | ---: | ---: | ---: |
| `bgp-reset` | 6 | 1 | 5 | 0 |
| `database-pool` | 5 | 1 | 4 | 0 |
| `dns-timeout` | 5 | 2 | 3 | 0 |
| **Toplam** | **16** | **4** | **12** | **0** |

**İlk canlı taban:** atılan cümle oranı `12 / 16 = 0,750`; atılan cümleler
içindeki uydurma atıf oranı `0 / 12 = 0,000`.

Bu sonuç TinyLlama'nın bu isteme biçimsel uyumunun zayıf olduğunu gösteriyor:
model istenen iki atıflı cümle yerine daha uzun açıklamalar yazdı. **Bir ürün
eşiği, anlamsal doğruluk skoru veya başka model ailesine aktarılabilir kalite
iddiası değildir.** Aday üretim modeli aynı sabit korpusla yeniden ölçülür;
eşik ancak etiketli gerçek koşum tabanı oluştuğunda konuşulur.

### Atfın anlamsal yerindeliği — makine sezgisi eklenmiyor

Sözcük örtüşmesi gibi ucuz bir vekil, doğru kanıtı eş anlamlılarla açıklayan
bir cümleyi reddedebilir; kanıt metnini kopyalayıp yanlış sonuç çıkaran bir
cümleyi ise kabul edebilir. Etiketli bir veri kümesi olmadan bunun duyarlılık
ve seçiciliği ölçülemez. Ölçülmemiş bir sezgiyi bekçi yapmak sahte güven
üretirdi.

Bu yüzden bugünkü sınır bilinçli:

- makine kapısı **atıf varlığını** ve görünür kapsama çözülmesini ölçer;
- anlamsal yerindeliğin yetkili ölçüsü insanın `Trivial` / `Sound` incelemesidir;
- yeterli etiketli veri biriktiğinde makine vekili ayrı bir deney olarak
  ölçülür; doğrulanmadan ürün kapısına girmez.

Ticket bu canlı sayı ve karar kaydedildiği için kapanır. Gelecekteki model
yeniden ölçümü bakım işidir; bu ticket'ın eksik kabul kriteri değildir.
