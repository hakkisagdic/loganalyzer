---
title: "T22 — Bildirim kanalları"
kind: ticket
status: 2
---

# T22 — Bildirim kanalları

**Bağımlılık:** T21 · **Sonraki:** T23

## Amaç

Alarmın kullanıcıya **ulaşması**. Tetiklenip kimseye gitmeyen alarm, alarm değil.

## Kapsam

### İçinde

- Kanallar: **Slack**, **Teams**, **e-posta**, **genel webhook**.
- Kanal yapılandırması kontrol düzleminde; gizli bilgiler (webhook URL'i, SMTP
parolası) **şifreli** saklanıyor.
- Mesaj biçimi: kural adı, tetikleyen değer, zaman aralığı, etkilenen kaynak, ve
ürüne **doğrudan bağlantı** (ilgili aramayı açan URL).
- Yeniden deneme ve geri adım: kanal geçici olarak ulaşılamazsa alarm kaybolmuyor.
- Gürültü kontrolü: aynı kural için tekrar tetiklenme aralığı ve gruplama.

### Dışında

- Alarm kuralı tanımı — T21.
- Çağrı nöbeti/eskalasyon — F2'de değil.

## Kabul kriterleri

- Dört kanalın her biri gerçek bir hedefe (test webhook'u, yerel SMTP) ulaşıyor.
- Kanal 500 döndüğünde alarm yeniden deneniyor ve deneme sayısı sınırlı.
- **Gizli bilgiler hiçbir log, hata mesajı veya API yanıtında görünmüyor** —
testle sabitlenmiş.
- Mesajdaki bağlantı, alarmı üreten aramayı doğru zaman aralığıyla açıyor.
- Aynı kural arka arkaya tetiklendiğinde kanal boğulmuyor.

## Notlar

F4'te agent senaryoları da bu kanalları kullanacak; arayüz senaryo motorundan
bağımsız tasarlanmalı.

Bağlantının doğru zaman aralığını açması küçük görünüyor ama alarmın işe
yararlığını belirleyen şey bu: "bir şey oldu" ile "şuna bak" arasındaki fark.
