---
title: "T23 — Alarm yönetim ekranı"
kind: ticket
status: 2
---

# T23 — Alarm yönetim ekranı

**Bağımlılık:** T14, T22 · **Sonraki:** T27

## Amaç

Kural yazmak, ne olacağını önceden görmek, ne olduğuna bakmak.

## Kapsam

### İçinde

- Kural CRUD: kaydedilmiş aramadan başlayarak tip ve parametre seçimi.
- **Önizleme:** kural geçmiş veriye karşı koşturulup "son 24 saatte kaç kez
tetiklenirdi" gösteriliyor. Bu olmadan eşik seçmek kör atış.
- Tetiklenme geçmişi: ne zaman, hangi değerle, hangi kanala gitti, ulaştı mı.
- Susturma/bakım penceresi yönetimi.
- Kanal ataması ve test gönderimi.

### Dışında

- Motor ve kanallar — T21, T22.

## Kabul kriterleri

- Önizleme gerçek geçmiş veriyle çalışıyor ve eşik değiştikçe sayı güncelleniyor.
- Kullanıcı yalnızca **kendi kapsamındaki** kaynaklar için kural yazabiliyor.
- Tetiklenme geçmişi kanalın başarısız olduğu denemeleri de gösteriyor —
"gönderildi" ile "ulaştı" ayrı.
- Test gönderimi gerçek kanala gidiyor ve sonucu ekranda görünüyor.

## Notlar

Önizleme, K16'daki 50 kişilik kurumda gürültüyü baştan kesen tek mekanizma:
eşiğini görmeden yazılan kural ya hiç tetiklenmiyor ya herkesi boğuyor.
