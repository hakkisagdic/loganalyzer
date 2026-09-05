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

Üçünde de payda sıfırken oran **`null`, asla `0.0`**.

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

### KARAR · "Soru sorulmadı" hâli **şema sürümünden** okunuyor

`CorrectFindingRank` `null` iki farklı şey olabilirdi. Ayrımı taşıyan şey
`SchemaVersion` (bugün **2**, `RankSchemaVersion` sabitiyle çivili).

T38 bu alanı tam olarak bu gün için taşımıştı ve gerekçesini yazmıştı:

> Kolonun varlığı ile doldurulmuş olması ayrı şeyler; sürüm olmadan ikisi ayırt
> edilemez.

**Bu, yakalama yolunun aynı commit'te bağlanmasını zorunlu kıldı.** Ölçü
yazılıp form sorulmasaydı bütün v2 kayıtlarının rank'i `null` olurdu ve
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
| **Atılan cümle oranı** (`reasoning` sözleşmesi) | ⏳ **başlamadı** |

### Altın küme ilk gün boş ve bu **beklenen**

Tohumlayıcı (`SeedCommandHandlers.Golden`) kanıt paketi ve örnek yazıyor,
**inceleme yazmıyor** — `GoldenReviewEntity`'yi yazan tek yol insan. Yani ilk
gün her iki payda da haklı olarak sıfır ve gösterge *"ölçülmedi"* diyor,
*"%0"* değil.

Bu satır yazılı olmasaydı boş payda bir kurulum hatası gibi okunabilirdi.

---

## 5 · Sıradaki iş — atılan cümle oranı

T44'ün sayacı `RcaReportResponse.reasoning` altında ve **çivili**. Okunacak dört
sayı: `produced_sentence_count` (payda), `dropped_sentence_count` (pay),
`dropped_sentence_ratio` (payda 0 ise `null`),
`fabricated_citation_sentence_count` (`dropped`'ın alt kümesi).

**Karıştırılmaması gereken üç hâl** — ve bu, yukarıdaki payda disiplininin aynısı:

| Hâl | Tel | Anlamı |
| --- | --- | --- |
| A | `reasoning: null` | Model **hiç koşmadı** → paydaya katma |
| B | `produced: 0` | Koştu, hiç cümle üretmedi → ayrı say |
| C | `produced: 5, dropped: 5` | Koştu, **hepsi atıldı** → ayrı say, **en pahalısı** |

B ile C saf sayımda aynı görünüyor (ikisi de boş bulgu listesi), ama C modelin
beş cümle uydurup hepsinin elendiği hâl — F4'ün ölçmek istediği şeyin kendisi.

### T44'ün bıraktığı üç tereddüt — ölçümü burada

1. Atıfsız cümle bir öncekinden **bağlam devralmıyor** (bilerek). İnsan gibi
   yazan bir modelde atılan cümle oranını yukarı çekebilir; çözüm eşik değil
   **prompt** ayarı olabilir. Ölç, sonra karar ver.
2. Kapı atfın **yerindeliğini değil varlığını** ölçüyor. Doğru kimliğe atıf
   yapan ama ilgisiz bir cümle bağlanmış sayılıyor — tiyatronun yaşadığı yer.
   Bu ticket'ın insan tarafı kuruldu (`Trivial` kararı); **makine tarafı yok**.
3. Cümle bölme **noktalama tabanlı**; kısaltma ve ondalık yanlış bölünebiliyor.
   **Ölçülmedi.**
