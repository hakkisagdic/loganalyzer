---
title: "M07 — Kaynaklar ve abonelik"
kind: ticket
status: 1
---

# M07 — Araç değil veri: kaynaklar ve durum bildirimi

[MCP teknik plan §5](../../mcp-teknik-plan/index.md): *"Kaynaklar (resources)
olarak sunulacaklar **araç değil veri**: kanıt paketi belgesi, RCA raporu,
parser tanımı. Abonelik `rca.runs` için anlamlı — koşum durum değiştirdiğinde
bildirim."*

Ayrım önemli ve ticket'ın tamamı ona dayanıyor: bir **araç** çağrılır ve bir
iş yapar; bir **kaynak** adreslenir ve okunur. İkisini karıştırmak, her
okumayı bir araç çağrısına çevirip [bağlam bütçesini](#6--bilinen-sınırlar-ve-açık-sorular)
gereksiz yere şişirir.

## 1 · Bugünkü hâl — ölçüldü

Kaynak olarak sunulacak üç şeyin **verisi** bugün var:

| Kaynak | Bugünkü karşılığı |
| --- | --- |
| Kanıt paketi belgesi | `GET /v1/rca/{id}`; `Bizigo.Evidence` tarafında paket deposu |
| RCA raporu | `Bizigo.Rca/Reasoning/RcaReportStore` + `RcaReportDocument` (bu hafta T44/T51 ile girdi) |
| Parser tanımı | `GET /v1/parsers`, `catalog/parsers/` |

`rca.runs` durum kaynağı: `GET /v1/rca/runs` var (T46'nın üç yönlü ayrımı —
[M05 §6.2](../rca-araclari/index.md)'de doğrulanmamış olarak işaretli).

**Ölçüldü (M07):** MCP kaynak yeteneği M01'in dalında **hiç kurulmamıştı** —
`options.Capabilities` yalnızca `Tools` ilan ediyordu ve yorumda *"Kaynaklar
M07'de"* yazılıydı. §6.2'nin birinci maddesi bu sorunun cevabını ve orada
çıkan kusuru taşıyor.

## 2 · Kapsam

**İçinde**

- Üç belge türünün **kaynak** olarak sunulması: kanıt paketi, RCA raporu,
  parser tanımı.
- `rca.runs` için **abonelik**: koşum durum değiştirdiğinde bildirim.
- Kaynakların **kapsam** kapısından geçmesi — bir kaynak URI'si adreslenebilir
  olduğu için kapsamı atlamanın en kolay yolu ([M04](../okuma-araclari/index.md)
  kapısı burada da geçerli).

**Dışında**

- İstemler (prompts). Plan §2 onları uyum tablosunda sayıyor ama araç
  tablosunda karşılığı yok; bu ticket **eklemiyor**.
- Araçların kendisi — M04 ve M05.

## 3 · Kabul kriterleri

1. Üç kaynak türü bir URI şemasıyla adreslenebiliyor (§6.1).
2. `rca.runs` aboneliği koşum durum değiştirdiğinde bildirim gönderiyor, ve
   bildirim **istemcinin desteklediği** bir yetenekse gönderiliyor — bitti
   tanımı §1'in *"desteklenmeyen yetenek kullanılmıyor"* şartı burada da geçer.
3. **Kaynak okuma kapsamı atlamıyor.** Bir kaynak URI'si tahmin edilebilirse
   (ör. sıralı kimlik), kapsam kontrolü URI'nin kendisinde değil **okuma
   yolunda** olmalı.
4. Abonelik **sızdırmıyor**: istemci gittiğinde abonelik kapanıyor.
5. Kırmızı yanabildiği ölçüldü: kapsam dışı bir kaynağın URI'si doğrudan
   istendiğinde reddedildiği görüldü.

## 4 · Bitti tanımından karşıladıkları

Doğrudan bir madde **sahiplenmiyor**; bu yazılı olsun ki okuyan kişi M07'yi
bir maddeye bağlı sanmasın. Beslediği:

- **§1** — desteklenmeyen yeteneğin kullanılmaması, abonelik tarafında.
- **§7** — kapsam tek kapıdan; kaynaklar bu kapının **en sessiz** kaçış yolu.

## 5 · Bağımlılık ve sıra

**M05.** Abonelik `rca.runs` için anlamlı ve `rca.runs` M05 ile geliyor; kanıt
paketi ile RCA raporu da M05'in ürettiği/okuduğu şeyler. Kaynakları önce
yazmak, tüketicisi olmayan bir adres alanı yazmak olurdu.

## 6 · Bilinen sınırlar ve açık sorular

### 6.1 · URI şeması — **karar verildi ve ölçüldü**

Plan §2'nin uyum tablosu *"Kaynaklar | URI şeması, abonelik, değişiklik
bildirimi"* diyor ve şemayı **tarif etmiyor**. Karar M07'nin ve şu:

```
bizigo://{tür}/{kimlik}
```

| Tür | Kimlik | Gövde |
| --- | --- | --- |
| `evidence-bundle` | kanıt paketi `Guid`'i | `text/markdown` — `DeterministicReport` |
| `rca-report` | **kanıt paketi** `Guid`'i | `text/markdown` — en yeni LLM raporu |
| `parser` | parser kimliği (`fortinet/fortigate`) | `application/json` — **yüklü** tanım |

Üç karar ve gerekçeleri:

**Adres kapsam taşımıyor.** Kısıt buydu ve tutuldu: şablonun **tek** değişkeni
var, ve bir bekçi (`Adres_kapsam_tasimiyor`) ikinci bir değişken eklendiği gün
kırmızı yanıyor. Kapsam **okuma yolunda** — `BundleScope.IsReadableBy`, yani
REST'in geçtiği aynı kapı.

**`rca-report`'un adresi paketin kimliği**, raporun değil. Raporun kendi
`owner_group`'u yok; kapsamını paketten devralıyor ve `RcaReportStore.LatestForAsync`
imzası bunu zorluyor (bir `EvidenceBundle` istiyor, `Guid` değil). Rapor
kimliğine adreslemek, kapsamın kapıya nasıl geldiğini ikinci kez kurmak olurdu.

**Şema `bizigo://`, `https://` değil.** Kasıtlı olarak **dolaşılamaz**:
`https://` bir adres, istemcinin ya da modelin onu *getirebileceği* izlenimini
verir — oysa bu belgeler yalnızca kimliğin çözüldüğü MCP oturumundan okunabiliyor.

### 6.2 · Açık soruların ölçülen cevapları

1. **Kaynak yeteneği M01'de ilan ediliyor muydu?** **Hayır** — `Capabilities`
   yalnızca araçları ilan ediyordu ve yorumda *"Kaynaklar M07'de"* yazılıydı.
   M07 onu keşfedilen kümeye bağladı. **Ve burada bir kusur ölçüldü:** yeteneği
   koşullu atamak **yetmiyor**. SDK, `ResourceCollection` **var olduğu için**
   yeteneği kendisi ilan ediyor — boş olsa bile. Yani `bizigo-sim` el
   sıkışmasında `ResourcesCapability { ListChanged = True }` çıkıyordu. İlan
   edilmemesi için koleksiyonun **hiç kurulmaması** gerekiyor.
2. **Bildirim sıklığı bir bütçe kalemi mi?** Bugün cevap yok, çünkü abonelik
   **yazılmadı** (§7).
3. **Değişiklik bildirimi kaynaklar için de var mı?** `listChanged` bugün
   SDK'nın türettiği değer ve `true`; kaynak kümesi süreç ömrü boyunca sabit
   olduğu için bildirim hiç gönderilmiyor. **Yazılı bir açık kalem** —
   ilan edilen ama kullanılmayan bir yetenek.

### 6.3 · Bağlam maliyeti — **ölçüldü, ve ticket'ın iddiası eksikti**

Ticket şöyle diyordu: *"kaynaklar araç değil, yani `tools/list` bütçesine
girmiyorlar — bu ticket'ın sessiz kazancı bu."* Doğru ama eksik: kaynaklar
**kendi** listesini taşıyor (`resources/templates/list`) ve o da modelin
bağlamına giriyor.

**Ölçülen (SDK'nın kendi serileştiricisiyle, üç kaynak):**

| Kaynak | Belirteç | Bunun açıklaması |
| --- | --- | --- |
| `evidence-bundle` | 170 | 59 |
| `rca-report` | 148 | 62 |
| `parser` | 143 | 60 |
| **toplam** | **461** | |

Yani kaynak başına ~154 belirteç — araç başına ölçülen **194**'ten pahalı
değil ama **bedava da değil**. Gerçek ayrım büyüklükte değil **ne zaman
ödendiğinde**: `tools/list` **her** oturumda geliyor, kaynak listesi ise istemci
kanalı **kullanırsa**. Üç belgeyi üç araç olarak sunmak ≈580 belirteç ederdi ve
her bağlamda taşınırdı.

**İlk ölçüm 500 çıktı ve yanlıştı** — `JsonSerializerDefaults.Web` `null`
alanları da yazıyor, oysa tel üzerinde giden şey `McpJsonUtilities.DefaultOptions`.
Aradaki 39 belirteç hiç gönderilmeyen alanlardı. Ölçümün **neyi** ölçtüğü,
ölçülen sayı kadar önemli.

Araç tavanı (700) **değiştirilmedi**; kaynakların kendi tavanı var (200), çünkü
iki liste ayrı yanıtlarda taşınıyor ve tek sayıya toplanması hangisinin
büyüdüğünü gizlerdi.

## 7 · Abonelik — yazıldı, ve iki sınırı ölçüldü

M07'nin **birinci turunda** abonelik yazılmamıştı ve gerekçesi §8'di: *"`rca.runs`
main'de yok — aboneliğin hedefi olan belge yokken abonelik yazmak tüketicisi
olmayan bir tip yazmaktır."* `rca.runs` M14 ile main'e girdi, önkoşul oluştu.

### 7.1 · Kurulan şey

| Parça | Nerede |
| --- | --- |
| Abonelik hedefi kaynak | `RcaRunsResource` — `bizigo://rca-runs`, **şablonsuz** |
| Yayın sözleşmesi | `Bizigo.Contracts` · `IRcaRunChangeListener` + `RcaRunChange` |
| Yayın kanalı | `Bizigo.Mcp` · `McpResourceUpdates` (canlı sunucu defteri) |
| Köprü | `Bizigo.Mcp.Product` · `McpRcaRunChangeListener` |
| Yayın noktaları | `RcaAdmission` · `AdmitAsync`, `AttachBundleAsync`, `StopAsync` |

`rca-runs` gövdesi `rca.runs` aracının **kendi** `ExecuteScopedAsync`'inden
üretiliyor — ikinci bir sorgu yok. Bedeli olurdu: aracın kapsam filtresi
`Take`'ten **önce** uygulanıyor ve o kararı ikinci kez doğru yazmak zorunda
kalmak, bir gün yanlış yazmak demektir.

### 7.2 · ⚠ Dört geçişten üçü bağlı

`TryStartAsync` (→ `Running`) **bağlı değil**: T54 aynı turda o metodun imzasını
değiştiriyor ve derleyicinin zorladığı değişiklik önce gelmeli — tersi sırada
eklenen çağrı metinsel olarak temiz merge olur ve **derlenmeyen** bir ağaç kalır
(§5). Sonucu ölçülebilir: bir abone koşumun *başladığını* öğrenmiyor, yalnızca
kuyruğa girdiğini ve bittiğini.

`Kosum_basina_uc_bildirim` testi `TryStartAsync`'i **bilerek çağırıyor**;
bağlandığı gün sayı 2'den 3'e çıkıp test kırmızı yanıyor.

### 7.3 · Revizyon: `resources/subscribe` KALDIRILMIŞ

Çivilediğimiz `2026-07-28` (SEP-2575) `resources/subscribe` ve
`resources/unsubscribe`'ı kaldırıp yerine `subscriptions/listen` +
`resourceSubscriptions` koymuş. Ölçüldü — sunucunun cevabı göç ipucu taşıyor:

```
The method 'resources/subscribe' is not available on protocol version
'2026-07-28'. Use 'subscriptions/listen' with 'resourceSubscriptions' instead.
```

**SDK'nın istemci tarafı bu göçü yapmamış:** `McpClient.SubscribeToResourceAsync`
hâlâ kaldırılmış RPC'yi çağırıyor ve `subscriptions/listen` için hiçbir istemci
API'si yok. Bekçiler bu yüzden **ham JSON-RPC** yazıyor — zinciri SDK
istemcisiyle ölçmek mümkün değil.

### 7.4 · Yetenek taşımaya bağlı

`Apply` bir parametre daha alıyor: `subscriptionsDeliverable`, **varsayılan
`false`** (tehlikeli taraf *fazla ilan etmek*). Gerekçe SEP-2567: aynı revizyon
`Mcp-Session-Id`'yi kaldırdı, yani akışlanabilir HTTP'de **oturum yok** ve
tutulacak bir sunucu örneği de yok. stdio veriyor, HTTP vermiyor.

Ölçülen: bayrak `false` iken sunucu `resourceSubscriptions` isteğini
**onaylamıyor** (`notifications:{}`). Yani bayrak aboneliğin kabul edilmesinin
şartı, bir süsleme değil.

### 7.5 · İki ölçülen sınır — açık kalem

1. **Bildirim abonelik kimliğiyle etiketlenmiyor.** Spesifikasyon
   `_meta/io.modelcontextprotocol/subscriptionId` istiyor; onay bildirimi
   etiketli geliyor (SDK'nın kendi yolu), bizim yayınımız değil — erişilebilen
   tek yayın ilkeli oturum geneline yazan `SendNotificationAsync`, SDK'nın
   yönlendirmesi `internal`.
2. **Bildirim kapsam süzgecinden geçmiyor.** İçerik taşımadığı için veri
   sızmıyor (adresi okumak kapsam kapısından geçiyor), ama *zamanlaması* bir
   sinyal: abone kapsamı dışındaki bir grupta bir şey olduğunu öğreniyor.

İkisinin de çözümü aynı: `SubscriptionsListenHandler`'ı **sahiplenmek**
(`McpRequestHandler<SubscriptionsListenRequestParams, EmptyResult>`). Bugün
yapılmadı çünkü SDK'nın kendi işleyicisi aynı akışta `*/list_changed` yayılımını
da taşıyor. Sınırın ikisi de **bekçiyle kilitli**
(`Bildirim_abonelik_kimligiyle_etiketlenmiyor`) — SDK bir gün yüzey açarsa
kırmızı yanıp haber veriyor.

### 7.7 · Neden ham JSON-RPC — SDK'nın istemcisi sunucusunu izlemiyor

Bekçiler abonelik zincirini **ham JSON-RPC** ile ölçüyor ve bir sonraki kişi
*"neden SDK istemcisi kullanılmadı"* diye soracak. Cevap ölçüldü:

**`McpClient.SubscribeToResourceAsync` hâlâ kaldırılmış `resources/subscribe`
RPC'sini çağırıyor ve `subscriptions/listen` için hiçbir istemci API'si yok.**
Aynı paket, aynı sürüm — sunucu tarafı `2026-07-28`'e geçmiş, istemci tarafı
geçmemiş.

Bu M01'de çivilediğimiz ayrımın en net kanıtı (*"güven SDK'ya değil ölçüme
bağlı"*) ve genelleştirilebilir hâli şu: **bir SDK'nın sunucu tarafının bir
revizyona geçmesi, istemci tarafının geçtiğini göstermiyor** — ve *"aynı paket,
aynı sürüm"* cümlesi bunu gizliyor.

Bekçilerin ham mesaj yazmak zorunda kalması bir zahmet değil, **kanıtın
kendisi**: zincir SDK'nın kendi istemcisiyle ölçülemiyor.

### 7.8 · Doğru bir gerekçe, yeterli bir gerekçe değil

Bu turda kendi kodumda bir açık çıktı ve şekli kayda değer.

`subscriptionsDeliverable` ilk hâlinde stdio'da **sabit `true`**'ydu ve
gerekçesi yazılıydı: *"stdio'da tek, uzun ömürlü bir sunucu var."* Cümle
**doğruydu** ve **yetmiyordu**: şart iki parçalıydı — kanal var **ve** yayıncı
var — ve ikincisi varsayılmıştı. Yayın defteri DI'da kayıtlı değilse
yayınlayacak kimse yok, ama sunucu `subscribe` ilan etmeye devam ediyordu. Yani
kaçınmak istediğim şey — *ilan edilen ama kullanılmayan yetenek* — tam o satırın
kendisinde duruyordu.

Kuralın hâli: **bir kararın gerekçesi doğru olduğu için tam olmuyor.** Gerekçe
bir şartın bir parçasını anlatıyorsa, geri kalanı yazılmadığı sürece
varsayılmış oluyor — ve varsayım tam olarak kapının olmadığı yer.

### 6.4 · Bildirim sıklığı bir bütçe kalemi mi — dolaylı olarak

Ölçülen: **koşum başına 2 bildirim** (bugün), `TryStartAsync` bağlanınca **3**.

Bildirim **içerik taşımıyor**, yalnızca adres (~10 belirteç). Yani bağlamı şişiren
şey bildirimin kendisi **değil**, abonenin her bildirimde belgeyi **yeniden
okuması**: koşum başına üç okuma, her okuma `rca-runs` gövdesi kadar.

Kaynak **ilanı** tarafında abonelik hiçbir şey eklemedi — yetenek nesnesinde tek
bir bayrak, bildirimler listeye hiç girmiyor.

## 8 · Ölçülen ve M07'nin dışında kalan iki kalem

**1 · stdio ürün yüzeyi bugün ayağa kalkmıyor.** Ölçüldü:

```
$ bizigo mcp serve --surface bizigo --data-boundary internal
Unhandled exception: MCP ilkeli `Bizigo.Mcp.Product.Tools.AlertRulesTool`
kurulamadı: Unable to resolve service for type 'Bizigo.Alerting.AlertRuleService'
```

Kusur **M07'den önce** var: `McpCommandHandlers.BuildServices()` simülatör ve
komut araçlarının bağımlılıklarını kaydediyor, **ürün okuma araçlarının**
bağımlılıklarını (`AlertRuleService`, `ParserCatalog`, `IScopedQuery`)
kaydetmiyor. M01'in *"iki taşıma, aynı araç kümesi"* iddiası ürün yüzeyi için
**ölçülmemiş**; mevcut stdio testleri yalnızca ret yollarını sınıyor.

M07 bu kusuru **büyütmüyor ama gizlemiyor** da: kaynaklar da aynı grafikten
kuruluyor. Düzeltme M02/M04'ün alanı (CLI kompozisyonu).

**2 · `Bizigo.Mcp.Product` iki referans daha aldı.** `Bizigo.Evidence` ve
`Bizigo.Rca`. Ölçülen maliyet: Evidence → Contracts + Query (Cli'de ikisi de
var), Rca → Contracts + ControlPlane + ScenarioPlugin + `Extensions.Http`. Yeni
çatı yok. Alternatif (ayrı derleme, yalnızca `Bizigo.Api`'den referans) elendi:
o zaman stdio bu kaynakları hiç göremezdi, yani aynı yüzey iki taşımada farklı
şey sunardı.
