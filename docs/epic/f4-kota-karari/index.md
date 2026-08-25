---
kind: spec
title: "F4 açık soru 2 — kota, kısıtlar ve aşıldığında ne oluyor"
---

# F4 · Kota kararı

[RCA özelliği §5](../rca-raporu-ozelligi/index.md)'in son paragrafı dört kısıt
sayıyor ve hepsini tek cümlede topluyor:

> eşzamanlılık limiti, koşu başına süre tavanı, token bütçesi ve grup başına
> günlük RCA kotası

Bu belge o dördünü **risklere karşı** açıyor, boş kalan hücreleri bulgu olarak
işaretliyor, ve [tetikleyici kararı](../f4-tetikleyici-karari/index.md)'nın
açtığı kesişmeyi kapatıyor.

**Sayı önerilmiyor** — her kısıt için nasıl ölçüleceği yazılı. Gerekçesi §8.1'in
dersi: işaretsiz bir sayı, okuyan için ölçülmüş bir sayıdan ayırt edilemez.

---

## 1 · Kısıt × risk matrisi

Dördü tek sepette durunca *"kota var"* cümlesi bütün riskleri kapatıyormuş gibi
okunuyor. Açınca öyle olmadığı görünüyor.

| | **Maliyet** (token/para) | **Gürültülü komşu** (bir grup kuyruğu kaplar) | **Takılı koşu** (bir koşum bitmiyor) | **Döngü** (RCA → RCA) |
| --- | --- | --- | --- | --- |
| **Eşzamanlılık limiti** | ✗ | ✅ **birincil** | ~ kısmen: yeri doldurur ama koşum sürer | ✗ |
| **Koşu süre tavanı** | ~ dolaylı | ~ dolaylı | ✅ **birincil** | ✗ |
| **Token bütçesi** | ✅ **birincil** (koşum başına) | ✗ | ~ dolaylı | ✗ |
| **Günlük kota** (grup başına) | ✅ **birincil** (toplam) | ~ kısmen: yavaş | ✗ | ~ kısmen: kotayı tüketerek durur |
| **Soyağacı + debounce** (§5) | ✗ | ~ kısmen | ✗ | ✅ **birincil** |

### Boş hücrelerin söyledikleri — üçü bulgu

**Bulgu 1 · Token bütçesi gürültülü komşuyu hiç tutmuyor.** Bir grup **ucuz**
ama **çok** RCA koşturarak kuyruğu kaplayabilir; her koşum bütçesinin altında
kalır ve hiçbir kapı kırmızı yanmaz. Gürültülü komşunun tek gerçek kapısı
**eşzamanlılık**.

**Bulgu 2 · Günlük kota takılı koşuyu hiç tutmuyor.** Kota **başlatılan** koşumu
sayıyor; başlamış ve bitmeyen bir koşum kotayı bir kez tüketip kuyruk yuvasını
**süresiz** tutuyor. Onun kapısı **süre tavanı**, ve o tavan olmadan kota
sayısı doğru olsa bile sistem tıkanır.

**Bulgu 3 · Kısıtların hiçbiri tek başına döngüyü tutmuyor.** Günlük kota
döngüyü **durdurur** ama fatura ödendikten sonra: grup, gerçek bir sorun
çıktığında kotasını çoktan tüketmiş olur. Döngünün kapısı §5'in **soyağacı**,
ve kota o kapının yerine geçemez — yalnızca kapı yoksa hasarı sınırlar.

**Dördünün de birincil olduğu bir risk var ve dördü farklı.** Yani §5'in
listesi eksik değil, ama *"kuyrukta uygulanır"* cümlesi dördünü aynı şey gibi
gösteriyor. **Bir kısıtı düşürmek, kapattığı riski açıkta bırakır** ve hangi
riski açtığı bu tablodan okunuyor.

---

## 2 · Reddedilen koşum kotadan düşülüyor mu — **karar: hayır, ama sayılıyor**

