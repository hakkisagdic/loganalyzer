---
kind: spec
title: "T36 → T37 devir notu — ekranı yazan için"
---

# T36 → T37 devir notu

T36 kanıt paketini ve LLM'siz raporu yazdı; T37 onu ekrana ve export'a taşıyor.
Bu belge, ekranı yazan kişinin **kodu okuyarak çıkarması gereken** şeyleri
okumadan bilmesi için.

Kod: `src/Bizigo.Evidence/`. Testler: `tests/Bizigo.UnitTests/Evidence*.cs`,
`DeterministicReportTests.cs`. Karar gerekçeleri:
[T36 kanıt paketi](../t36-kanit-paketi/index.md).

---

## 0 · Çivilenmiş değişmez — bunu bozmak sessiz

> **Dört durum yanıtta, ekranda ve export'ta ayırt edilebilir kalmak
> zorundadır.** `Empty`, `NeverFed`, `Unavailable`/`Failed` ve `NotRegistered`
> tek bir "veri yok" değerine indirgenemez.

Bu bir stil tercihi değil. T34 ve T36'nın kurduğu her şey bu ayrımın üstünde
duruyor ve ekranda tek bir "veri yok" kutusu çizmek, ikisini de tek satırda
geri alır. Üstelik **hiçbir şey haber vermez**: hata yok, sayaç yok, belirti
yok — yalnızca rapor okuyanın yanlış bir sonuca varması.

En pahalısı `NeverFed`: "değişiklik akışı hiç beslenmemiş" cümlesi ekranda
"değişiklik olmadı" diye görünürse, kullanıcı RCA'nın en güçlü sinyalinin
yokluğunu bir **bulgu** sanır ve kök nedeni başka yerde aramaya başlar.

Aynı sınıf `WindowTrust.Unmeasured` için de geçerli: "ölçemedik" ile "sıfır"
farklı ve ikincisi "sorun yok" diye okunuyor.

---

## 1 · Dört ayrım ve ekranda ne demeleri gerektiği

`EvidenceStatus` (`src/Bizigo.Evidence/EvidenceContract.cs`) altı değer taşıyor;
ekran açısından anlamlı gruplama şu:

| Durum | Ne oldu | Ekranda | Bölüm |
| --- | --- | --- | --- |
| `Gathered` | Koştu, kanıt buldu | Bulgu satırları | **Bulgular** |
| `Empty` | Koştu, pencerede eşleşme **yok** | "bakıldı, bu pencerede yok" | **Bakıldı, kanıt çıkmadı** |
| `NeverFed` | Kaynak **hiç** beslenmemiş | "besleme bağlı değil — 'değişiklik olmadı' demek DEĞİL" | **Bakılmayanlar** |
| `Unavailable` | Sağlayıcı kayıtlı, koşamıyor | "kapalı" | **Bakılmayanlar** |
| `Failed` | Koştu ve patladı | "hata" + gerekçe | **Bakılmayanlar** |
| `NotRegistered` | Bu tür için sağlayıcı yok (F5) | "bu türe hiç bakılmadı — F5" | **Bakılmayanlar** |

`EvidenceSlice.IsEvidence` yalnızca `Gathered` ve `Empty` için `true`. Rapor bu
ayrımı zaten yapıyor:

- `DeterministicReport.Silent` → koşan ama bir şey bulamayanlar
- `DeterministicReport.NotConsulted` → bakılamayanlar

**İkisini tek listede birleştirmeyin.** Ayrı durmalarının tamamı bu.

`EvidenceSlice.Detail`, `Gathered` dışındaki her durumda dolu ve **insan
okunur**: "neden bakılmadı" sorusunun cevabı orada. Ekranda gösterilmezse durum
etiketi tek başına yeterli bilgi vermiyor.

### Boş liste ≠ boş bölüm

`NotConsulted` boş olması "her şeye bakıldı" demek — bu bir **bilgi** ve
gösterilmeye değer. Bugün F5'in üç türü (`metric`, `trace`, `topology`) her
zaman `NotRegistered` döndüğü için liste hiç boş olmayacak; ama o gün geldiğinde
bölümün sessizce kaybolması yanlış olur.

---

## 2 · `RankedEvidence` — nedir, **ne değildir**

`src/Bizigo.Evidence/EvidenceRanking.cs`.

