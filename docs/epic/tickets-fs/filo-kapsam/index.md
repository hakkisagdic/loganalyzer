---
title: "S05 — Filo ve kapsam yayılımı"
kind: ticket
status: 2
---

# S05 — Filo ve kapsam yayılımı

**Bağımlılık:** S04 · **Sonraki:** — (FS-a'nın kapanış ticket'ı)

## Amaç

Beş cihazlık bir filo, **iki `owner_group`**, envanter ve bağlama otomatik.

Bu ticket FS'in taşıyıcı iddiasını kanıtlıyor: ürün, gerçek cihaz olmadan
**uçtan uca** koşabiliyor.

## Kapsam

### İçinde

- Beş simüle cihaz, iki gruba dağılmış.
- Envanter kayıtları ve `owner_group` ataması **otomatik** — elle tohumlama yok.
- Kapsam yayılımı: bir grubun kullanıcısı diğerinin cihazını görmüyor, ve bu
  **ekranda** da böyle.

### Dışında

- Ölçek. Beş cihaz, beş yüz cihazlık bir kurulumun davranışını göstermez;
  bağlama oranı, dispatcher kademeleri ve WAL basıncı hakkında bu fazdan çıkan
  hiçbir sayı **bağlayıcı değil** (FS §11).

## Kabul kriterleri

- **Uçtan uca harness'taki elle tohumlama silinebiliyor** — FS §6'nın kapanış
  ölçütü ve bu fazın var olma sebebi.
- Kapsam yayılımı iki kimlikle sınanıyor: aramada görünmüyor **ve** adresini
  bilen açamıyor.

  İkinci yarı ayrı bir iddia. F2'de bu ayrımın atlanması bir açık bırakmıştı:
  kanıt paketinde toplama kapsam altındaydı, **okuma değildi**.
- Filo yapılandırması **tek dosyada**; beş cihazın tanımı beş yere dağılmıyor.
