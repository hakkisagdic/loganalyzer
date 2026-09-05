---
kind: ticket
title: "T51 — rca_report kalıcılığı ve rapor yüzeyi"
status: 2
---

# T51 — `rca_report` kalıcılığı ve rapor yüzeyi

[T44](../llm-adimlari-ve-iki-kapi/index.md) Karar 1'in sayacını **üretti**;
kimse göremiyordu. `DescribeDroppedSentences` ve `ToMarkdown` belge düzeyinde
duruyordu, tablo yoktu, uç yoktu, ekran yoktu.

Karar 1'in cümlesi iki fiil taşıyor — *"atıldığı **sayılıyor** ve
**gösteriliyor**"* — ve ikincisi bir depolama kararı değil bir **görünürlük**
kararı. Yalnızca ölçüp saklamak, *"ölçemedim"* ile *"sorun yok"*u yine aynı
çıktıya indirirdi.

---

## 1 · Kapsam kararı: (a) uçtan uca

§8'in kısıtı seçimi belirledi: **tüketicisi olmayan bir tip tahmindir.**

İki yol vardı — (a) depolama + uç + ekran, (b) depolamada durup bekleyen
sözleşmeyi `ProducesContractTests.Pending`'e yazmak. **(a) seçildi** ve gerekçe
ölçüldü, tahmin değil:

| | Beklenen maliyet | Ölçülen |
| --- | --- | --- |
| Ekran | Yeni bir ekran yazmak | **Ekran zaten var** (`ui/src/app/rca/[id]/ReportView.tsx`) ve T37 yeri **bilerek boş bırakmış** |
| Uç | Yeni bir uç | Rapor `bundle_id` ile çekiliyor; LLM raporu `GET /v1/rca/{id}` yanıtına **nullable bir alan** olarak takılıyor |

T37 kod içine şunu yazmıştı: *"F4'ün yorumu buraya gelecek: özetin altında,
bulguların üstünde ve kanıtın YANINDA — yerine değil."* T51 o yeri doldurdu.
(b) seçilseydi `Pending`'e gereksiz bir satır yazılırdı ve liste boşalmadan F2
bitmiş sayılmadığı için o satır bir borç olarak dururdu.

---

## 2 · Üç hâl — F3'ün dört durumunun F4 karşılığı

Bu ticket'ın taşıyıcı ayrımı, ve sözleşmenin en değerli parçası.

| Hâl | Tel | Anlamı |
| --- | --- | --- |
| **A** | `reasoning: null` | Model **hiç koşmadı** |
| **B** | `findings: []`, `produced: 0` | Koştu, **hiç cümle üretmedi** |
| **C** | `findings: []`, `produced: 5`, `dropped: 5` | Koştu, **hepsi atıldı** |

**B ile C'nin bulgu listesi aynı** — ikisi de boş. Ayrımı taşıyan şey liste
değil **sayılar**, ve ekran boş liste metnini sayıya bakarak seçiyor.

**C en pahalısı.** Boş bir bulgu listesi *"bulgu yok"* diye okunursa, modelin
beş cümle uydurup hepsinin elendiği gerçeği kaybolur — ve F4'ün ölçmek istediği
tam olarak o. Aynı sınıf F3'te dört kez ödendi (`empty` · `never_fed` ·
`unavailable` · `not_registered`), o yüzden bekçisi de aynı biçimde **iki
tarafta ayrı**: `RcaReportPersistenceTests` (tel) ve `rca-screen.test.tsx`
(ekran). Telde üç ayrı şekil gelmesi, ekranın onları üç ayrı şey olarak
**çizdiğini** göstermiyor.

A'nın bölümü de **sessizce kaybolmuyor**: model koşmadıysa ekran bunu bir
cümleyle söylüyor — RCA §6'nın *"kapalı sağlayıcılar görünüyor"* kararının
aynısı.

---

## 3 · `dropped_sentence_ratio` payda sıfırken `null`

Koordinatörün düzeltmesi, ve haklıydı: sözleşmenin ilk taslağında bu alan
`0` dönüyordu ve **tuzağı ben yazmıştım** — *"0 ise oran da 0 — ölçülmedi
değil"* — sonra içine girmiştim.

`0.0` bu alanda **"hiç cümle atılmadı"** demek, yani **mükemmel kalite**.
Ölçülemeyen bir oranın en iyi sonuçla aynı baytları üretmesi, §7'nin tarif
ettiği sınıfın kendisi.

Karşılığı somut: T47 `null`'ı paydadan düşebiliyor, `0.0`'ı düşemez çünkü o
geçerli bir ölçüm.