```
Score = ClassRank + (Weight / dilimdeki_en_büyük_Weight)
```

**Ne olduğu:** kanıt satırlarının tek bir sıraya dizilmiş hâli. `Findings`
skora göre, `Timeline` zamana göre sıralı — **aynı satırlar, iki görünüm.**
İkinci bir liste üretmeyin; tek kaynak var.

**Ne olmadığı — üç madde, üçü de önemli:**

1. **Skor pakete yazılmıyor ve ekran da saklamamalı.** Türetilmiş bir değer:
   `DeterministicReport.From(bundle)` her çağrıldığında yeniden hesaplanıyor.
   Sıralama bir **yargı** ve zamanla düzeltilecek; skoru bir yere yazmak, o
   düzeltmeyi geçmişe uygulanamaz yapar. Ekranda cache'lenmesi gereken şey
   paket, rapor değil.

2. **Skor kullanıcıya gösterilecek bir sayı değil.** `4.73` ekranda hiçbir şey
   ifade etmiyor ve gösterilirse ölçülmüş bir kesinlik iddiası olur. Skorun işi
   **sıra**; gösterilmesi gereken şey sıranın kendisi ve sağlayıcının adı.

3. **Hipotez değil.** "Şu değişiklik şunu bozdu" cümlesini kuran taraf F4.
   T36'nın ürettiği şey gözlemler, kaynaklarıyla ve sırasıyla. Ekran bunları
   "bulgu" diye sunabilir ama "neden" diye sunamaz.

### Sınıf sırası — ekranda gruplama yapacaksanız

`change.feed` (6) → `logs.first-seen` (5) → `logs.propagation` (4) →
`logs.silence` (3) → `logs.volume` (2) → `logs.attribute-lift` (1) →
`logs.window` (0).

Sınıf adımı 1,0 olduğu için **sınıf içi hiçbir büyüklük bir üst sınıfı
geçemiyor**. Yani listeyi skora göre çizdiğinizde sinyal cinsleri zaten
kümelenmiş geliyor; ayrıca gruplamak isterseniz `RankedEvidence.ClassRank`
hazır.

Bu sıra bir yargı, ölçüm değil — doğrulanacağı yer T38'in altın kümesi.
Ekranın onu **kalıcı bir gerçek** gibi sunmaması iyi olur.

---

## 3 · `Drilldown` — "ilgili aramaya bağlantı" maddesinin doğrudan karşılığı

Ticket'ın şu maddesi: *"Her bulgudan ilgili aramaya bağlantı — 'şu imza ilk kez
göründü' satırı, o imzayı arayan sorguyu doğru zaman aralığıyla açıyor."*

Karşılığı `EvidenceItem.Drilldown` ve tipi **`EventQuery`**, ham SQL değil.

```csharp
public sealed record EvidenceItem(
    string Id, string ProviderId, EvidenceKind Kind,
    DateTimeOffset Timestamp, double Weight, string Summary,
    IReadOnlyDictionary<string, string> Payload,
    EventQuery? Drilldown = null);
```

**Neden `EventQuery`:** kanıt paketi **saklanıyor**. SQL dizgisi taşımak, kapsam
kapısını (K17) atlayan bir yolu diske yazmak olurdu — altı ay sonra o paketi
açan biri, o günkü yetkisinden bağımsız bir sorgu elde ederdi. Yapılandırılmış
sorgu UI'dan `IScopedQuery`'ye veriliyor ve **kapsam kapısı yeniden
uygulanıyor**.

Ekran için pratik sonuç: `Drilldown` zaten `From`/`To`/`OwnerGroups`/`Filters`
taşıyor, yani olay arama ekranının URL'ine çevrilmesi mekanik bir iş.
`ui/src/lib/events/criteria.ts` bu çevrimin yaşadığı yer.

`Drilldown` **null olabilir** — `change.feed` satırları taşımıyor, çünkü
değişiklik kayıtları olay tablosunda değil. Ekran null'ı "bağlantı yok" diye ele
almalı, boş bir arama açmamalı.

`Payload` her bulgu için sağlayıcıya özgü ham sayıları taşıyor
(`signature_hash`, `z_score`, `lift`, `lag_seconds`, `unreliable_time_count`…).
Detay panelinde göstermek için hazır; anahtarlar `snake_case`.

