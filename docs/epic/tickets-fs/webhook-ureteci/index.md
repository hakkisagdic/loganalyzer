---
title: "S07 — İmzalı webhook üreteci"
kind: ticket
status: 0
---

# S07 — İmzalı webhook üreteci

**Bağımlılık:** S01 · **Sonraki:** — (FS-b)

## Amaç

GitHub / GitLab / Jenkins / generic imzalı webhook üreteci — yani T24'ün
alıcısını **gönderen tarafından** sınamak.

## Neden bu ticket, alıcı zaten test edilmişken

Bu soru soruldu ve cevabı ticket'ın var olma sebebi:

T24'ün testleri **kendi kurdukları gövdeyi kendi imzalıyor**. Yani imza
doğrulamasının doğru olduğunu değil, **kendi kendiyle tutarlı** olduğunu
kanıtlıyorlar. GitHub'ın gerçekte hangi başlığı hangi biçimde gönderdiği o
testlerden okunamaz.

Üreteç bunu ayırıyor: gövdeyi ve imzayı **vendor'ın belgelediği biçimde**
kuruyor, alıcı onu tanımak zorunda kalıyor. İkisi ayrıştığı gün fark
görünür oluyor.

Ayrıca F4'ün dört tetikleyicisinden biri **değişiklik olayı** ve o bu yoldan
besleniyor — S07 olmadan F4'ün o tetikleyicisi test edilemez.

## Kapsam

### İçinde

- Dört biçim: GitHub, GitLab, Jenkins, generic.
- İmza, **vendor'ın belgelediği algoritma ve başlık adıyla**.
- Aynı teslimatı iki kez gönderebilme (idempotans sınaması için).

### Dışında

- Alıcı tarafında değişiklik — T24 kapalı, bu ticket onu **sınıyor**, değiştirmiyor.
  Değiştirmesi gerekiyorsa bu bir bulgudur ve ayrı bir kalem açar.

## Kabul kriterleri

- İmza doğrulaması **dört biçimde de** geçiyor.
- Aynı teslimat iki kez gönderilince **tek** kayıt oluşuyor.
- Bozuk imza reddediliyor — ve reddin sebebi *"imza tutmadı"*, genel bir
  ayrıştırma hatası değil.
- Üretecin kurduğu gövde, vendor belgesinden alınmış **gerçek bir örneğe**
  karşı sınanıyor; kendi ürettiğine karşı değil.

  Bu madde ticket'ın tamamının anlamı: kendi ürettiğini doğrulayan bir üreteç,
  T24'ün testlerinin yaptığı şeyi ikinci kez yapardı.
