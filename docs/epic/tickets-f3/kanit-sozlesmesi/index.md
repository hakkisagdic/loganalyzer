---
title: "T34 — Kanıt sağlayıcı sözleşmesi"
kind: ticket
status: 2
---

# T34 — Kanıt sağlayıcı sözleşmesi

**Bağımlılık:** T29 · **Sonraki:** T35 · **Yöneten kararlar:** K21, K22

> Sevk edildi — `t34-kanit-sozlesmesi` dalı, birleştirme koordinatörde.
> Boşluğun dört cinsi, "motor değişmiyor" kriterinin nasıl sınandığı ve
> yazarken çıkan iki sessiz kusur:
> [T34 kanıt sözleşmesi](../../t34-kanit-sozlesmesi/index.md).

## Amaç

Kanıtın nereden geldiğini soyutlamak. F3'te iki sağlayıcı var; sözleşme
**beşini de** tanıyor ve motor hiçbirine özel kod içermiyor.

## Kapsam

### İçinde

- Sözleşme: bir olay/zaman penceresi/kapsam verilince kanıt döndüren arayüz.
- Beş tür **tanımlı**: `log`, `change`, `metric`, `trace`, `topology`.
- İki tür **uygulanıyor**: log (ClickHouse) ve change (`change_events`).
- Kayıt bulunamayan tür için "sağlayıcı yok" ile "sağlayıcı var ama veri yok"
ayrımı — rapor ikisini farklı yazacak.
- **Kapsam dışı sayım:** sağlayıcı, kapsam dışında kaç eşleşme olduğunu döndürür
(içeriği değil).

### Dışında

- Metrik, trace, topoloji sağlayıcıları — **F5**. Sözleşme onları tanıyor ama
uygulaması yok.
- Korelasyonlar — T35.

## Kabul kriterleri

- Yeni bir sağlayıcı eklendiğinde **motor değişmiyor** — F5'in testi bu olacak,
ama tasarım bugün buna izin vermeli.
- Kapsam dışı sayım içerik sızdırmıyor: dönen şey yalnızca bir sayı, testle
sabitlenmiş.
- "Sağlayıcı yok" ile "veri yok" ayrımı rapora kadar taşınıyor.

## Notlar

K21 projedeki en büyük kapsam genişlemesi ve K1'i genişletiyor. Sözleşmenin
beş türü de tanıması, F5'in ne zaman gelirse gelsin motoru yeniden yazmamasını
sağlıyor — ama F3'te iki sağlayıcıyla yetinmek bilinçli.

RCA artifact'ının 4. riski burada geçerli: change beslemesi boşsa "değişiklik
yok" diyen bir sağlayıcı olur. F2'nin T24-T26'sı bunu doldurmak için var; bu
ticket besleme olmadığında **sessiz kalmamalı**, "bu türde hiç veri yok" demeli.
