---
title: "M10 — Üç ertelenmiş ürün aracı"
kind: ticket
status: 2
---

# M10 — Üç ertelenmiş ürün aracı

Üçü de daha önce bilinçli olarak ertelenmişti ve üçünün de sahibi yoktu.
Ticket **iş bittikten sonra** yazıldı: T61'in statü bekçisi merge edilmiş bir
kimliğin hiçbir tabloda anılmadığını gördü ve kırmızı yandı. Kaydın geç
yazılması bir kusur; kapının onu yakalaması kapının işi.

| Araç | Ne | Neden ertelenmişti |
| --- | --- | --- |
| `logs.get` | Tek olayın ham baytları | M06'nın redaksiyon kapısının **ilk gerçek müşterisi** — kapı yazıldığında tüketicisi yoktu |
| `rca.quality` | `GET /v1/rca/quality`'nin araç yüzeyi | T47 atılan cümle oranını ölçene kadar taşıyacak sayı yoktu |
| `alerts.maintenance` | Bakım penceresi | M04 kapsamdan çıkarmıştı: *"yazma mı okuma mı"* cevaplanmamıştı |

## Verilen karar · `alerts.maintenance` OKUMA

**Bu ürün MCP üzerinden yazma yapmıyor.** Dört gerekçe ve dördüncüsü belirleyici:

1. `ProductReadTool.IsReadOnly` `sealed` — yazan bir araç kendi tabanını, kendi
   kapsam kapısını ve kendi `ScopeRejection`'ını getirirdi: **K17'nin yolunda
   ikinci bir kapı** (§9).
2. **Aktör kaydı yok.** REST `CreatedBy = scope.Subject` yazıyor; MCP'de o özne
   çağıran kimlik ve kayıt *"insan karar verdi"* ile *"model karar verdi"*
   arasında ayrım taşımıyor. T54'ün aynı sınıfı.
3. Silme geri alınamaz ve izi yok.
4. **Bu yazmanın hatası SESSİZ.** Pencerenin işi alarmı *bastırmak*; yanlış
   açılmış bir pencerenin belirtisi bir hata değil **sessizlik** — alarm
   çıkmıyor, ekran sağlıklı görünüyor. Ürünün MCP'de vereceği **ilk** yazma
   yetkisi, hatası görünmeyen **tek** yazma olurdu.

Karar mekanik olarak tutuluyor: `Hicbir_urun_araci_yazma_cagirmiyor`, IL taraması.

## Ölçülen bulgu · yapısal kanalda kapı derleyicide DEĞİL

Brief *"kapıyı atlayan bir çağrı derlenmemeli; derleniyorsa bulgudur"* diyordu.
**Derleniyor.** `logs.get`'in yük alanı `string` yazılıp gövde olduğu gibi
verildiğinde çözüm **0 hata 0 uyarı** derlendi.

Kapı yalnızca `WithLogText(params RedactedPrompt[])` imzasında derleyicide;
`structuredContent` kanalında değil. Bu **yeni bir kusur değil** — M06 kendi
belgesinde beyan etmişti (*"o alanın öyle yazılması bugün MEKANİK OLARAK
TUTULMUYOR"*) ve bekçiyi bilerek yazmamıştı: o gün tüketici yoktu ve §8
görülmeyen şeye bekçi yazmayı yasaklıyor. M10 tüketiciyi getirdi, bekçi
yazıldı — **ama bir test, bir derleme şartı değil.**

Bütün araçlar için mekanik ölçüt **yazılamıyor**: bir yükte meşru `string`'ler
var (kaynak adı, damga, kimlik) ve hangisinin log içeriği taşıdığına karar
verebilen makine yok. Kapsam yazılı.

## Ölçülen bulgu · `IlCallReader` `async` gövdeleri kaçırıyordu

`Hicbir_urun_araci_yazma_cagirmiyor` §6 ölçümünde bir kez **yeşil kaldı**:
`SaveChangesAsync` kusuru konulduğunda görmedi. Sebep mekanik — `async` bir
metodun gövdesi IL'de o metotta durmuyor, derleyici onu iç içe bir durum
makinesine taşıyor, ve ürünün **her** aracı `async`. Yani bekçi bütün gövdeleri
kaçırıyordu. `WithStateMachines` ile düzeltildi ve `IlCallReader`'ın belgesi
artık üç kör nokta sayıyor.

## Bağlam bütçesi · `rca.quality` bir tur tavanı aştı

| Hâl | Belirteç |
| --- | --- |
| Üç iç içe nesne | 792 |
| Düzleştirilmiş, REST'in alan adlarıyla | 773 |
| + türetilebilir üç alan düştü + açıklama kırpıldı | **679** |

Düzleştirmenin yalnızca **19 belirteç** kazandırması maliyetin nerede olduğunu
söylüyor: zarflarda değil **alan adlarının kendisinde** — her ad `properties` ve
`required` içinde iki kez sayılıyor. Bu JSON Schema'nın şekli, aracın kusuru
değil, ve tavanın sonraki konuşmasının **bu aracı değil şemanın şeklini**
ilgilendirdiği anlamına geliyor.

Düşen üç alanın ölçütü belirteç avı değil bir kural: *kalan alanlar üzerinde tek
bir aritmetik işlemle elde edilebilen ve `null` kuralı olmayan alan düşer*.
`measured_coverage` kuralın dışında bırakıldı çünkü o bir sayı değil bir
**uyarı**.

## Kesişme · `AlertSuppression.cs`

Yarı-açık aralık kararı (`[StartsAt, EndsAt)`) `IsOpen(window, now)` diye
çıkarıldı; `IsInMaintenanceWindow` onu çağırıyor. Gerekçe §9: aracın `state`
alanı bir kural taşımıyor, dolayısıyla aralığı **kendisi yazması** gerekiyordu ve
kopya ayrıştığı gün gösterge *"pencere kapandı"* derken motor hâlâ bastırıyor
olurdu — belirtisi olmayan bir ayrışma. `<` → `<=` tek satırı **iki bekçiyi
birden** düşürüyor.

## Kabul kriterleri

- [x] Üç araç `ProductReadTool` kalıbıyla, beklenen küme **tek yerde**
- [x] `alerts.maintenance` yazma yapmıyor ve bu IL taramasıyla tutuluyor
- [x] Araç başına tavan (700) **yükseltilmedi**
- [x] §6: 8 kusurun 8'i kırmızı yandı, 3 karşı-kontrol yeşil kaldı
- [x] Yapısal kanalın kapı dışında kaldığı **ölçüldü ve yazıldı**

## Bu ticket'ın açtığı kalem

**`bizigo mcp serve --surface bizigo` bugün hiç ayağa kalkmıyor** — ayrı bir
ticket. Ölçüldü: `AlertRulesTool` kurulamadığı için süreç `exit 1` veriyor,
simülatör yüzeyi aynı ikiliyle `exit 0`. Sebep koordinatörün M02 merge'inde
`ToolAssembliesFor(Product)`'a ürün derlemesini eklemesi ve stdio'nun servis
grafiğini **tamamlamaması**. HTTP yüzeyi yeşil, stdio ürün yüzeyi ölü, arada
kapı yok.

**Aramadım:** `logs.get`'in `attrs` sınırı (60) gerçek bir parser çıktısına karşı
ölçülmedi — üretimde kaç anahtar geldiği bilinmiyor, sayı tavan olarak seçildi.
