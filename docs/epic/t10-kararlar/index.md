---
kind: spec
title: "T10 — API uçlarında alınan kararlar"
---

# T10 — API uçlarında alınan kararlar

> Bu belge geriye dönük yazıldı: kaynağı kod, commit geçmişi ve F1 kapanışı.
> Ticket koşulurken tutulmuş bir karar günlüğü **değil**. Burada yazan gerekçeler
> kodun bugünkü hâlinden çıkarıldı; o an tartışılıp reddedilen alternatifler
> kayıtta yok.

Ticket: [T10 — API uçları](../tickets/api-uclari/index.md) ·
Yöneten kararlar: [K17, K29, K30](../mimari-kararlar/index.md) · risk #6

## 1 · Ticket ne yaptı

F1'in dışarıya açılan yüzü: olay arama ve tekil okuma, ham bayta iniş, envanter,
değişiklik yazma/okuma, boru hattı sağlığı, parser okuma ve deneme, replay
başlatma. Hepsinin ortak koşulu tek kapı — `IScopedQuery`.

## 2 · Koddan okunan kararlar

### 2.1 Tek kapı, ve kapının kendisinin bekçileri

`IScopedQuery`'nin sınıf yorumu gerekçeyi yazıyor: REST uçları, CLI, replay
okuma, F3'ün kanıt toplayıcısı ve F4'ün MCP sunucusu **hepsi buradan geçiyor**.
ClickHouse row policy bilinçli olarak tercih edilmemiş — tek kapı olması gereken
yer sorgu API'si, çünkü agent'lar ve MCP de aynı API'yi kullanacak.

Kapının delinmemesi üç ayrı bekçiye bağlanmış:

| Bekçi | Ne tutuyor |
| --- | --- |
| `ArchitectureTests` — `ClickHouse.Driver` yasağı | `Bizigo.Storage.ClickHouse` dışında hiçbir derleme sürücüye referans veremiyor |
| `ApiSurfaceTests.Api_ham_nesne_deposuna_dogrudan_erisemez` / `..._olay_yazicisina_...` | API katmanının kapıyı atlayan kısayolları |
| `ApiSurfaceTests.IScopedQuery_her_metodunda_kapsam_istiyor` | Kapsamsız bir metot imzasının arayüze eklenmesi |

Dosyanın kendi özeti niyeti açıkça söylüyor: "Bu testlerin hiçbiri 'kod
çalışıyor mu' diye sormuyor; hepsi **bir yolun kapalı kaldığını** sınıyor."

Sonradan bir dördüncüsü eklenmiş: `Alarm_motoru_somut_okuyuculara_erisemez`
(T21). Yorumu neden daha çok gerektiğini yazıyor — alarm değerlendirmesi arka
planda, kimliksiz ve kimsenin bakmadığı saatlerde koşuyor; kapıyı delen bir
kural **hiçbir belirti üretmezdi**.

### 2.2 Serbest SQL yok — ve bu bir testle kapalı

`Serbest_SQL_kabul_eden_bir_filtre_operatoru_yok`. Ticket gerekçeyi yazıyor:
K17'nin tek zorlama noktası sorgu API'si; serbest SQL açılırsa kapsam ayrımı
arka kapıdan delinir. Analitik ihtiyaç için F2'de "kayıtlı sorgu" mekanizması
düşünülebileceği not edilmiş — kayıtta bir karar olarak değil, bir seçenek
olarak duruyor.

### 2.3 Yazma da kapıdan geçiyor, ve envanter sonradan kapıya taşındı

`WriteChangeAsync`'in özet yorumu: çağıran yalnızca **kendi kapsamındaki** bir
gruba yazabilir; aksi hâlde bir ekip başka bir ekibin zaman çizelgesine olay
düşürebilir ve RCA yanlış kanıtla çalışırdı.

Ticket ayrıca bir **düzeltmeyi** kaydetmiş: envanter listesi ilk yazımda kapsam
filtresini uç katmanında elle uyguluyormuş — yani zorlama iki ayrı yerdeymiş, ki
K17'nin kaçındığı durum tam olarak bu. `SearchSourcesAsync` eklenip filtre tek
kapıya taşınmış. Gerekçe: bir ekip başka bir ekibin **cihaz listesini** de
görmemeli.

Buna karşılık **katalog uçlarına kapsam filtresi uygulanmıyor** ve gerekçesi
yazılı: katalog veri değil yapılandırma, hangi parser'ların var olduğunu görmek
kimsenin logunu görmek değil.

### 2.4 Kapsam dışı = 404, ve bunun üç ayrı yerdeki karşılığı

