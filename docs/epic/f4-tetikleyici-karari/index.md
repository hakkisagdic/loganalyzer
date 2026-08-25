---
kind: spec
title: "F4 açık soru 1 — anomali tetikleyicisi hangi sinyalden doğuyor?"
---

# F4 · Anomali tetikleyicisi hangi sinyalden doğuyor?

[RCA özelliği §5](../rca-raporu-ozelligi/index.md) dört tetikleyici sayıyor.
Üçünün girişi belli; dördüncüsü — **anomali zinciri** — *"senaryo → senaryo"*
diyor ve zinciri başlatan **ilk** anomalinin nereden doğduğu hiçbir yerde
yazılı değil.

Bu belge o soruyu ölçüyor ve bir öneri veriyor. **Karar koordinatörde.**

---

## Özet: soru yanlış sorulmuş olabilir

Üç adayı ölçtükten sonra çıkan sonuç, üçünden birini seçmek değil:

> **Dördüncü tetikleyici bir KAYNAK değil, bir DEVAM kuralı.** Zincirin ilk
> halkası her zaman diğer üçünden biri; *"anomali zinciri"* satırı bir RCA
> koşumunun **başka bir RCA koşumu doğurmasını** tarif ediyor, yeni bir sinyal
> girişini değil.

Bu doğruysa tabloda dördüncü satırın yeri farklı: bir tetikleyici satırı değil,
diğer üçünün üstüne binen bir **çarpan** ve bir **sınır**.

Gerekçesi aşağıdaki üç ölçümde.

---

## Ölçüm 1 · F3'ün beş korelasyonu bugün **hiç tetiklenemiyor**

Aday 1, kuyruk yükü sorusunun cevabını en somut vereceği sanılan adaydı.
Cevap: **sıfır**, ve sebebi sessiz olmaları değil.

| Ne arandı | Bulunan |
| --- | --- |
| Korelasyonları **zamanlanmış** olarak değerlendiren bir bileşen | **Yok** |
| Depodaki `BackgroundService` türevleri | `AlertSchedulerWorker`, `DiscoveryWorker`, `ChangeConnectorScheduler`, `ChangeRetentionWorker`, `EventSinkFlushService`, `RawArchiveService`, `IngestPipeline`, `CatalogRefreshService`, `NotificationDispatcher` |
| Bunlardan korelasyon **değerlendireni** | **Hiçbiri** |

Beş korelasyon `IScopedQuery` üzerinde **sorgu yüzeyi** olarak duruyor
(`GetFirstSeenSignaturesAsync`, `GetSignatureVolumeAsync`,
`GetAttributeLiftAsync`, `GetPropagationAsync`) ve tek tüketicileri
`Bizigo.Evidence/Providers/LogCorrelationProviders.cs`.

`EvidenceCollector` **çekme** (pull) modelinde: bir RCA koşumu *"bu pencerede
ne ilginç"* diye sorduğunda çalışıyor. Yani korelasyonlar bir **olay üretici**
değil, bir **pencere tüketicisi**.

> Bugünkü hâliyle bir korelasyon *"bir şey oldu"* diyemez, yalnızca *"sorarsan
> şunu görüyorum"* der. Aradaki fark tam olarak bir tetikleyicinin tanımı.

**Kuyruk yükü:** ölçülemedi ve **ölçülemez** — ölçülecek bir olay akışı yok.
Aday 1'i seçmek, var olmayan bir üreticiyi seçmek değil, **onu yazmayı**
seçmek demek. Yani aday 1 ile aday 3 arasındaki fark sanıldığı kadar büyük
değil: ikisi de yeni bir bileşen istiyor, biri mevcut sorgulara sarılmış hâli.

---

## Ölçüm 2 · Aday 2 **ayrı bir yol değil** — zaten alarm yolu

Sigma detection'ın ayrı bir tetikleyici olup olmadığı sorusunun cevabı kesin
ve T33'te yazılan koddan okunuyor:

* Sigma kuralı senkronla `AlertRuleEntity` satırına dönüşüyor
  (`Source = AlertRuleSource.Sigma`).
* Kullanıcı açtığında `AlertSchedulerWorker` onu **diğer alarm kurallarıyla
  aynı turda** değerlendiriyor — tek filtre `Status == Enabled`.
* Tetiklendiğinde `alert_triggers` tablosuna **aynı biçimde** satır düşüyor.

