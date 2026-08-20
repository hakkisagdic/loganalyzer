---
title: "T38 — Altın küme ve inceleme akışı"
kind: ticket
status: 0
---

# T38 — Altın küme ve inceleme akışı

**Bağımlılık:** T33, T37 · **Sonraki:** —

## Amaç

Kalitenin **ölçülebilir** olması. Altın küme olmadan F4'ün LLM'i iyi mi kötü mü
bilinemez.

## Neden ayrı bir ticket

RCA artifact'ının 2. riski: *"kimse 'doğru muydu?' düğmesine basmazsa altın küme*
*boş kalır ve kalite ölçülemez."* Bu bir arayüz ayrıntısı değil, F4'ün tüm kalite
ölçümünün dayanağı.

## Kapsam

### İçinde

- Altın küme deposu: olay, kanıt paketi, insan kararı (doğru / yanlış / eksik),
serbest not.
- **Zorunlu inceleme:** alarm tetikli RCA'da, alarmı **kapatma akışının**
**zorunlu parçası**. Kullanıcı zaten oradadır; ayrı bir "geri bildirim ver" adımı
hiç kullanılmaz.
- Kullanıcı tetikli RCA'da inceleme isteğe bağlı — zorlamak orada kullanıcıyı
kaçırır.
- Kalite göstergesi: altın kümede kaç kayıt var, doğruluk oranı ne.

### Dışında

- LLM çıktısının değerlendirilmesi — F4. Ama depo şeması onu **taşıyabilmeli**:
F4 aynı olay için model çıktısını ekleyecek ve insan kararıyla karşılaştıracak.

## Kabul kriterleri

- Alarm kapatan kullanıcı inceleme adımını **atlayamıyor**.
- Kayıtlar F4'ün ihtiyacı olan şekli taşıyor: aynı kanıt paketi + insan kararı,
sonradan model çıktısı eklenebilir.
- Kalite göstergesi ekranda görünüyor; küme boşsa bu bir uyarı olarak duruyor.
- Kapsam altında: bir ekip başka ekibin incelemesini görmüyor.

## Notlar

RCA artifact'ının 5. riski de buraya bakıyor: *"çelişen kanıt tiyatrosu"* —
model, çelişen kanıt alanını doldurmak için önemsiz bir şey uydurabilir. Altın
küme şeması bunu ayrıca ölçebilmeli, yani "çelişen kanıt doğru muydu?" ayrı bir
alan olmalı. F4'te kullanılacak ama alanın bugün açılması gerekiyor —
sonradan eklenirse geçmiş kayıtlar onu taşımaz.
