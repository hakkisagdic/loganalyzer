---
kind: ticket
title: "M15 — Kota rezervi Agent kaynağını kapsamıyor"
status: 2
---

# M15 — Kota rezervi `Agent` kaynağını kapsamıyor

M14 `rca.trigger`'ı yazarken ölçtü ve bulgu bir tasarım boşluğu: **`RcaQuota.EffectiveLimit`
rezervi yalnızca `Schedule` kaynağı için uyguluyor.**

Sonucu şu: `EventReservePercent` **açılsa bile** `Agent` ile `Manual` tam limiti
paylaşmaya devam ediyor. Yani koruma sadece kapalı değil — **bu vakayı hiç
kapsamıyor**, ve varsayılanının `0` olması ikincil bir ayrıntı.

## Neden bugün önemli hâle geldi

T46 rezervi *"olay tetikli iş aç kalmasın"* diye kurdu ve o gün MCP yoktu, yani
kotayı tüketen taraflar insan (`Manual`) ve takvim (`Schedule`) idi.

M14 üçüncü bir tüketici getirdi ve niteliği farklı: **bir model, insandan çok daha
hızlı tetikleyebilir.** Ölçülmüş hâl — kota **grup başına** (`DailyPerGroup`,
`MaxConcurrentPerGroup`, sayım `r.OwnerGroup` üzerinden) ve stdio ile HTTP **aynı
havuzu** yiyor. Dolayısıyla:

> Aynı gruptaki bir modelin gürültülü koşumu, o grubun **insanını** RCA'sız
> bırakabilir, ve `EventReservePercent` bunu engellemiyor.

## Ölçüm · gözlem HAZIR DEĞİL — iddia çürütüldü

Bu ticket ilk hâlinde *"gözlem hazır"* diyordu. **Ölçüldü ve çıkmadı**, ve ayrımı
yazmak gerekiyor çünkü karıştırılması yanlış iş yaptırıyor:

| | Durum | Ölçüm |
| --- | --- | --- |
| Kırılım **hesaplanıyor** | ✅ | `RcaQuotaUsage.BySource`, `UsageAsync` içinde `GroupBy(r => r.Source)` |
| Kırılım **sorulabiliyor** | ❌ | `UsageAsync`'in `src/` altında **tek** çağıranı kendi `CheckAsync`'i; `BySource`'u `src/` altında **hiç kimse okumuyor** (tek okuyan bir birim testi) |
| **Koruma** | ❌ → ✅ | Rezerv ekseni `Agent`'ı tanımıyordu; bu ticket düzeltti |

Yani kırılım **hesaplanıyor ve atılıyor**: onu yüzeye çıkaran bir uç, bir CLI
komutu, bir MCP aracı ya da bir ekran yok. Operatörün rezervasyona ihtiyaç
olduğunu görmesi bugün **elle SQL yazmayı** gerektiriyor.

**Bir sayının hesaplanması, sorulabilir olması demek değil.** Aynı sınıf bu depoda
`Produces<T>` kapısında ödendi — kolonlar yerindeydi, kapı üç uç dosyasını hiç
görmedi. `RcaQuotaOptions.EventReservePercent`'in belgesinde duran *"operatör
rezervasyonu gerektiğini veriden görebiliyor"* cümlesi bu yüzden **düzeltildi**;
olduğu gibi kalması, olmayan bir yeteneği varmış gibi okutuyordu.

## Yapılacak

1. ~~**Rezerv eksenini ölç, sonra genişlet.**~~ **Eksen seçilmedi — zaten
   yazılıydı.** Bu maddenin ilk hâli üç şık sunuyordu (*"insan mı makine mi"*,
   *"olay tetikli mi istek tetikli mi"*, *"kaynak başına yüzde mi"*) ve yanlış
   çerçeveydi: `EventReservePercent`'in kendi gerekçesi ekseni söylüyor —
   *"korunmak istenen şey 'takvim az koşsun' değil, **olay tetikli iş aç
   kalmasın**"*. Yapılan şey bir eksen seçimi değil bir **sadakat düzeltmesi**:
   ölçüt artık *"kaynak olay tetikli mi"* ve yalnızca `Alert` öyle.

   **Kusur üç yerde birden yazılıydı** ve birbirini doğruluyordu — uygulamada
   (`source != Schedule`), `EffectiveLimit`'in belgesinde (`Alert`, `User`, `Api`;
   son ikisi enum'da bile yok) ve testinde
   (`Rezervasyon_yalnizca_takvim_kaynagini_daraltiyor`, `Manual` ile `External`'ı
   "olay tetikli" sayıyordu). **Bekçi vardı ve yanlış eksende yeşildi**; kusuru
   hiçbir şeyin yakalamamasının sebebi buydu.

2. **Sayı önerilmedi.** T46'nın gerekçesi geçerli: *"ölçülene kadar tek slot,
   yavaş ama yanlış değil."* Ama bu maddenin dayanağı **düzeltildi**: rezerv
   yüzdesinin gerçek kullanımdan gelmesi, kullanımın **görünür olmasından**
   sonra mümkün — ve yukarıdaki ölçüm görünür olmadığını gösterdi.

3. **Kırmızı ölçüldü.** Bekçi bedavaydı ve iki yönlü yazıldı: rezerv açıkken
   ajan daraltılmış sınırda **reddediliyor**, ve aynı havuzda alarm **hâlâ
   geçiyor**. İkincisi rezervin var olma sebebi; yalnızca reddi ölçmek her şeyi
   reddeden bir uygulamayla da geçerdi.

