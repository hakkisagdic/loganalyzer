---
kind: spec
title: "F1'in kapsam kabul kriteri yanlış yazılmış — F1 kapandığından beri"
---

# F1'in kapsam kabul kriteri yanlış yazılmış

M18 `IScopedQuery`'nin tüketici listesini türetirken bir kabul kriterine çarptı ve
kararı bana bıraktı. Ölçtüm, ve durum onun çerçevesinden **ağır**: kriter
daraltılmadı, **yanlış yazılmıştı** ve F1 kapandığından beri öyle duruyor.

## Kriterin hâli

`docs/epic/f1-teknik-plan/index.md:25`:

> | Kapsam | Kapsam dışı sorgu **hiçbir** yoldan (REST, replay okuma, CLI) veri döndürmez |

Üç yol sayıyor. **Yalnızca biri** kapsam kapısından geçiyor.

| Yol | Ölçülen | Gerekçe |
| --- | --- | --- |
| **REST** | ✅ `IScopedQuery` üzerinden | Kriterin gerçek olduğu tek yol |
| **replay okuma** | ❌ `ReplayEngine.cs:180` → **`AccessScope.System("replay")`** | Kapsamı **tasarımı gereği** atlıyor |
| **CLI** | ❌ `IScopedQuery`'yi hiç anmıyor; `ClickHouseContext`/`EventWriter` ile doğrudan | Çağıran zaten veritabanı erişimine sahip |

## Bu bir kusur değil — ama kriterin yanlış olması kusur

İkisinin de kapsam dışı olması **doğru** ve ikisi ayrı sebeple:

**Replay bir sistem işlemi.** İşi ham arşivi yeniden okuyup boru hattına vermek.
Kapsamlanmış bir replay, arşivin yalnızca **bir kısmını** geri yükleyebilirdi — yani
kurtarma aracının kurtarabildiği şey çağıranın grubuna bağlı olurdu. `System("replay")`
bir gevşeme değil, aracın tanımı.

**CLI'da kimlik yok ve olamaz.** Komut için `BIZIGO_CONTROLPLANE` gerekiyor; elinde
`psql` olan birini bir bayrakla durdurmaya çalışmak, duvarı olmayan yere kapı takmak.

Kriterin hatası **kapsamı** karıştırmakta: *"bütün okuma yolları"* ile *"kullanıcıya
açık sorgu yolları"* aynı şey değil, ve kriter birincisini yazıp ikincisini kastediyor.

## Ve kimse ölçmedi

Asıl bulgu bu. Kriter F1 kapanış tablosunda **karşılandı** sayılıyor ve bugüne kadar
hiçbir koşum onu üç yol için sınamadı. `ArchitectureTests` doğrudan sürücü erişimini
yalnızca **API** için kapatıyor; replay ve CLI o listede bilinçli olarak yok.

Yani kriter *"hiçbir yoldan"* diyor, mekanizma **bir** yoldan tutuyor, ve arada bir
bekçi yok. Bu deponun beş kez adını koyduğu şeklin faz kabul kriteri katmanındaki
hâli — ve M18'in bekçisi (`ScopedQueryConsumerTests`) bugün ilk kez o kümeyi
ölçülebilir kılıyor.

## Düzeltilmiş kriter

> | Kapsam | Kapsam dışı sorgu **ürün yüzeylerinin hiçbirinden** (REST, MCP, arayüz) veri döndürmez. Replay okuma ve CLI **kapsam dışı** ve gerekçeleri yazılı |

İki değişiklik var ve ikisi ayrı:

1. **Yollar düzeltildi** — replay ve CLI çıktı, gerekçeleriyle.
2. **MCP ve arayüz eklendi** — F1 yazıldığında ikisi de yoktu. Kriter *"REST"*
   derken kastettiği şey **kullanıcıya açık her yüzey**di, ve o küme o günden beri
   büyüdü. Bugün kapsam kapısından geçen dört derleme var (`Api`, `Alerting`,
   `Evidence`, `Mcp.Product`) ve M18 bunu türetiyor, elle listelemiyor.

## Bunun açtığı soru — cevaplanmadı

**F1 kapanış tablosundaki diğer kriterler de aynı sınıfta olabilir.** M18'in işaret
satırı bunu söylüyor ve ben de doğruluyorum: bu kriter *"karşılandı"* diye duruyordu
ve ölçülmemişti. Tablonun tamamı aynı gözle geçirilmedi.

Bu bir ticket değil bir **tetikleyici**: bir kabul kriterine dokunan bir iş çıktığında
o kriterin bugün ölçülüp ölçülmediği **ilk soru** olmalı. Tabloyu baştan taramak
ayrı bir iş ve bugün sahibi yok.