> **Yan not, ve ayrı bir bekçisi var:** ekran tarafında `Number(null)` **0**
> döndürüyor. Körlemesine dönüştüren bir bileşen, sunucunun `null` kararını
> tam olarak geri alırdı — sayıyı `Number()`'dan **önce** `null` diye
> sınamak bu yüzden bir üslup tercihi değil.

T47 bu kararın aynısını bağımsız olarak verdi (çelişen kanıt boyutu için).
İki koldan aynı sonuca varılması kararı sağlamlaştırdı.

---

## 4 · `findings[]` sıralıdır ve sırası anlamlıdır

Liste **modelin kendi sıralaması**; depolama, serileştirme ve tel boyunca
korunuyor, ekran onu yeniden sıralamıyor.

Sebebi T47'nin `accuracy@1` / `accuracy@3` ekseni: inceleyen *"doğru olan
kaçıncı bulgu"* diyecek ve o sayı **bu listenin sırasına** atıfta bulunuyor.
Metin eşleştirmesi bilerek reddedildi (bu depoda *"SQL doğru, kolon doğru,
**dizge** yanlış"* sınıfının dört örneği var), dolayısıyla bütün eksen sıranın
güvenilirliğine dayanıyor.

Sıra sessizce değişirse ölçü sessizce yanlış olur — ve yanlışlığı hiçbir şey
haber vermez, çünkü sayı yine makul bir sayı olur.

Bekçi `Bulgu_sirasi_depolama_boyunca_korunuyor`. Fixture'ın sırası **alfabetik
değil** ve bilerek: alfabetik bir liste, sıralayan bir hatayı kendi başına
gizlerdi.

---

## 5 · Depolama

| Karar | Gerekçe |
| --- | --- |
| Belge (`jsonb`) **+** sorgulanabilir üst veri kolonları | `evidence_bundles`'ın kalıbı. Rapor bütün olarak okunuyor; ilişkisel bir alt tablo her göçte geçmiş satırlara `NULL` kolonlar ekleyip eski raporları sessizce farklı bir şekle sokardı |
| Üst veri kolonları (`produced` · `dropped` · `fabricated` · `scenario_id`) | Kuralın **ölçülmüş istisnası**: T47 altın küme üzerinde oran hesaplarken her satır için JSON açmamalı |
| **Tek yazan taraf** + ayrışmayı tutan test | Kopyanın bedeli budur ve tek yerde ödeniyor |
| `bundle_id` **tekil değil** | RCA §3: aynı paket üzerinde farklı model/prompt koşturmak beklenen iş. Tekil yapmak o karşılaştırmayı tanım gereği imkânsız kılardı |
| **Statü kolonu yok** | RCA §4.2'nin çivilediği sınır; `RcaReportStatusGuardTests` bekliyor |
| `SchemaVersion` kolon **ve** belgede | "Okuyabiliyor muyum" sorusu belgeyi açmadan cevaplanmalı |

### Kapsam kapısı bir tip, bir alışkanlık değil

Okuma metotları `Guid` değil **`EvidenceBundle`** istiyor.

Sebebi K17: **raporun kendi `owner_group`'u yok**, kapsamını üretildiği paketten
devralıyor. `Guid` alsaydı çağıranın kapsam kontrolünü *hatırlaması* gerekirdi
ve unutulduğu gün hiçbir şey kırılmazdı — A grubunun paketinden üretilmiş bir
rapor B grubuna okunurdu ve ne hata ne sayaç ne belirti olurdu.

Paketi istemek o kontrolü **zaten yapılmış** kılıyor. T41/T42/T44'te üç kez
verilen kararın dördüncüsü.

### Göç — okundu, ve ne yaptığı yazılı

`20260905103042_AddRcaReports`:

- **Tek bir yeni tablo yaratıyor** (`bizigo.rca_reports`) ve iki indeks.
- **Hiçbir mevcut tabloya dokunmuyor.** `AddColumn` yok, `AlterColumn` yok,
  veri taşıma yok, varsayılanla geri doldurma yok.
- `Down` tabloyu düşürüyor.

Bu ayrım bu depodaki en pahalı **önlenmiş** hatanın tam karşıtı: o göç *mevcut*
bir tabloyu değiştiriyordu (`enabled` → `status`) ve her pasif kuralı sessizce
açıyordu. Yeni ve boş bir tabloda geri doldurma semantiği **yok**, dolayısıyla o
sınıf burada doğamıyor.

**Göç uygulanmadı** — koordinatörün faz sonu koşumuna kalıyor
(`AddGoldenReviews` ve `AddActualRootCauseToGoldenReview` ile birlikte).

**Yabancı anahtar yazılmadı** ve bu bir karar: `evidence_bundles`'a `FK`
koymak, paket silindiğinde raporun ne olacağını (cascade mi, restrict mi)
kararlaştırmayı gerektirirdi ve o kararın veri sonuçlarını ölçmedim. Bedeli
sınırlı: rapor **paket üzerinden** okunuyor, yani sahipsiz bir rapor yanlış
cevap üretmiyor, yalnızca okunmayan bir satır olarak kalıyor. Yanlış cevap
değil, artık.

---

## 6 · Ölçülen kırmızılar

T44'ün dördüne üç yeni ölçüm eklendi; araç `tools/t44-kirmizi-olcumu.py` ve
artık iki koşucu tanıyor (`dotnet` ve `vitest`).

| # | Kusur | Hedef | Kontrol |
| --- | --- | --- | --- |
| E | Ölçülemeyen oran `0` yazılıyor | `Payda_sifirken_oran_null_sifir_degil` | `Bildirilmemis_belirtec_telde_de_null` |
| F | "Son rapor" en eskiyi döndürüyor | `Ayni_paketin_iki_raporu_da_saklaniyor` | `Bulgu_sirasi_depolama_boyunca_korunuyor` |
| G | Her cümlesi atılan rapor "bulgu yok" diye çiziliyor | `her cümlesi atılan rapor…` (vitest) | `model hiç koşmadıysa…` (vitest) |
| H | Muafiyetin **yokluğu** yazılmıyor | `K6 muafiyeti ve yokluğu…` (vitest) | `gerekçesiz muafiyet…` (vitest) |

Her ölçümde kusur, koşumdan **önce dosyadan okunarak** doğrulandı, ve her
ölçümün bir **kontrol testi** var — kusurun dar olduğunu, yani kapının *neyi*
tuttuğunu gösteren.

Yordamın son adımı T44'ün turunda eklendi ve burada da uygulandı: **geri
aldıktan sonra tam paketi bir kez daha koştur.**

---

## 7 · Yol boyunca çıkan iki sessiz tuzak

**1 · Kayıt tipinin `==`'i koleksiyonlarda referans eşitliği yapıyor.**
`Assert.Equal(belge, geriOkunanBelge)` her zaman düşüyordu — bütün alanlar
eşitken. Tehlikeli olan tersi: yalnızca skaler alanları karşılaştıran bir test,
listelerin bozuk döndüğü bir turda **yeşil kalırdı**. Karşılaştırma
serileştirilmiş hâl üzerinden yapılıyor; iç içe listeler dahil her alanı
kapsıyor.

**2 · `Number(null) === 0`.** Ekranda üretilen tipler sayıları
`number | string` veriyor ve körlemesine `Number(...)` uygulamak, sunucunun
`null` kararını tam olarak geri alırdı: ölçülemeyen bir oran **%0** — yani
mükemmel kalite — diye çizilirdi. Oran `Number()`'dan **önce** `null` diye
sınanıyor.

İkisi de aynı şekilde: **bir dönüşümün sessizce anlam değiştirmesi.**

---

## 7.1 · Model bölümü muafiyeti de söylüyor (T54'ün bulgusu)

`ModelEndpoint.BoundaryOverrideReason` T42'den beri **vardı ve doğru
duruyordu** — `AuditFields()` onu veriyordu — ama **üretimde tüketicisi yoktu**.
Bağlanacağı nokta T44+T51 ile doğdu.

Aciliyeti kuran cümle:

> Bugün raporda model hakkında hiçbir şey yok ve okuyan bunu **biliyor**.
> Bundan sonra model hakkında bir bölüm olacak ve okuyan onu **tam sanacak**.

İki alan, `AuditFields()`'takiyle **aynı adlar**: `BoundaryOverridden` (bool) ve
`BoundaryOverrideReason` (nullable).

**İkisi birden gerekiyor.** Yalnız gerekçe taşınsaydı boş bir dizge
*"muafiyet yok"* ile *"var ama gerekçe yazılmamış"*ı ayıramazdı. Üç hâl ayrımı
zaten `reasoning == null` ile çözülü; dördüncü bir alan gerekmiyor.

**`false` hâli de yazılıyor** — ekranda ve Markdown'da. Yalnız `true` iken
görünen bir rozet, muafiyetsiz koşumu *"bu soru sorulmamış"* hâline sokardı; bu,
deponun *"bakılmadı" ile "bakıldı, temiz" ayrı cümleler* kuralının aynısı.

### Aynı satırda düzeltilen bir yer tutucu

Çağrı noktası şunu yazıyordu:

```csharp
Provider: "model",
Model: scenario.Metadata.Id,
```

Yani rapor *"hangi modelle üretildi"* sorusuna **senaryo adıyla** cevap
veriyordu. F4'ün *"aynı kanıt, farklı model"* karşılaştırması iki koşumu ayırt
edemezdi — ve ayırt edememesi hiçbir yerde görünmezdi, çünkü alan doluydu.
Artık uçtan okunuyor.

**T54'ün aciliyet gerekçesini zayıflatan olgu bu ticket'tan geliyor:**
`SaveAsync`'i bugün hiçbir üretim kodu çağırmıyor, yani *"arada üretilen
raporlar kör kalır"* riski **sıfır**. Kalem yine de yapıldı çünkü satırlar
burada.

---

## 8 · Yapılmayanlar

- **Toplu/çapraz paket sorgusu yazılmadı.** T47 altın küme üzerinde oran
  hesaplayacak; o sorgunun kapsam semantiği bir `join` istiyor (raporun kendi
  `owner_group`'u yok) ve gruplama ekseni T47'nin kararı. **Tüketicisi olmayan
  bir tip tahmindir** — şekli T47 söylediğinde yazılacak. Bugün var olan:
  `LatestForAsync` ve `AllForAsync`, ikisi de paket kapsamlı.
- **Göç uygulanmadı** (koordinatörün faz sonu koşumu).
- **Yabancı anahtar yazılmadı** — §5, gerekçesiyle.
- **`ContradictingEvidenceVerdict.Unspecified` eklenmedi** — T47'nin kalemi.
  Ekran tarafında `unspecified` geldiğinde *"bölüm yoktu"* diye çizilmemesi
  gerekiyor; bugünkü ekran o değeri **hiç tanımıyor** ve bu bir borç olarak §9'da
  duruyor.
- **Rapor üreten yol kuyruğa bağlanmadı.** `RcaReportStore.SaveAsync`'i bugün
  **hiçbir üretim kodu çağırmıyor**: koşum kolu (T46/T47) motoru çalıştırıp
  sonucu yazacak. Yani tablo var, yol var, **yazan yok**.
- **Entegrasyon testi yazılmadı.** Depo testleri `InMemory` sağlayıcıyla
  koşuyor; `jsonb` kolon tipi ve indeks davranışı orada **sınanmıyor**.
  Konteyner gerektiren bir test yazmadım.

---

## 9 · Tereddütler

1. **`InMemory` sağlayıcı `jsonb`'yi sınamıyor.** Depo testleri geçiyor ama
   Postgres'te `jsonb` kolonuna yazılan dizgenin geçerli JSON olması gerekiyor
   ve bunu ancak gerçek bir veritabanı reddeder. Serileştirici her zaman geçerli
   JSON üretiyor, yani risk düşük — ama **ölçülmedi**, ve bu ikisi farklı
   şeyler.
2. **`AddBizigoScenarioPlugins` hâlâ `Program.cs`'te çağrılmıyor** (T44'te
   bildirildi). `RcaReportStore` `AddBizigoRcaTriggers` içine kaydedildi ve
   sebebi tam olarak bu: ayrı bir uzantı yazmak, çağrılmayı unutulabilecek
   ikinci bir satır üretirdi.
3. **`unspecified` ekranda tanınmıyor** (§8). T47 enum'a değeri eklediğinde
   ekranın onu *"bölüm yoktu"* diye çizmemesi gerekiyor — *"bölüm yoktu"* ile
   *"kimse söylemedi"* farklı şeyler, A/B/C ayrımının aynı disiplini. Kalem
   T47'de ama **ekran benim dokunduğum dosyada**, o yüzden burada yazılı.

---

## 10 · İlk bakılacak yer

> **İlk bakılacak yer:** `RcaReasoningResponse.Of` — belge ile tel arasındaki
> tek çeviri noktası. Ekranda beklenmedik bir sayı ya da kaybolan bir alan
> neredeyse hep burada başlıyor; `null` taşıyan iki alan (`dropped_sentence_ratio`,
> `prompt_tokens`) da buradan geçiyor.
>
> **Aradım ve elemedim:** göçün mevcut tablolara dokunup dokunmadığı (okundu,
> saf ek) · üretilen OpenAPI tiplerinin depodakiyle aynı olduğu (`api:check`
> birebir) · belge/üst veri ayrışması (gerçek yazma yolundan ölçüldü) ·
> serileştirmenin `null`'ları gizlemediği.
>
> **Aramadım:** `jsonb` kolonunun gerçek Postgres'te davranışı · indekslerin
> T47'nin sorgusuna gerçekten yeteceği (sorgu henüz yazılmadı) · raporu
> **yazacak** koşum yolunun nereye bağlanacağı — `SaveAsync`'in bugün çağıranı
> yok ve ben bağlamadım.