`EventsEndpoints` içindeki yorum: "Kapsam dışı olay da 404: 403 dönmek 'böyle
bir olay var ama göremezsin' bilgisini sızdırırdı."

Aynı ilke iki yerde daha tekrarlanıyor ve ikisinde de **farklı bir biçim**
alıyor:

- `GetEventViewAsync` kapsam dışı olayda **boş liste** dönüyor — yorumu "404'ün
bilgi gizleme gerekçesi burada da geçerli" diyor.
- `CountOutOfScopeEventsAsync` / `CountOutOfScopeChangesAsync` ise tam tersini
yapıyor: **sayıyor ama içeriği vermiyor**. Yorumu gerekçeyi yazıyor —
sayamadığını sıfır sanmak bu projedeki en pahalı hata sınıfı; kök neden başka
grubun cihazındaki bir değişiklikse rapor bunu bilmeden yanlış sonuca varırdı.

İki davranış çelişmiyor: tekil okuma **varlığı** gizliyor, sayaç **sayıyı**
veriyor ama içeriği vermiyor.

### 2.5 Keyset sayfalama

`EventReader.AddKeyset` `(ts, event_id)` demeti üzerinde çalışıyor ve yorumu tek
cümle: offset derin sayfalarda çöker. Ölçülen hâli §4.1'de.

### 2.6 Hız sınırı — kullanıcı başına

`Program.cs`'te `PartitionedRateLimiter`, bölüm anahtarı `sub` claim'i (yoksa IP,
yoksa `anonymous`), `PermitLimit=4`, `QueueLimit=8`, ret kodu **429**.

Gerekçe yorumda: sınır **kullanıcı başına**, çünkü küresel bir sınır kalabalık
bir ekibi tek kişilik bir ekiple aynı kefeye koyardı. Risk #6'ya (gürültülü
komşu) atıf yapılmış. Sayıların (4 ve 8) neden bu olduğunun gerekçesi kayıtta
yok.

**Ticket'ın "Uygulama sonucu" tablosunda hız sınırı geçmiyor** ama kod
uyguluyor. Hangi aşamada geldiği bu belgede izlenmedi.

### 2.7 Parser ucu okuma-yalnız — ticket'tan bilinçli sapma

Ticket `POST /v1/parsers` istiyordu; uçtan parser **yayınlamak** yapılmamış.
Gerekçe ticket'ın kendi kaydında: katalog bu fazda repodan geliyor ve sıcak
yeniden yükleme atomik; yayın ucu, taslak→inceleme→yayın akışı olmadan kataloğu
**tek bir isteğin bozabileceği** bir yere çevirirdi. O akış F2'ye (T19/T20)
bırakılmış.

Yerine gelen `POST /v1/parsers/try` iki kararı daha taşıyor:

1. **`author` rolü istiyor, `reader` değil** — keyfi bir satırı motora
koşturmak veri okumak değil ama bedeli sınırsız bir hesaplama.
2. **Hangi kademenin karar verdiğini de dönüyor** — envanter bağı yerine literal
filtreye düşen bir satır, parser doğru olsa bile **envanterin eksik olduğunu**
söylüyor.

### 2.8 CSV yüklemesi ya hep ya hiç — ve saf kısmı ayrı

`SourceCsvImport` ayrıştırma/doğrulama/kapsam kontrolünü veritabanından ayırıyor.
Gerekçe sınıf yorumunda ve ölçüme dayanıyor: "ya hep ya hiç" kuralı ve kapsam
reddi kapsam hatasına dönüşebilecek davranışlar, dolayısıyla **konteyner
gerektirmeden** sınanabilmeli.

Ticket gerekçenin ürün tarafını yazıyor: yarı yüklenmiş envanter hangi cihazın
hangi gruba düştüğünü belirsiz bırakır ve o belirsizlik doğrudan kapsam hatasına
döner.

### 2.9 Depolama tipi tel sözleşmesi değil

`PipelineHealthResponses` anonim nesne yerine adlandırılmış record'lar
kullanıyor; yorumu gerekçeyi yazıyor — tipin `unknown` kalması, ekranın blok
şekillerini **elle yazması** demekti. Aynı dosya göstergelerin ortak yanını da
kaydediyor: hiçbiri arıza anında alarm üretmiyor, hepsi **sessiz çürüme**
sınıfından.

## 3 · Bugün ayakta duran bekçiler

