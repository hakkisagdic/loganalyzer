---
title: "S02 — Syslog basıcı"
kind: ticket
status: 2
---

# S02 — Syslog basıcı

**Bağımlılık:** S01 · **Sonraki:** S04

## Amaç

Profilden örnek okuyup TCP/UDP'den syslog basmak. Hız ve kodlama profilden
geliyor.

## Kapsam

### İçinde

- Basıcı: profilin işaret ettiği örnek dosyalardan satır okuyup ağa basıyor.
- Hız, kodlama, taşıma (TCP/UDP) profilden.

### Zaman damgası — **karar verildi: simülatör "şimdi"yi damgalar**

Örnek dosyalar 2015–2022 tarihleri taşıyor. Simülatör onları **olduğu gibi
basmaz**; her satırın zaman damgasını basım anına kaydırır.

Gerekçe: **gerçek bir cihaz 2016 tarihi basmaz.** Simülatörün işi cihazı taklit
etmek, dosyayı taklit etmek değil.

Bunun iki sonucu var ve ikisi de yazılmalı:

1. **Örnek dosyaların tarihleri simülatörde hiç kullanılmıyor.** Parser'ın
   zaman ayrıştırma yolu bu yoldan sınanmıyor — o `ParserTests`'in işi ve orada
   sınanıyor.
2. Kaydırma **her satır için ayrı** yapılmalı, toplu değil: bir sıçrama
   dizisinin göreli aralıkları senaryonun anlamını taşıyor (§7'deki geçişler).

**Neden bu bir karar ve neden şart:** `events` tablosunda
`TTL toDateTime(ts) + INTERVAL 90 DAY` var. Kaydırma yapılmazsa ClickHouse
süresi dolmuş satırı parçayı oluştururken **atıyor ve istemciye "yazdım"
diyor** — S02'nin ilk koşumunda ölçülen kayıp tam olarak buydu (bkz. FS §10.5).

Altın örnek yükleyicisi aynı sorunu `GoldenSamplePlan.Anchor` ile çözüyor
(*"yayılımın sağ ucu — pratikte şimdi"*). Simülatör aynı mantığı kullanmalı;
**ikinci bir kopya yazma** (§9), çapa mantığını ortak yere taşı.

### Dışında

- Senaryo geçişleri — S04.

## Kabul kriterleri

- Basılan satır ClickHouse'a **ham arşivden geçerek** ulaşıyor; `raw_manifest`
  doğrulanmış.
- Basılan satır sayısı ile `events`'e inen satır sayısı **eşit** — ve eşit
  olmadığı gün fark **raporlanıyor**, sessizce yutulmuyor.
- Bir bekçi zaman kaydırmasının yapıldığını sınıyor: basılan satırın `ts`'si
  basım anının yakınında.

## Durum

**Kodu indi, S02a kapandı.** İlk koşumda TTL kaybı bulundu ve kök nedeni
ölçüldü. Kalan: yukarıdaki zaman kaydırma kararının uygulanması ve bekçisi.
