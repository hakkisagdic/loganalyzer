---
title: "T24 — Change: elle giriş ve webhook"
kind: ticket
status: 2
---

# T24 — Change: elle giriş ve webhook

**Bağımlılık:** T14 · **Sonraki:** T25
**Yöneten karar:** K34

## Amaç

`change_events` tablosunun gerçek veriyle **dolmaya başlaması**. Tablo F1'de
kasten kuruldu ve boş duruyor; RCA F3'te kanıt arayacak ve geçmiş birikmemişse
özellik boş doğar.

## Kapsam

### İçinde

- UI'dan elle değişiklik kaydı: ne, ne zaman, hangi kaynak/grup, kim yaptı,
serbest açıklama.
- Değişiklik listesi ve arama (`GET /v1/changes` zaten var).
- **İmzalı webhook alıcısı**: dış sistemler değişiklik bildirebiliyor.
- Sağlayıcı başına yük eşlemesi: GitHub Actions, Jenkins, GitLab CI —
her birinin gövdesi farklı, hepsi aynı `change_events` şekline düşüyor.
- Bilinmeyen sağlayıcı için genel eşleme (JSON yol ifadeleriyle).

### Dışında

- Connector yapılandırma ekranı — T25.
- Cihaz config fark tespiti — T26.

## Kabul kriterleri

- Elle girilen kayıt kapsam kapısından geçiyor: kullanıcı yalnızca kendi
kapsamındaki bir gruba yazabiliyor. F1'de `IScopedQuery.WriteChangeAsync` bunu
zaten zorluyor — ekran onu atlamamalı.
- Webhook **imza doğrulaması** olmadan kayıt kabul etmiyor.
- Üç sağlayıcının gerçek örnek gövdeleri doğru eşleniyor — testte sabit örnekler.
- Aynı webhook iki kez gelirse ikinci kayıt oluşmuyor (idempotans anahtarı).

## Notlar

Bu ticket'ın değeri bugün değil F3'te görünüyor: RCA "ne değişti" verisi olmadan
"ne oldu"nun ötesine geçemiyor, ve o veri **geçmişe dönük üretilemiyor**. Ham
arşivle aynı mantık — geç eklenen şey geçmişi kurtarmıyor.