Yani Sigma ile kullanıcının yazdığı alarm kuralı arasında **çalışma zamanında
hiçbir yol farkı yok**. §5 tablosunun *"Alarm / Sigma"* satırını tek satır
tutması **doğru**; ikisini ayırmak ikinci bir kuyruk, ikinci bir debounce ve
ikinci bir kota demek olurdu — ve §5'in kendi açılış cümlesi tam bunu
yasaklıyor.

**Sonuç:** aday 2 dördüncü tetikleyici olamaz, çünkü birinci tetikleyicinin
kendisi.

### Yan bulgu: `AlertRaised` kodda **yok**

`AlertRaised` adı yalnızca RCA belgesinde geçiyor; depoda o adla bir olay,
tip ya da yayın yok. Bugün gerçekten var olan sinyal `alert_triggers`
tablosuna düşen **satır**.

Bu bir kusur değil — F4 henüz yazılmadı — ama tasarım okunurken *"AlertRaised
var, ona abone oluruz"* diye okunmamalı. Bağlantı noktası bir **tablo**, bir
olay veri yolu değil. Olay veri yolu isteniyorsa o da yazılacak iş.

---

## Ölçüm 3 · Zincir derinliği ve döngü tespiti

§5 *"senaryo A → RCA → alarm → senaryo A"* döngüsünü örnek veriyor ama tespitin
nasıl yapılacağını söylemiyor. İki aday mekanizma var ve **aynı şeyi
ölçmüyorlar**.

| Mekanizma | Neyi yakalar | Neyi kaçırır |
| --- | --- | --- |
| **Anahtar** `(rule_id, scope, pencere)` | Aynı tetikleyicinin kısa sürede tekrarı | Döngüyü **kaçırır**: A → B → A zincirinde her halka farklı `rule_id` taşıyabilir |
| **Koşu soyağacı** (`root_run_id` + `depth`) | Zincirin kendisini — kim kimi doğurdu | Tek başına debounce yapmaz |

**İkisi rakip değil, farklı iş yapıyorlar** ve §5 zaten birincisini alarm
satırında **debounce** için kullanıyor. Debounce anahtarını döngü tespitine de
koşmak, iki farklı soruyu tek anahtara yüklemek olur — bu depoda defalarca
bedeli ödenmiş bir hata sınıfı.

### Öneri: soyağacı, ve debounce anahtarı **soyağacının içinde**

Her RCA koşumu iki alan taşısın:

* `root_run_id` — zinciri başlatan koşum. Kök koşumda kendisi.
* `depth` — kökten uzaklık. Kök `0`.

Kabul kuralı:

1. `depth >= 2` ise **reddet**. (§5'in ≤ 2 sınırı.)
2. Yeni koşumun tetikleyici anahtarı `(rule_id, scope, pencere)`, **soyağacında
   zaten varsa** reddet — derinlik dolmamış olsa bile.

İkinci kural olmadan `depth ≤ 2` bir döngüyü **kısaltır ama engellemez**: A → B
→ A zinciri derinlik 2'de duruyor ama A ikinci kez koştu ve aynı raporu
üretti. Kullanıcı iki özdeş rapor görür ve kota iki kez ödenir.

**Reddedilen koşum kayda geçsin.** Sessizce düşürmek, *"neden RCA üretilmedi"*
sorusunu cevapsız bırakır — ve bu depoda "veri var, yüzey yok" ile "veri kayda
hiç girmiyor" ayrımını iki kez ödedik. Ret sebebi (`depth` mi, `ata tekrarı`
mı) ayrı tutulmalı: biri sınır, diğeri döngü, ve ikisi farklı şey söylüyor.

---

## Öneri

**Dördüncü tetikleyiciyi bir kaynak olarak yazmayın.**

| | Öneri |
| --- | --- |
| Zincirin ilk halkası | Her zaman diğer üç tetikleyiciden biri |
| Dördüncü satırın rolü | Bir **devam** kuralı: bir RCA koşumunun bulgusu yeni bir koşum doğurabilir |
| Kuyruk yükü | Bağımsız bir akış **değil**, diğer üçünün üstüne **çarpan** (≤ 2 derinlikle sınırlı) |
| Mekanizma | Soyağacı (`root_run_id`, `depth`) + ata tekrarı kontrolü |

Gerekçe üç ölçümün toplamı:

1. Korelasyonlar bugün **üretici değil**; onları tetikleyici yapmak yeni bir
   dedektör yazmak demek ve o iş F4'ün kapsamında yazılı değil.
2. Sigma **zaten** birinci tetikleyici; ayrı bir yol olarak saymak §5'in
   *"dört yol tek kuyrukta"* ilkesini kendi içinden deler.
3. Geriye kalan tek anlam *"RCA'nın RCA doğurması"* — ve o bir kaynak değil,
   bir özyineleme.

