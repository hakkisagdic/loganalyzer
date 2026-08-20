---
title: "T25 — Change: connector yapılandırma"
kind: ticket
status: 2
---

# T25 — Change: connector yapılandırma

**Bağımlılık:** T24 · **Sonraki:** T26
**Yöneten karar:** K34 — "ekrandan yapılandırılabilmeli"

## Amaç

Değişiklik kaynaklarının koddan değil **ekrandan** tanımlanması. T26'nın cihaz
poller'ı bu altyapıya oturacak.

## Kapsam

### İçinde

- Connector modeli: tip (webhook / cihaz config / elle), hedef, zamanlama,
kimlik bilgisi referansı, etkin-pasif, sahip grubu.
- CRUD API ve yönetim ekranı.
- **Kimlik bilgisi saklama:** kontrol düzleminde şifreli. Anahtar yönetimi
yapılandırmadan geliyor; parola/token hiçbir yanıtta, log'da veya hata mesajında
görünmüyor.
- Bağlantı testi: connector kaydedilmeden önce "erişebiliyor muyum" denemesi.
- Connector başına çalışma geçmişi ve son hata.

### Dışında

- Cihaz config çekme ve fark alma mantığı — T26.

## Kabul kriterleri

- Kimlik bilgisi API yanıtında **maskeli** dönüyor ve düz metin hiçbir yerde
görünmüyor — testle sabitlenmiş.
- Connector yalnızca sahibinin kapsamındaki kaynaklara bağlanabiliyor.
- Bağlantı testi başarısız olduğunda hata mesajı kimlik bilgisi sızdırmıyor.
- Pasif connector çalışmıyor; etkinleştirildiğinde zamanlayıcı devralıyor.

## Notlar

Şifreli saklama bu üründe **ilk** kez burada gerekiyor. Anahtarın nerede
duracağı (ortam değişkeni, dosya, dış KMS) bu ticket'ta karara bağlanmalı —
sonradan değiştirmek saklanmış her kaydı yeniden şifrelemek demek.