---

## 4 · Export — `ToMarkdown()` hazır

```csharp
var report = DeterministicReport.From(bundle);
var markdown = report.ToMarkdown();
```

`src/Bizigo.Evidence/DeterministicReport.cs`. Ticket'ın export maddesi
(*"Markdown ya da PDF"*) için Markdown tarafı yazılmış ve testli.

Kabul kriteri şunu diyor: *"Export edilen rapor kendi kendine yeten bir belge:
bağlantılar çalışmasa bile içerik anlaşılıyor."* Üretilen metin buna göre
yazıldı — her bulgu özeti ham veriyi taşıyor, bağlantıya ihtiyaç duymuyor.

Ve şunu: *"Kapsam dışı ve zaman uyarıları export'ta da var — ekranı okuyan
görüyor ama PDF'i okuyan görmüyorsa uyarı işe yaramaz."* Üç uyarı da
Markdown'da ve **en üstte**:

- kapsam dışı sayım (yalnızca sayı, içerik yok)
- zaman güvenilmezliği (ya da "ölçülemedi")
- eksik kanıt (bir sağlayıcı patladı / bütçeye takıldı / kırpıldı)

**Uyarıların en üstte olması bilinçli:** raporun sonunda duran bir kısıt
okunmuyor, ve okunmayan bir kısıt hiç yazılmamış gibi. Ekranda da aynı yere
koymanızı öneririm.

PDF'i Markdown'dan üretirseniz uyarılar bedavaya geliyor. Ayrı bir yol
yazarsanız, o yolun üç uyarıyı da taşıdığını sınayan bir test şart — aksi
halde ekran ile PDF sessizce ayrışır.

### Bir uyarı: Markdown tablo kaçırma

`Escape()` gövdedeki `|`, `\r` ve `\n` karakterlerini kaçırıyor. Teorik bir
risk değil — kanıt özetleri ham log satırı taşıyor ve syslog gövdelerinde boru
işareti sık. Kendi biçimlendiricinizi yazarsanız aynı şeyi yapmanız gerekiyor.

---

## 5 · Paketi nereden alacaksınız

Üç tip, hepsi DI'de kayıtlı (`AddBizigoEvidence`, scoped):

| Tip | İşi |
| --- | --- |
| `EvidenceBundleFactory.BuildAsync(window, scope, budget, ct)` | Sağlayıcıları koşturur, zaman güvenilirliğini ölçer, paketi kurar. **Saklamaz.** |
| `EvidenceBundleStore.SaveAsync / GetAsync / ListRecentAsync` | Postgres kalıcılığı |
| `DeterministicReport.From(bundle)` | Rapor — saf, I/O yok |

`ListRecentAsync` **JSON açmadan** okuyor (üst veri kolonlarda): liste ekranı
her satır için belge çözmek zorunda değil. `EvidenceBundleSummary` şu alanları
veriyor: `Id`, `GatheredAt`, `WindowFrom`, `WindowTo`, `ContentHash`,
`OutOfScopeCount`, `IsPartial`, `SchemaVersion`.

`GetAsync` **okunamayan sürümde istisna atıyor**, `null` dönmüyor — "paket yok"
ile "paket var ama okuyamıyoruz" farklı. Ekranın bu ikisini farklı göstermesi
gerekiyor (dört durum kuralının bir başka yüzü).

### API ucu bilerek yazılmadı

§8: *tüketicisi olmayan bir tip tahmindir.* Uç, onu gerçekten tüketen ekranla
birlikte gelmeli ve alan seçimlerini ekranın ihtiyacı belirlemeli. Bu yüzden
`EvidenceResponses` diye bir dosya yok — **sahibi T37**.

İki hatırlatma uç yazarken:

- **Domain tipini tel sözleşmesi yapmayın** (§8). `EvidenceBundle` ve
  `DeterministicReport` domain tipleri; onlara eklenen her alan kimse karar
  vermeden API'ye sızar. Ayrı `record` yazın. Aynı karar T27'de
  `ReplayResponse` için verildi, `src/Bizigo.Api/ReplayResponses.cs` iyi bir
  örnek.
- **JSON `snake_case`**, `JsonPropertyName` ile.

---

## 6 · Zaman güvenilirliği — `WindowTrust`