## Bu düzeltmenin KAPATMADIĞI şey — ve tetikleyici koşulu

Rezerv `Alert`'i istek tetikli kaynakların **hepsinden** koruyor, ama istek
tetikli kaynakları **birbirinden** korumuyor. Yani M14'ün asıl endişesi bu
düzeltmeden **sonra da** açık:

> Aynı gruptaki bir `Agent` koşumu, bir `Manual` koşumun payını hâlâ yiyebilir.

Bu **ayrı bir soru** ve T46 onu hiç sormadı: rezerv ekseni *olay ↔ istek*, oysa
bu soru istek tetiklinin **içindeki** payı bölmek istiyor — *"insan mı makine
mi"* ekseni. İkisini tek mekanizmaya yüklemek, rezervin adıyla söylediği şeyi
bulanıklaştırırdı.

**Mekanizma yazılmadı ve sayı önerilmedi** (§8 — bugün tüketicisi yok).
Tetikleyici koşul yazılı olsun:

> **Gerçek kullanımda bir `Agent` koşumunun bir `Manual` koşumu reddettirdiği
> görüldüğünde ayrı bir ticket doğar.** O ölçümün yapılabilmesi yukarıdaki gözlem
> boşluğunun kapanmasına bağlı — yani sıra: gözlemi yüzeye çıkar, sonra bu
> soruyu ölç.

## M16 · gözlem yüzeye çıktı — üç aday ölçüldü, biri seçildi

Yukarıdaki boşluk kapandı: `bizigo rca quota --owner-group <grup>` kaynak başına
tüketimi **ve kaynak başına etkin sınırı** basıyor. Kırılım
`RcaQuotaUsage.BySource`'tan geliyor; ikinci bir toplama yazılmadı.

Üç aday ölçüldü ve **ikisinin bugün okuyucusu yok**:

| Aday | Ölçüm | Sonuç |
| --- | --- | --- |
| `GET /v1/rca/quota` | RCA ekranı kotayı **bilerek** kapsam dışında bırakmış — `ui/src/app/rca/page.tsx`: *"kuyruk/kota/debounce yok — onlar dört tetikleyiciyle birlikte F4'te"* | ❌ tüketicisi **ertelenmiş** (§8) |
| `rca.quota` MCP aracı | Modelin cevabı **zaten var**: `rca.trigger` kota dolduğunda `state=rejected` + `reason` döndürüyor, `rca.runs` her satırda `counts_against_quota` taşıyor. Reaktif yol **bedava** — reddedilen koşum kotadan düşülmüyor | ❌ ikinci yol, yeni yetenek yok (§9) |
| `bizigo rca quota` | Okuyucu **var ve M15 onu yarattı**: rezerv yüzdesine karar verecek operatör | ✅ **seçildi** |

Emsal `bizigo fields coverage`: bu depoda *"ölç ve bas"* komutunun okuyucusu bir
ekran değil, **karar veren bir insan**.

### Bekçi · "hesaplanıp atılan alan" — bir SINIFIN ilk örneği

`ComputedButUnreadTests` `RcaQuotaUsage.BySource`'un üretimde bir okuyucusu
olduğunu `Bizigo.Cli`'nin **MemberRef tablosundan** ölçüyor. M15'ten önce
**kırmızı yanardı** — kırılımı okuyan tek yer bir birim testiydi, ve bir birim
testi okuyucu değil.

⚠️ **Genel bir kural yazılamadı ve sebebi yazılı:** bir tipin her özelliği için
*"üretimde okunuyor mu"* sormak, meşru olarak okunmayan onlarca alanı da kırmızı
yakardı (eşitliğe giren ama okunmayan `record` alanı, serileştirilip tele inen
ama kod tarafından okunmayan alan). Ayrımı yapabilen mekanik bir ölçüt **yok**:
hangi alanın bir *karar* için var olduğunu bilen tek şey onu yazan kişi. Yani bu
bekçi sınıfın **ilk örneği**, sınıfın kendisi değil — bir sonraki kişi genel
kuralı aramasın.

## Ölçülmeyen iki komşu — yazılı, ticket açılmadı

İkisi de bu ticket'ın kapsamı dışında ve **ölçülmediler**:

1. **`MaxConcurrentPerGroup`'ta rezerv ekseni yok.** Rezerv yalnızca *günlük*
   sayaçta uygulanıyor; eşzamanlılık tavanında kaynak ayrımı hiç yok. Yani bir
   ajan grubun eşzamanlı slotlarını doldurabilir ve günlük kotası dolmamış
   olabilir. Eşzamanlılık ayrı bir soru: günlük kota *bütçe*, eşzamanlılık
   *anlık kapasite*, ve ikisinin rezervi aynı sayı olmak zorunda değil.

2. **`Schedule` ile `Agent` daraltılmış sınırı PAYLAŞIYOR.** T46'nın §6.1
   gerekçesi (*"bir kaynağın öngörülebilirliği, başka bir kaynağın
   öngörülemezliğini eziyor"*) bu ikisi arasında da geçerli olabilir: takvim
   öngörülebilir, ajan değil. Bugün ikisi aynı rezerv-sonrası havuzu yiyor.

## Bu ticket'ın YAPMADIĞI şey

**Kotayı kimlik başına saymaya çevirmek.** T46 tek havuz kararını gerekçeleriyle
verdi (`§6.1`) ve bu ticket onu açmıyor: sorun havuzun **birliği** değil, havuz
içindeki **payın** kaynağa göre ayrılamaması.