| Bekçi | Ne tutuyor |
| --- | --- |
| `ApiSurfaceTests` (5) | Kapının etrafından dolaşan her yol: nesne deposu, `EventWriter`, serbest SQL operatörü, alarm motorunun somut okuyucuları, kapsamsız arayüz metodu |
| `ScopeNegativeTests` (12, entegrasyon) | "Başka grubun verisi" senaryoları — arama, tekil okuma, envanter, değişiklik yazma, kapsam daraltmasının genişletememesi |
| `ArchitectureTests` sürücü yasağı | Ham ClickHouse erişiminin tek derlemede kalması |
| `ProducesContractTests` (F2'de geldi) | Her ürün ucunun bir yanıt tipi bildirmesi; `Pending` boş, `Exempt` altıda sabit |
| CI `ui` işi — `api:check` | Üretilen OpenAPI şeması ve TypeScript tipleri depodakiyle birebir değilse CI düşüyor |

**Bu bekçilerin kırmızı yanabildiğini bu turda ölçmedim.** Belge geriye dönük;
kod okundu, ölçüm yapılmadı.

## 4 · Açıkta kalanlar

Ticket üç şeyi "doğrulanmadı" diye bırakmış. İkisi sonradan **ölçüldü** ve
ikisinin de cevabı ticket'ın varsaydığından daha ayrıntılı çıktı.

### 4.1 "Keyset 1M satırda sabit süre" — koşullu doğru

[Mimari kararlar](../mimari-kararlar/index.md) ve
[F1 kapanışı](../f1-kapanis/index.md) aynı ölçümü taşıyor. İddia **sıralama
anahtarının tam öneki verildiğinde** doğru, genel olarak değil: tablo
`ORDER BY (owner_group, source_id, ts)`, sorgu ise `ORDER BY ts DESC, event_id DESC`.

| Sorgu şekli | Sayfa 1 | Derin sayfa |
| --- | --- | --- |
| Filtresiz | 40,7 ms / 377k | 38,8 ms / **1M satır** |
| `owner_group` | 45,9 ms / 155k | 57,1 ms / 286k |
| `owner_group` + `source_id` | 17,8 ms / 57k | **13,7 ms / 57k** |

Kapsam kapısı `owner_group`'u her sorguya eklediği için kısmi fayda garanti; tam
sabitlik kaynak filtresi istiyor. Offset'e üstünlük her hâlükârda net (38,8 ms
vs 148,6 ms).

### 4.2 "TR/AR/CJK tam metin" — çalışıyor, ama bir uzunluk eşiğiyle

Aynı ölçüm: eşleşmeyen sorgu **0 satır** okuyor (indeks sağlam), ama seçicilik
~10-11 karakterden sonra başlıyor — `kullanıcı` (9 karakter) granül atlamıyor,
`用户登录失败，请检查凭据` (12 karakter) atlıyor. Eşik **alfabeden bağımsız**, yani
K4'ün "dile özel tokenizasyon olmadan çözüyor" iddiası doğru; kırılan şey kısa
sorgular — bir log arama kutusuna yazılan şey tam olarak odur.

Bedeli de ölçülmüş: `idx_body` 13,3 MiB, tablo 29,4 MiB — **indeks tablonun
%45'i**.

### 4.3 "OpenAPI şeması geçerli mi" — dolaylı olarak kapandı

Ticket "üretiliyor ama geçerliliği bir araçla doğrulanmadı" diyor. Bugün F2'nin
`api:generate`/`api:check` zinciri şemayı **tüketiyor**: TypeScript tipleri
ondan üretiliyor ve CI birebir eşitliği kapı yapıyor. Bu, şemanın bir araçla
işlendiğini gösteriyor; **bir OpenAPI doğrulayıcısıyla sınandığını değil.**
İkisi aynı şey değil ve ticket'ın sorduğu ikincisiydi.

### 4.4 Küçük sürüklenme: altı gösterge, yedi blok

Ticket ve F1 kapanışı `GET /v1/health/pipeline` için "altı gösterge" diyor.
Bugün yanıt **yedi** blok taşıyor — yedincisi `inventory` ve T17 ile geldi
(`40a329e`). `PipelineHealthResponse`'un özet yorumu hâlâ "altı bloğun şekli"
diyor. Zararsız ama sürüklenmiş bir cümle.

### 4.5 Gerekçesi kayıtta olmayanlar

| Kalem | Not |
| --- | --- |
| Hız sınırı sayıları (`PermitLimit=4`, `QueueLimit=8`) | Kullanıcı başına olmasının gerekçesi yazılı; sayıların değil |
| Hız sınırının hangi aşamada geldiği | Ticket'ın sonuç tablosunda yok, kodda var |
| "Kayıtlı sorgu" mekanizması | Bir seçenek olarak not edilmiş, karar verilmemiş |