```csharp
public sealed record WindowTrust(long TotalEvents, long UnreliableTimeEvents, bool Measured = true)
```

`UnreliableTimeEvents` = penceredeki `time_source != parsed` olan olay sayısı,
**pencerenin tamamı** üzerinden ölçülmüş (bir sağlayıcıdan türetilmemiş —
türetilseydi yayılma boş döndüğünde sessizce sıfır olurdu).

Ekranda üç ayrı durum:

| | Anlamı | Ekranda |
| --- | --- | --- |
| `Measured = false` | Ölçüm patladı | "ölçülemedi — bilinmiyor" |
| `Measured, Unreliable = 0` | Ölçüldü, hepsi güvenilir | **Uyarı yok** |
| `Measured, Unreliable > 0` | Ölçüldü, kısmı güvenilmez | Sayı + oran + "yayılma sırası kaymış olabilir" |

Ortadaki satır önemli: **her raporda duran bir uyarı hiçbir şey söylemez.**
Rapor bu yönde de test edilmiş (`Guvenilir_zamanlarda_uyari_yok`).

`UnreliableRatio` ölçülmediyse `null` — sıfır değil.

---

## 7 · Kapsam dışı dürüstlüğü

`bundle.OutOfScopeCount` **yalnızca bir sayı**. İçerik yok, grup adı yok,
kimlik yok — bir test paketin hiçbir yerinde kapsam dışı içerik olmadığını
tutuyor (K17, RCA §3.2).

Rapordaki cümle: *"Kapsamınız dışında **342** ilişkili kayıt var. Tam analiz
için ilgili grubun sahibiyle görüşün."*

Ekranda **hangi grubun** sahibiyle görüşüleceğini yazmak cazip ama o bilgi
kasten yok: grup adı da bir sızıntı. Cümle bilinçli olarak belirsiz.

`0` olduğunda satır **hiç yazılmıyor** — her raporda duran bir uyarının değeri
sıfır.

---

## 8 · F4 için yer

Ticket diyor: *"F4'ün LLM yorumu bu ekrana eklenecek. Yer şimdiden ayrılmalı ki
o zaman ekran yeniden tasarlanmasın — ve yorum geldiğinde kanıtın yerine
geçmemeli, yanına gelmeli."*

T36 tarafında bunu destekleyen iki şey var:

- **Paket saklanıyor**, yani aynı kanıt üzerinde farklı model/prompt
  koşturulabiliyor. Ekranın "bu rapor hangi paketten üretildi" bağını
  göstermesi (`BundleId`, `ContentHash`) F4'te karşılaştırmayı mümkün kılıyor.
- **Rapor deterministik**: aynı paket her zaman aynı metni üretiyor. Yorum
  geldiğinde değişen tek şey yorum olacak; kanıt bölümü sabit kalacak.

RCA belgesindeki ekran taslağı (`docs/epic/rca-raporu-ozelligi/index.md` §6)
yorumun nereye geleceğini zaten çiziyor: özet + bulgular solda, sağ panelde
sağlayıcılar ve model bilgisi. O taslaktaki üç bilinçli şey — kapalı
sağlayıcılar görünüyor, desteklenmemiş bulgu kırmızı rozetli, çelişen kanıt
bulgunun yanında — T37'de karşılığını bulmalı; ilki zaten bu notun §1'i.

---

## 9 · Bize sorabileceğiniz şeyler

T36'yı yazan ajan (`t36-kanit-paketi` dalı) ulaşılabilir. Özellikle şu üçünde
kodu okumak yerine sormak daha hızlı:

1. **Sınıf sırasını değiştirmek gerekirse** — tablo `EvidenceRanking.ClassRanks`
   ve bir bekçi testi kayıtlı her sağlayıcının orada olduğunu tutuyor. Sıra
   tartışmaya açık; sessizce değiştirilmemeli.
2. **Yeni bir dürüstlük satırı gerekirse** — `AppendHonesty` tek yer ve
   Markdown ile ekranın ayrışmaması için ikisinin aynı kaynaktan beslenmesi
   iyi olur.
3. **Paket şeması değişmesi gerekirse** — `CurrentSchemaVersion` ve
   `MinReadableSchemaVersion` ayrı sabitler, ve kaynakta elle duran bir v1
   fixture'ı var. Sürüm artırmak iki ayrı bilinçli hareket gerektiriyor.