[Tetikleyici kararı](../f4-tetikleyici-karari/index.md) bu soruyu açtı ve iki
cevabın zıt sonuç verdiğini yazdı. Üçüncü bir yol da vardı (ayrı bütçe).
Karar ve gerekçesi:

> **Reddedilen koşum kotadan düşülmüyor. Ama ayrı bir sayaçla sayılıyor ve
> görünür oluyor.**

### Gerekçe: kota bir **maliyet** kapısı, reddedilen koşumun maliyeti yok

Kota matriste **maliyet** sütununun birincil kapısı. Reddedilen bir koşum —
`depth ≥ 2` ya da ata tekrarı — girişte, **kanıt toplanmadan ve LLM
çağrılmadan** duruyor. Yani maliyeti sıfıra yakın.

Bir maliyet kapısını **yapılmamış işle** doldurmak, kapının ölçtüğü şeyi
bozar: kota o zaman *"ne kadar harcadın"* değil *"kaç kez denedin"* ölçmeye
başlar, ve ikisi aynı sayı değil.

**(a) düşülsün** seçeneğinin somut zararı: bir döngü, kotayı **hiç rapor
üretmeden** tüketir. Sonra o gruba gerçek bir alarm geldiğinde RCA üretilmez —
ve kullanıcı için görünen şey *"kotam doldu"*, sebebi ise kendisinin hiç
yararlanmadığı bir döngü. **Kurbanı cezalandıran** bir kapı.

**(c) ayrı bütçe** seçeneğini de almadım: ayrı bir bütçe, ikinci bir eşik,
ikinci bir yapılandırma ve ikinci bir *"doldu"* hâli demek. Reddedilen koşumun
tükettiği şey **para değil kuyruk**, ve kuyruğu koruyan kapı zaten var
(eşzamanlılık). Var olan bir kapının işini ikinci bir bütçeye devretmek, aynı
riski iki yerden yönetmek olur.

### Ama bırakılan boşluk **gerçek** ve adı konuyor

(b) tek başına şunu bırakıyor: sonsuz reddedilen koşum üreten bir döngü,
kotayı hiç zorlamadan **kuyruğu** meşgul edebilir.

Bu bir **kota** boşluğu değil, matristeki **gürültülü komşu** hücresi — ve
kapısı eşzamanlılık. Yani boşluk yanlış kapıyla kapatılmamalı; doğru kapı
zaten listede.

**Şart:** reddedilen koşumlar **sayılıyor ve grup bazında görünür**. Sayılmazsa
bir döngü sessizce sürer: kota dolmaz, rapor üretilmez, ve kimse bir şeyin
döndüğünü fark etmez. Sayaç kotadan ayrı çünkü **farklı soruyu** cevaplıyor —
kota *"ne kadar harcandı"*, bu sayaç *"kaç kez reddedildi ve neden"*.

