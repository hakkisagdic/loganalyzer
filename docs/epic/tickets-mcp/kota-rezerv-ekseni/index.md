---
kind: ticket
title: "M15 — Kota rezervi Agent kaynağını kapsamıyor"
status: 0
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

## Boşluk gözlemde DEĞİL, korumada

Ayrımı yazmak gerekiyor çünkü ikisi karıştırılırsa yanlış iş yapılır:

| | Durum |
| --- | --- |
| **Gözlem** | ✅ Hazır. `rca_runs` her koşumda `Source` **ve** `CountsAgainstQuota` yazıyor, yani kaynak başına tüketim okunabiliyor |
| **Koruma** | ❌ Rezerv ekseni `Agent`'ı tanımıyor |

Yani operatör rezervasyonun gerekip gerekmediğini **veriden** görebiliyor; göremediği
şey onu açtığında bir şeyin değişmesi.

## Yapılacak

1. **Rezerv eksenini ölç, sonra genişlet.** Bugünkü eksen *"`Schedule` mi değil mi"*.
   Doğru eksen ne — *"insan mı makine mi"*, *"olay tetikli mi istek tetikli mi"*,
   yoksa kaynak başına ayrı yüzde mi? Üçü farklı sayı ve farklı gevşeme yolu üretir.
2. **Sayı önerme, ölç.** T46 bu sabiti bilerek `0` bıraktı ve gerekçesi hâlâ
   geçerli: *"ölçülene kadar tek slot, yavaş ama yanlış değil."* Rezerv yüzdesi
   gerçek kullanımdan gelmeli ve gözlem tarafı zaten hazır.
3. **Kırmızıyı ölç.** Bugünkü ağaç bu boşluğu **taşıyor**, yani bekçi bedava: rezerv
   açıkken bir `Agent` koşumunun limiti tükettiği ve bir `Manual` koşumun reddedildiği
   hâl yazılabilir ve bugün **kırmızı yanar**.

## Bu ticket'ın YAPMADIĞI şey

**Kotayı kimlik başına saymaya çevirmek.** T46 tek havuz kararını gerekçeleriyle
verdi (`§6.1`) ve bu ticket onu açmıyor: sorun havuzun **birliği** değil, havuz
içindeki **payın** kaynağa göre ayrılamaması.