### Bu öneri yanlışsa nereden anlaşılır

Öneriyi çürütecek tek bulgu şu olurdu: **RCA koşumu dışında** bir yerde
korelasyonları değerlendiren, bugün var olan bir bileşen. Aradım, **yok**
(Ölçüm 1). Biri F4 kapsamında yeni bir dedektör yazmaya karar verirse öneri
düşer ve dördüncü tetikleyici **gerçekten** dördüncü bir kaynak olur — ama o
zaman kuyruk yükü sorusu da yeniden sorulmalı, çünkü yeni dedektörün sıklığı
tasarım kararı olur, ölçüm değil.

---

## Aramadıklarım ve ölçemediklerim

Bu ayrım bilerek yazılı: *"aradım, yok"* ile *"aramadım"* farklı şeyler.

**Aradım, yok:**

* Korelasyonları zamanlanmış değerlendiren bir bileşen — depodaki dokuz
  `BackgroundService` türevinin hiçbiri.
* `AlertRaised` adında bir olay/tip/yayın — yalnızca RCA belgesinde, kodda yok.

**Ölçemedim:**

* **Korelasyonların tetiklenme sıklığı.** Ölçülecek bir olay akışı yok
  (Ölçüm 1). Canlı veriye karşı *"bu korelasyon şu pencerede kaç sinyal
  üretirdi"* diye bir **simülasyon** koşulabilir ama o ölçüm değil **tahmin**
  olur; eşik seçilmeden sayı üretilemez ve eşik tam da kararın parçası.
* **Kuyruk yükü sayıları.** Yukarıdakine bağlı: eşiksiz bir dedektörün yükü
  tanımsız.
* Makine bu turda sayfalama yapıyordu (2156 swap-in/sn, tavan 1500), bu yüzden
  **hiçbir derleme ya da test koşulmadı**. Bu belge yalnızca kod ve belge
  okumasına dayanıyor; iddiaların hepsi dosya ve satır gösterilerek kuruldu.

---

## Ayrı bir açık soru — F4'ün değil, ama görünür olmalı

**Ürünün bugün push modda anomali tespiti yok.**

Ölçüm 1'in bulgusu F4'ün kapsamına girmiyor ama bir kenara atılmamalı: beş
korelasyon çekme modelinde duruyor ve hiçbir zamanlanmış değerlendirici onları
okumuyor. Yani ürün *"bir şey oldu"* diyen bir bileşen **taşımıyor**; yalnızca
sorulduğunda cevap veren bir yüzey taşıyor.

> **Açık soru:** Korelasyonlar zamanlanmış bir değerlendiriciye bağlanacak mı?

**Sahibi yok, fazı yok, kararı verilmedi** — ve burada karar da verilmiyor.
Yazılmasının tek sebebi görünür olması: F4 bittiğinde bu soru hâlâ açıksa, o
bir **eksiklik değil bilinen bir sınır** olur. Yazılmasaydı ikisi ayırt
edilemezdi.

## Kesişme — kota ajanına iletilecek

Soyağacı önerisi kota kararına değiyor ve **kararı burada verilmiyor**:

> Reddedilen bir koşum (derinlik sınırı ya da ata tekrarı) kotadan **düşülüyor
> mu**?

İki cevap da savunulabilir ve zıt sonuç veriyor: düşülürse bir döngü, kotayı
hiç rapor üretmeden tüketebilir; düşülmezse reddedilen koşum bedava olur ve
bir hata döngüsü kotayı hiç zorlamaz.

§9 gereği kendi kararımı vermiyorum ve doğrudan da iletmiyorum — koordinatör
kota ajanına iletecek. Burada **kesişme olarak işaretli** duruyor ki cevap
geldiğinde nereye yazılacağı belli olsun.

## Tereddüt ettiğim yer

*"Anomali zinciri"* ifadesini **devam kuralı** diye okumam bir yorum. Belgeyi
yazan kişi *"F3'ün korelasyonları bir dedektöre bağlanacak ve o dedektör
dördüncü kaynak olacak"* demek istemiş olabilir — ve o niyet §5'te yazılı
değil, dolayısıyla ben okuyamıyorum.

İki okuma arasındaki fark küçük değil: birincisinde F4 hiçbir yeni bileşen
istemiyor, ikincisinde **bir dedektör** yazılacak ve onun eşiği, sıklığı ve
kotası ayrı kararlar. Bu belgeyi okuyan kişi hangi niyetin doğru olduğunu
söylerse ikinci yol için ayrı bir ölçüm gerekiyor.