Ret sebebi ayrı tutuluyor (§5'te de öyle): `depth` sınırı ile ata tekrarı
farklı şeyler söylüyor.

---

## 3 · Nerede uygulanıyor — girişte ret ile koşum sırasında iptal ayrı

Dördü *"kuyrukta uygulanır"* diye tek cümlede geçiyor ama **iki farklı an** var
ve karıştırmak sessiz bir hataya açık.

| Kısıt | Ne zaman | Sonucu |
| --- | --- | --- |
| Günlük kota | **Girişte** | Koşum hiç başlamıyor; kullanıcıya *"kota"* denir |
| Soyağacı / debounce | **Girişte** | Koşum hiç başlamıyor; sebebi ayrı |
| Eşzamanlılık | **Girişte bekletme** (ret değil) | Koşum **kuyrukta**, sırası gelince koşacak |
| Süre tavanı | **Koşum sırasında iptal** | Koşum başladı, yarıda kesildi — **maliyet kısmen ödendi** |
| Token bütçesi | **Koşum sırasında** | Aynı: kısmen ödendi |

**Bu ayrım kotanın muhasebesini belirliyor.** Girişte reddedilen koşum
maliyetsiz; **yarıda kesilen koşum değil.** Süre tavanına ya da token
bütçesine takılan bir koşum kanıt toplamış, belki LLM çağırmıştır — o
**kotadan düşülmeli**, çünkü harcama gerçekten oldu.

Yani §2'nin kararı *"reddedilen koşum düşülmez"* yalnızca **girişte** reddedilen
için geçerli. Yarıda kesilen koşum reddedilmiş değil, **başarısız olmuş** bir
koşum — ve ikisini aynı kefeye koymak, kotayı gerçek harcamadan koparır.

⚠️ **Eşzamanlılık bir ret değil bir bekletme** ve bu ayrım ekrana da yansımalı:
*"sıranı bekliyorsun"* ile *"kotan doldu"* kullanıcı için tamamen farklı iki
cümle. Tek bir *"şu an çalıştırılamıyor"* mesajı ikisini birleştirir ve
kullanıcı bekleyeceği yerde kotasını sorgular.

---

## 4 · Kota aşıldığında ne oluyor — **boş rapordan ayırt edilebilmeli**

Bu bölümün tek işi bir sessiz yanlış davranışı önlemek:

> **Kota yüzünden RCA üretilmemiş bir alarm, RCA'sı boş çıkmış alarmdan ayırt
> edilebilmeli.**

İkisi de ekranda *"rapor yok"* olarak görünür ve anlamları zıt: biri *"bakıldı,
bir şey bulunamadı"*, diğeri *"hiç bakılmadı"*. Ayırt edilemezse operatör
ikinci hâli birinci sanır ve **soruşturmayı kapatır**.

### Emsal aynı depoda ve ölçülmüş

`AlertRunState` bu ayrımı zaten yapıyor ve gerekçesi kayıtlı: zaman aşımı
`Quiet` ile aynı kefeye konsaydı *"yavaş bir sorgu sessizce her şey yolunda'ya
dönüşürdü"*. Bugün `TimedOut` ayrı bir durum.

RCA tarafında aynı yapı gerekiyor: bir alarmın RCA durumu **kapalı bir küme**
olmalı ve *"üretilmedi"* tek bir değer olmamalı.

| Durum | Anlamı | Kullanıcıya |
| --- | --- | --- |
| `Produced` | Koştu, rapor var | Rapor |
| `Empty` | Koştu, **kanıt bulunamadı** | *"Bakıldı, bulunamadı"* + kanıt penceresi |
| `QuotaExceeded` | **Hiç koşmadı** — grup kotası dolu | *"Kota doldu"* + ne zaman sıfırlanacağı |
| `LoopRefused` | Hiç koşmadı — soyağacı reddi | Sebebi (derinlik / ata tekrarı) |
| `Cancelled` | **Başladı, yarıda kesildi** — süre ya da token | *"Yarıda kesildi"* + kısmi kanıt varsa o |
| `Failed` | Hata | Hata |

`Cancelled`'ın `Empty`'den ayrı olması özellikle önemli: yarıda kesilen bir
koşum **kanıt toplamış olabilir** ve o kanıt *"bulunamadı"* diye sunulursa
yanlış bir olumsuzluk üretir.

**Ve bu değer kayda giriyor, yalnızca ekrana değil.** Bir turdur bu depoda
ödenen ders: *"veri var, yüzey yok"*un bir adım ötesi *"veri kayda hiç
girmiyor"* — o zaman soru sonradan hiç cevaplanamaz.

---

## 5 · Sayılar — **önerilmiyor**, ölçümü tarif ediliyor

İşaretsiz bir sayı, okuyan için ölçülmüş bir sayıdan ayırt edilemez. Bu yüzden
her kısıt için **nasıl ölçüleceği** yazılı, değeri değil.

| Kısıt | Nasıl ölçülür | Bugün elde ne var |
| --- | --- | --- |
| **Eşzamanlılık** | Bir RCA koşumunun kaynak profili (CPU · ClickHouse sorgu sayısı · bellek) ölçülür, makine kapasitesine bölünür | **Emsal var:** `AlertingOptions.MaxConcurrentEvaluations = 4` (K16, T21'de karara bağlandı). RCA koşumu alarm değerlendirmesinden **ağır**; aynı sayı devralınmamalı |
| **Süre tavanı** | Altın küme üzerinde uçtan uca koşum süresi dağılımı; tavan **kuyruğun** dayanabileceği yerden seçilir, ortalamadan değil | **Emsal var:** `GatherBudget.Default = (400 kalem, 20 sn)` yalnızca **kanıt toplama** için. Uçtan uca süre bunu **içeriyor**, eşit değil |
| **Token bütçesi** | Altın küme koşumlarında koşum başına token dağılımı; kanıt paketi boyutuyla birlikte | **Yok.** LLM çağrısı henüz yazılmadı, dolayısıyla ölçülecek bir dağılım da yok |
| **Günlük kota** | Grup başına **alarm** hacminin dağılımı × debounce sonrası RCA'ya dönüşme oranı | **Yok.** Debounce yazılmadı; oran ölçülemez |

### Ölçülemeyenler ve **neden**

Son iki satır bugün **ölçülemez** ve sebebi eksiklik değil sıra: token bütçesi
LLM çağrısı yazılmadan, günlük kota debounce yazılmadan ölçülemez. İkisi de
**F4'ün kendi çıktısına** bağlı.

Bu bir döngü değil ama bir **sıra kısıtı**: F4'ün ilk yarısı (kuyruk, debounce,
tetikleyici bağlanması) yazılmadan son iki sayı seçilemez. Seçilirse **tahmin**
olur ve tahmin, ölçülmüş bir sayıdan ayırt edilemeyecek biçimde yazılır.

**Öneri:** ilk iki kısıt (eşzamanlılık, süre tavanı) ölçülüp konabilir; son
ikisi **açıkça geçici** olarak konur ve *"ölçülmedi"* işaretini taşır. İşaret
kaldırılana kadar o sayılara dayanan hiçbir kapasite iddiası yapılmamalı.

---

## Aramadıklarım ve ölçemediklerim

**Aradım, yok:** depoda kota ya da günlük sayaç türünden bir mekanizma. İki
yerde açıkça F4'e ertelenmiş (`EvidenceEndpoints.cs:11`,
`Program.cs:150`). Yani sıfırdan yazılacak.

