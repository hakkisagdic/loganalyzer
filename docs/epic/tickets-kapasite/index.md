---
title: "Kapasite ölçümü — Implementasyon Ticket'ları"
kind: story
status: 2
---

# Kapasite Implementasyon Ticket'ları

[Kapasite ölçümü](../kapasite-olcumu/index.md) planı beş iş kalemi bırakıyor.
Bu belge onların **evi**: tablo burada, gerekçe planda.

**Bugünkü dilim kapandı (2026-09-21):** B01 ve B02 tamamlandı. B03–B05 henüz
ticket değil; hedef donanım ve sürdürülebilirlik ölçütü seçildiğinde açılacak
ayrı bir kapasite koşumu. Bu story'nin `status: 2` olması o gelecekteki ürün
kararlarını verilmiş saymıyor.

## Neden ayrı bir ev — ölçülmüş bir bulgudan doğdu

B02 yazılırken ölçüldü: `EpicStatusTests` bir `ticket` belgesini **iki kez
birden** bulamıyordu.

| Sebep | Neden yapısal |
| --- | --- |
| Kimlik deseni `[TSM]\d+` | `B` ile başlayan hiçbir kimlik eşleşmiyor |
| Dizin globu `tickets*` | B tablosu `kapasite-olcumu/` altındaydı, yani hiç okunmuyordu |

Karar (koordinatör, 2026-09-15) **iki parçalı** ve ikisi ayrı gerekçeye
dayanıyor:

- **Dizin globu genişletilmedi, seriye ev verildi.** Globu
  `kapasite-olcumu`'nu da kapsayacak şekilde açmak bir dizini **özel durum**
  yapardı; kural *"ticket'lar `tickets-*` altında yaşar"* ise özel durum kuralı
  zayıflatır. Her fazın şekli zaten bu: `tickets-fN/index.md` (story + tablo)
  **artı** plan belgeleri.
- **Kimlik deseni genişletildi** (`[TSM]` → `[TSMB]`). Bu bir özel durum değil,
  **seri adının tanınması**. Genişletme ölçüldü: bir `B` satırının artık
  **bulunduğu** ve tanınmayan bir öneğin (`X99`) hâlâ **bulunmadığı** birlikte
  sınandı — yoksa genişletme sessizce *"her şeyi kabul et"* hâline kayardı.

Bekçinin dört kimlik okuma noktası vardı ve **ikisi `B`'yi zaten tanıyordu**
(merge dalı adı, ve *"depoda anılan kimlik"* kümesi). Yani genişletme iki yerde
yapıldı, dördünde sayıldı — *bir iddiayı düzeltmeden önce kaç yerin söylediğini
say.*

## Ticket listesi

| # | Ticket | Özü | Durum |
| --- | --- | --- | --- |
| B01 | [Gerçek hız kontrolü ve üretecin kendi tavanı](gercek-hiz-kontrolu/index.md) | Token kovası · beş profil · gerçek TCP/UDP · batch/çok bağlantı · raw/tagged · tel-baytı manifesti · **`GENERATOR-LIMITED`** | ✅ |
| B02 | [Üç katmanlı varış defteri](../kapasite-olcumu/b02-varis-defteri/index.md) | OS sayaçları · collector metrikleri · manifest ↔ ham arşiv ↔ `events`; `uncertain` ayrı sınıf; **`LEDGER-LIMITED`** | ✅ |

**B03, B04 ve B05'in ticket dosyası yok** ve bu bir eksiklik değil: plan
§6'nın üç açık sorusu (hedef donanım · `auto`'nun sürdürülebilirlik ölçütü · yük
üretecinin nereden koşacağı) cevaplanmadan yazılamazlar. Özleri ve bitti
ölçütleri planın **§5'inde** duruyor; buraya satır olarak kopyalamak ikinci bir
temsil doğururdu ve ikisi sessizce ayrışırdı.

