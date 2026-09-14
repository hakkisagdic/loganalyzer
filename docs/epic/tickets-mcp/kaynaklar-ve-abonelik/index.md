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

## 7 · Yazılmayan: abonelik — ve gerekçesi

`rca.runs` aboneliği (kabul kriteri 2 ve 4) **yazılmadı**, ve sebebi bir zaman
kısıtı değil §8:

**`rca.runs` main'de yok.** Ölçüldü: `Bizigo.Mcp.Product/Tools/` altında beş araç
var (`alerts.rules`, `alerts.triggers`, `catalog.parsers`, `inventory.list`,
`logs.search`) ve **hiçbir `rca.*` aracı yok**. Ticket'ın §5'i M05'i bağımlılık
olarak sayıyor ve o dilim henüz inmemiş. Aboneliğin hedefi olan belge
yokken abonelik yazmak, *tüketicisi olmayan bir tip* yazmaktır.

**İkinci sebep mekanizmanın yeri.** Bildirim bir **yayın noktası** istiyor:
koşum durumu `RcaAdmission.TryStartAsync` / `StopAsync` / `AttachBundleAsync`
içinde değişiyor. Doğru tasarım `Bizigo.Contracts`'ta bir bildirim arayüzü ve
`RcaAdmission`'a isteğe bağlı bir yayıncı — ama o dosya T45/T46'nın alanı ve
`rca.runs` gelmeden yayınlanacak bir şey de yok.

Bugünkü hâl bu boşluğu **sessiz bırakmıyor**: `SupportsSubscription` her kaynakta
`false` ve `Capabilities.Resources.Subscribe` ondan **türüyor**. Yani sunucu
abonelik **ilan etmiyor** — istemciye verilmemiş bir söz yok. Bir kaynak bir gün
`true` derse yetenek kendiliğinden açılıyor ve bekçi
(`Desteklenmeyen_yetenek_ilan_edilmiyor`) bildirimi göndermeyen bir aboneliği
yakalıyor.

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