**Aradım, var:** eşzamanlılık ve zaman aşımı için iki emsal —
`AlertingOptions` (4 eşzamanlı, 20 sn) ve `GatherBudget` (400 kalem, 20 sn).
İkisi de **ölçülerek** değil kararla konmuş; devralınırsa aynı işaretle
devralınmalı.

**Ölçemedim:** dört kısıttan hiçbirinin sayısı. İlk ikisi ölçülebilir ama
**RCA koşumu henüz yok** — ölçülecek koşum olmadan dağılım çıkarılamaz. Son
ikisi F4'ün kendi çıktısına bağlı (yukarıda). Bu turda **hiçbir koşum
yapılmadı**; belge kod ve belge okumasına dayanıyor.

## Tereddüt ettiğim yer

§4'teki durum kümesini **kapalı** yapmayı öneriyorum ama `Cancelled` ile
`Failed` arasındaki sınır her zaman net olmayabilir: token bütçesi dolduğunda
bu bir iptal mi, bir başarısızlık mı? İkisini ayırmamın sebebi iptalin
**beklenen** bir sonuç olması — ama bunu ancak gerçek koşumlar görülünce
doğrulayabilirim.

Ayrıca: `QuotaExceeded` bir **tel sözleşmesi** değeri olacak (§8). Ekleneceği
an tüketicisi olmalı — bugün RCA durumu gösteren bir ekran yok, dolayısıyla bu
kümenin tamamı T37'nin rapor ekranıyla birlikte gelmeli. Ayrı gelirse kayda
yazılan ama kimsenin okumadığı bir alan olur.