Sıra ve bağımlılıklar da planda (§5, mermaid). B01 ve B02 kapandı; ikisi B03'ü
besliyor, B04 ve B05 ikisine de bağlı.

## Sözleşme — B01'in üreteceği şekil

**Yön kararı (koordinatör, 2026-09-15): şekli tüketici belirliyor.** Bir
üreticinin şekli var olan tüketicisine göre kurulur, tersi değil — B02'nin sonda
sözleşmesi bugün ağaçta ve **testli**, B01'in manifesti henüz yok. B01 yeni bir
şekil seçerse iki temsil doğar ve biri uydurmaya çalışır (§9).

`ArrivalLedger` bir yük koşumundan **beş okuma + bir sayı** bekliyor.
İmza `sim/Bizigo.Capacity/ArrivalLedger.cs`:

| Alan | Tip | B01 neyi vermeli | Kim üretiyor |
| --- | --- | --- | --- |
| `RunId` | `string` | Koşum kimliği. **Deneme başına ayrı** olmalı: bir kademenin gecikmiş olayları diğerinin sayısını kirletiyor (plan §4) | **B01** |
| `Expected` | `long` | Üretecin **bastığını iddia ettiği** satır sayısı. Defter bunu üretmiyor ve bir *iddia* olduğu yazılı — üreteç istediği hıza ulaşamadıysa suçlu hedef değil (`GENERATOR-LIMITED`, B01'in hükmü) | **B01** |
| `WireDrops` | `LedgerReading` | — | `WireDropReader.Read(udpPort, …)` |
| `CollectorAccepted` | `LedgerReading` | — | `CollectorMetricsReader.Delta(önce, sonra)` |
| `CollectorRefused` | `LedgerReading` | — | aynı, `RefusedMetric` ile |
| `ProductArchived` | `LedgerReading` | Manifestin ham arşivle **EŞLEŞEN** kayıt sayısı | B01'in manifesti + `RawReader` |
| `ProductSearchable` | `LedgerReading` | `events` sayımı (koşum penceresi + `owner_group`) | `EventReader.CountAsync` |

**Manifestten defterin istediği tek şey `ProductArchived`'i üretebilmek**, ve o
alanın anlamı **bir sayım değil bir eşleşme**:

| Gereksinim | Neden |
| --- | --- |
| Satır başına **sha256** | Eşleşme bundan kuruluyor. Düz bir sayım *"biri kayıp, biri iki kez yazıldı"* hâlini **görmez** ve defter o hâlde yanlışlıkla `CONSISTENT` der |
| Koşum kimliği ile bağ | Manifest hangi koşuma ait — `RunId` ile aynı değer |
| Satır sayısı | `Expected`'in kaynağı |

**Defterin manifest dosya biçimi hakkında hiçbir iddiası yok** (NDJSON, CSV,
başka bir şey — B01 seçer). Defterin gördüğü tek şey `long` bir sayı ve
okunamadıysa bir gerekçe: `LedgerReading.Limited(...)`. Yani biçim değişikliği
defteri **kırmıyor**; kıran tek şey sha256'nın olmaması olurdu.

**Ölçemediğin hâli sıfıra çevirme.** Sonda `LedgerReading.Limited(katman,
kaynak, gerekçe)` döndürüyor ve gerekçe **zorunlu**; sıfır dönmek *"kayıp yok"*
demek olurdu ve defterin bütün mantığı yalan söylerdi. Fabrika boş gerekçeyi
reddediyor.

## Bu belgenin bilmediği şey

**B02'nin işi `b02-*` adlı bir dalda merge edilmedi**, `t60-entropi-terfisi`
içinde geldi. Yani `EpicStatusTests`'in *"merge edilmiş her kimlik bir tabloda
anılıyor"* kapısı B02'yi **hiç görmüyor** — dal adı kimlik taşımıyor. Bu bir
kusur değil ama bir **kapsam notu**: o kapının B serisi hakkında konuşmaya
başlaması ilk `b0N-*` dalının merge edilmesine bağlı.
