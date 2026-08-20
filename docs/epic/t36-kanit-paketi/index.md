---
kind: spec
title: "T36 — Kanıt paketi: sıralama kararı ve donan sözleşme"
---

# T36 — Kanıt paketi ve deterministik rapor

Bu belge iki şeyi kayda geçiriyor: **kanıt sıralamasının nasıl kurulduğu** (bir
yargı, ölçüm değil) ve **paketin sözleşmesinin nerede donduğu** (saklandığı
için geri alınamaz).

Kod `t36-kanit-paketi` dalında, `main`'den (`06d972c`) dallandı.

## 1 · Ağırlık normalleştirme — koordinatörün istediği karar

### Sorun

`EvidenceItem.Weight` sağlayıcı içinde anlamlı, sağlayıcılar arasında **değil**:

| Sağlayıcı | Ağırlık nedir | Tipik büyüklük |
| --- | --- | --- |
| `logs.first-seen` | kaynak sayısı | 1–20 |
| `logs.volume` | Poisson z-score | 3–40 |
| `logs.attribute-lift` | kat (lift) | 2–10 |
| `logs.propagation` | `1/(1+saniye)` | 0,001–1 |
| `change.feed` | zamansal yakınlık | 0–1 |

Bu beş sayıyı doğrudan karşılaştırmak sıralamayı **ölçek kazasına** bırakıyor:
z-score her zaman kazanır, yayılma hiçbir zaman üste çıkamaz. Ve yanlışlık
hiçbir yerde hata vermez — rapor üretilir, bulgular sıralanır, sıra sessizce
yanlış olur.

### Seçilen yol: sınıf + sınıf içi oran

`Score = ClassRank + (w / max_w_dilim_içi)`

Sınıf farkı **1,0 adım** olduğu için sınıf içi hiçbir büyüklük bir üst sınıfı
geçemiyor. Yani kararı veren **yargı**, ölçek değil: en güçlü hacim sapması
(z=40) bile tek kaynakta görülmüş bir ilk-görülen imzayı geçmiyor.

Sınıf içinde **oran** kullanıldı, sıra (rank) değil — sıra "z=20 ile z=3,1"
farkını siler, oysa o fark sağlayıcının söylemek istediği şeyin ta kendisi.

### Sınıf sırası ve gerekçeleri

Eksen tek: **kök nedene yakınlık**.

| # | Sağlayıcı | Gerekçe |
| --- | --- | --- |
| 6 | `change.feed` | Elimizdeki tek **niyet** kaydı. Diğerleri belirti ölçüyor; bu, birinin bilerek yaptığı bir şeyi gösteriyor ve aksiyona en yakın olan o (K21) |
| 5 | `logs.first-seen` | "Yeni bir şey oldu". RCA belgesi log korelasyonları arasında **tek en güçlü sinyal** diyor: tabanda hiç görülmemiş bir imza, tanımı gereği olayla eş zamanlı doğmuş |
| 4 | `logs.propagation` | İlk bozulan çoğu zaman kök nedene en yakın olan. **Nedensel yön** veriyor; diğerleri yalnızca birliktelik |
| 3 | `logs.silence` | Susan cihaz. Ağda kritik ama **ne bozulduğunu** söylüyor, **neden** bozulduğunu değil |
| 2 | `logs.volume` | Var olan hatanın patlaması. Güçlü ama çoğunlukla **belirti**: hacim artışının kendisi nadiren nedendir |
| 1 | `logs.attribute-lift` | "Hepsi aynı switch'in arkasında". Tek başına bulgu değil, diğer bulguları **gruplayan** içgörü |
| 0 | `logs.window` | Pencerenin ham bozuk satırları. **Bağlam**, bulgu değil |

**Bu bir yargı, ölçüm değil** — ve öyle olduğu yazılı. Doğrulanacağı yer altın
küme (RCA §7): gerçek kök nedenle eşleşen raporlar biriktiğinde "ilk bulguda
doğru oranı" bu sıranın iyi olup olmadığını ölçecek. O zamana kadar sıra
tartışmaya açık; değiştirmenin maliyeti tek bir tablo.

### İki koruma

