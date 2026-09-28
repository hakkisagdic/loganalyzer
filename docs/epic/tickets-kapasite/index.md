---
title: "Kapasite ölçümü — implementasyon ticket'ları"
kind: story
status: 1
---

# Kapasite ölçümü ticket'ları

[Kapasite ölçümü planı](../kapasite-olcumu/index.md) beş ticket'a bölündü.
Bu story yalnızca onları **gruplar**; kararların tamamı planda ve ticket
dosyalarında.

Planın kendi tablosu (§5) dilimlemenin gerekçesini ve bitti ölçütlerini
taşıyor — burada tekrarlanmıyor, çünkü iki kopya ayrışır ve ayrışmayı hiçbir
şey yakalamaz.

## Ticket listesi

| # | Ticket | Özü | Bağımlılık |
| --- | --- | --- | --- |
| B01 | [Gerçek hız kontrolü ve üretecin kendi tavanı](gercek-hiz-kontrolu/index.md) | Token kovası; `fixed`/`ramp`/`burst`/`soak`/`max`; `GENERATOR-LIMITED` hükmü | — |
| B02 | Varış defteri | Üç katmanlı sayaç (OS · collector · ürün); `LEDGER-LIMITED` | — |
| B03 | `auto` keşfi | Hızlı büyütme + ikili arama, deneme başına ayrı `RUN-ID` | B01, B02 |
| B04 | Hüküm ve boşluk deseni | SLO `PASS`/`FAIL`, kayıp deseni sınıflandırması | B02, B03 |
| B05 | Güvenlik devre kesici | Yeni drop / sürekli CPU / sürekli `Recv-Q` görünce üreteci durdur | B01, B02 |

> ⚠️ **Bu tablonun satırları yol haritası bekçisine bugün GÖRÜNMÜYOR.**
> `EpicStatusTests.ReadRoadmap` kimliği `[TSM]\d+` deseniyle arıyor ve `B`
> ön eki desende yok. Ölçüldü ve koordinatöre bildirildi (B01 ticket'ı
> §"Ölçülen bir bekçi boşluğu"); kimlik uzayının şekli onun kararı, desen bu
> turda **genişletilmedi**.
>
> Sonucu bölünmüş: `Declared` okuyucusu `[TSMB]` kullandığı için *"merge
> edilmiş her kimlik bir tabloda anılıyor"* kapısı çalışıyor, ama *"merge
> edilmiş bir iş `status: 0` görünmüyor"* kapısı B kalemlerinde **hiç**
> çalışmıyor.
