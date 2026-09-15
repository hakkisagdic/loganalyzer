---
title: "T47 — Kalite ölçümü"
kind: ticket
status: 1
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

## 5 · Kalan iş

### T44'ün bıraktığı üç tereddüt — hâlâ ölçülmedi

1. Atıfsız cümle bir öncekinden **bağlam devralmıyor** (bilerek). İnsan gibi
   yazan bir modelde atılan cümle oranını yukarı çekebilir; çözüm eşik değil
   **prompt** ayarı olabilir. Ölç, sonra karar ver.
2. Kapı atfın **yerindeliğini değil varlığını** ölçüyor. Doğru kimliğe atıf
   yapan ama ilgisiz bir cümle bağlanmış sayılıyor — tiyatronun yaşadığı yer.
   Bu ticket'ın insan tarafı kuruldu (`Trivial` kararı); **makine tarafı yok**.
3. Cümle bölme **noktalama tabanlı**; kısaltma ve ondalık yanlış bölünebiliyor.
   **→ ÖLÇÜLDÜ, §6'ya bakın — iddianın yarısı yanlıştı.**

## 6 · Neden hâlâ `status: 1` — denetim (2026-09-15)

§4'ün tablosundaki **altı parçanın altısı** ✅. Ticket'ta *"şu kriter
koşturulmadı, o yüzden 1"* diye okunabilir bir satır yoktu; kalan iş §5'in üç
tereddüdü ve **üçü aynı sınıfta değil**. Denetimin ilk çıktısı o ayrım:

| Tereddüt | Sınıf | Neden |
| --- | --- | --- |
| 1 · Atıfsız cümle bağlam devralmıyor → oran yükselebilir | **canlı model** | Sorulan şey mekanizma değil **pratikteki oran**: insan gibi yazan bir modelin ürettiği metinde kaç cümle atıfsız kalıyor. Mekanizma zaten yazılı ve bilinçli bir karar; ölçülecek olan davranış |
| 2 · Kapı atfın yerindeliğini değil varlığını ölçüyor | **karar** | Makine tarafı bir **anlam** yargısı istiyor. Ucuz bir vekil (atıf ile cümle arasında sözcük örtüşmesi) yazılabilir ama o bir sezgi — ve bu depoda yanlış pozitifin bedeli yazılı. Yazılıp yazılmayacağı ürün kararı |
| 3 · Cümle bölme noktalama tabanlı | **şimdi yapılabilir → yapıldı** | Konteyner yok, model yok: bölme kuralına bilinen zor girdiler verildi |

### 3. tereddüdün ölçümü — ondalık GÜVENLİ, numaralı liste DEĞİL

Kural `(?<=[.!?])\s+|\r?\n+`, yani **noktalama + boşluk**. Bir nokta ancak
**ardından boşluk gelirse** sınır sayılıyor, ve buradan çıkan ayrım ticket'ın
cümlesinden keskin:

| Girdi | Bölünüyor mu | Not |
| --- | --- | --- |
| `3.14`, `10.0.0.1`, `net10.0`, `api.kurum.local` | **hayır** | noktadan sonra boşluk yok — *"ondalık yanlış bölünebiliyor"* iddiası **bugünkü kural için yanlış** |
| `vb.` · `örn.` · `bkz.` | **evet** | iddianın doğru çıkan yarısı |
| `1. Kök neden …` | **evet** | **ticket bunu saymıyordu** ve en sık karşılaşılacak hâl bu: modeller gerekçeyi numaralı liste hâlinde yazıyor |

**Bedeli de ölçüldü ve ücretsiz değil:** atıf parçalardan yalnızca birinde
kalıyorsa diğer parça **atıfsız** sayılıyor ve atılan cümle oranının **payına**
yazılıyor. Ölçüm yan yana yapıldı — kısaltmalı cümle `Dropped = 1`, aynı
cümlenin kısaltmasız hâli `Dropped = 0` — çünkü tek başına ölçülen bir sayı
*"model kötü yazdı"* diye de okunabilirdi.

Bekçi: `SentenceSplitMeasurementTests` (10 test, 0 düştü). **Bugünkü davranışı
çiviliyor, düzeltmiyor:** bölme kuralını değiştirmek atılan cümle oranının
**tanımını** değiştirir, yani ölçünün kendi tabanını — bu bir ajan kararı değil.

**Öneri (karar koordinatörde):** kural, noktadan sonra gelen sözcük **küçük
harfle** başlıyorsa bölmeyi reddedecek şekilde daraltılabilir; bu hem `vb. bileşenler`
hem `1. Kök` hâllerini kapatmaz (ikincisinde sözcük büyük harfli). Numaralı liste
için ayrı bir kural gerekiyor: satır başındaki `\d+\.` bir cümle sonu değil.
İkisi ayrı ayrı ölçülmeli, çünkü ikisi paydayı **farklı yönlerde** oynatıyor.

**Bu denetimin aramadığı:** bölme kuralının Türkçe dışındaki dillerde davranışı,
ve modelin gerçekten hangi biçimde yazdığı (numaralı liste ne sıklıkta çıkıyor —
o sayı 1. tereddütle aynı koşumdan gelir).