**Skor pakete yazılmıyor.** Sıralama yargısı zamanla değişebilir, kanıt
değişmez. Skoru saklamak, altı ay sonra sıralamayı düzelttiğimizde geçmiş
paketleri eski yargıyla dondururdu — oysa paketin saklanma sebebi tam tersi.

**Tanınmayan sağlayıcı düşürülmüyor, en dibe konuyor** (`ClassRank = -1`) ve bir
bekçi testi kayıtlı her sağlayıcının tabloda olduğunu tutuyor. F5'te trace
sağlayıcısı geldiğinde onu yazmayı unutmak, kanıtını "en önemsiz" ilan etmek
olurdu ve hiçbir şey kırmızı yanmazdı.

## 2 · Paket saklanıyor — sözleşme donuyor

Bugün yazılan bir paket altı ay sonra, o günkü kodla okunacak. F4'ün "aynı kanıt
üzerinde farklı model koşturup karşılaştır" ihtiyacının tamamı buna dayanıyor.

| Karar | Seçim | Gerekçe |
| --- | --- | --- |
| Nerede | **Postgres** kontrol düzlemi | Koşu başına bir satır, tekil kimlikle okunuyor, RCA raporuyla ilişki kuracak (T37). ClickHouse'un güçlü olduğu hiçbir şeye ihtiyaç yok |
| Gövde | **Tek `jsonb` belge** + sorgulanabilir üst veri kolonları | Kanıt satırları bir anlık görüntü, sorgulanan çalışma kümesi değil. İlişkisel alt tablo, her göçte geçmiş satırlara `NULL` kolon ekleyip eski paketleri sessizce farklı bir şekle sokardı |
| Sürüm | `schema_version` **hem belgede hem kolonda** | "Bugünkü kod bunu okuyabiliyor mu" sorusu belgeyi açmadan cevaplanabilmeli |
| Okuma sınırı | `MinReadableSchemaVersion` **ayrı sabit** | "Ne yazıyoruz" ile "ne okuyabiliyoruz" tek sayıya bağlanırsa, sürümü artıran ilk kişi bütün geçmişi okunamaz yapar ve fark etmez |
| Okunamayan paket | **İstisna**, `null` değil | "Paket yok" ile "paket var ama okuyamıyoruz" farklı; ikincisini birincisi gibi göstermek F4'ün karşılaştırmasını sessizce eksik kümeye indirger |
| JSON adlandırma | `snake_case` | Depo kuralı §8. Saklanan bir belgede camelCase'e kayma geri dönülemez |

Bir bekçi testi, **kaynakta elle duran** bir v1 belgesini bugünkü kodla okuyor.
Fixture üretilmiş olsaydı test hiçbir şey kanıtlamazdı: kod ile fixture aynı
anda değişir ve soru hep evet cevaplanırdı.

## 3 · Determinizm — ve bulunan açık

**Kabul kriteri:** aynı girdiyle aynı paket.

İçerik hash'i duvar saati taşıyan her şeyi **dışarıda** bırakıyor: `id`,
`gathered_at`, dilimlerin `duration`'ı. Aynı ayrım `ReplayDiff`'te de var ve
aynı sebeple.

Dilimler **sağlayıcı kimliğine göre sıralanarak** hash'leniyor (sağlayıcılar
paralel koşuyor, kayıt sırası DI'nin insafında). Dilim **içindeki** satır sırası
korunuyor — sıra sinyalin kendisi: yayılma zaman sıralı, ilk-görülen hacim
sıralı.

### Bu, T35'te bir açık ortaya çıkardı

Dilim içi sıranın hash'e girmesi, sorguların **kararlı bir `ORDER BY` eşitlik
bozucusu** taşımasını şart koşuyor. Dört korelasyon sorgusundan üçü taşıyordu;
`GetAttributeLiftAsync` taşımıyordu:

```sql
ORDER BY window_count DESC          -- eşit sayılarda sıra sunucunun insafında
```

Eşit `window_count`'lu iki alan değeri koşumdan koşuma yer değiştirebilir, içerik
hash'i kayar ve **"aynı paket mi" sorusu sessizce hep hayır cevaplanır**.
Düzeltildi (`ORDER BY window_count DESC, value`), gerekçesi SQL'in içinde.

## 4 · Raporun dürüstlük satırları

Rapor hipotez üretmiyor — o F4'ün işi (K22). Ürettiği şey gözlemler,
kaynaklarıyla ve sırasıyla. Dört ayrım rapora yansıyor:

| Ayrım | Rapordaki yeri | Karıştırılsaydı |
| --- | --- | --- |
| **Bakıldı, bir şey yok** (`Empty`) | "Bakıldı, kanıt çıkmadı" | Sessizliğin kendisi bir bilgi; kaybolurdu |
| **Besleme yok** (`NeverFed`) | "Bakılmayanlar · besleme yok" | "Değişiklik olmadı" diye okunur, kök neden başka yerde aranır |
| **Sağlayıcı yok** (`NotRegistered`) | "Bakılmayanlar · sağlayıcı yok" | F5'in üç türü sessizce yokmuş gibi görünür |
| **Ölçülemedi** vs **sıfır** | "ölçülemedi — bilinmiyor, 'sorun yok' değil" | Ölçülmemiş bir şeye "sorun yok" denir |

Üç uyarı satırı (kapsam dışı sayım, zaman güvenilirliği, eksik kanıt) raporun
**en üstünde**: sonda duran bir kısıt okunmuyor, ve okunmayan bir kısıt hiç
yazılmamış gibi.

### Zaman dürüstlüğü sağlayıcıdan türetilmiyor

Yayılma sağlayıcısı yalnızca **bozulma sayılan** olayları görüyor. Ondan
türetilen bir sayı, yayılma hiçbir şey döndürmediğinde sessizce sıfır olurdu —
yani pencere baştan sona güvenilmez zamanlı olsa bile rapor "sorun yok" derdi.

Ölçüm pencerenin tamamı üzerinden ve **üçüncü bir sorgu yüzeyi yazılmadan**:
`time_source` zaten olay sorgusunun filtrelenebilir alanı, sayım
`CountEventsAsync`'ten geçiyor, yani kapsam kapısı (K17) burada da tek kapı.

## 5 · Ölçülen

| Ölçü | Değer |
| --- | --- |
| Birim testleri | **778** geçti, 3 atlandı (732'ydi) |
| Derleme | 18 proje, **0 uyarı** |
| Yeni birim testi | 46 |
| Entegrasyon testi | 4 — **yazıldı, koşturulmadı** |

### Bekçilerin kırmızı yandığı ölçüldü, sonra geri alındı

| Kırılan | Kırmızı yanan |
| --- | --- |
| Hash `gathered_at`'i içeriyor | `EvidenceBundleTests.Ayni_girdi_ayni_hash` |
| Sıralama ham ağırlığa indirildi | `EvidenceRankingTests` — 3 iddia |
| Rapor "Bakılmayanlar" bölümünü yazmıyor | `DeterministicReportTests` — 2 iddia, biri `NeverFed` |

### Entegrasyonda koşturulduğunda ne kanıtlayacak

`EvidenceBundleStorageTests` — birim testleri bellek içi EF sağlayıcısıyla
koşuyor ve orada `jsonb` diye bir şey yok; kolon `string` gibi davranıyor. Yani
"paket saklanıyor" iddiasının gerçekten sınandığı tek yer orası: göç uygulanıyor
mu, `jsonb` Türkçe/CJK gövdeleri bozmadan taşıyor mu, üst veri belge açılmadan
sorgulanabiliyor mu, ve aynı hash iki kez yazılabiliyor mu (F4'ün karşılaştırma
akışı bunu yapıyor; tekil kısıt olsaydı ikinci koşu düşerdi).

## 6 · Kapsam dışı bırakılanlar

- **Ekran ve export** — T37. Rapor `ToMarkdown()` üretiyor ve o metin ekranın
kaynağı; ekranın kendisi bu ticket'ta değil.
- **API ucu** — paketi dışarı veren uç T37'nin. Bugün `EvidenceBundleFactory` ve
`EvidenceBundleStore` DI'de kayıtlı ve kullanılmaya hazır, ama hiçbir uç onları
çağırmıyor. **Tüketicisi olmayan bir tip tahmindir** (§8): uç, onu gerçekten
tüketen ekranla birlikte gelmeli.
- **RCA raporu tablosu** (`rca_report`) — F4. Bu ticket kanıtı saklıyor, yorumu
değil.
