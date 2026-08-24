---
title: "S06 — N3: CLI öykünmesi"
kind: ticket
status: 0
---

# S06 — N3: CLI öykünmesi

**Bağımlılık:** S03 · **Sonraki:** — (FS-b)

## Amaç

Etkileşimli kabuk: prompt, sayfalama, vendor hata mesajları.

S03'ün sunucusu komuta cevap veriyor; bu ticket onu **cihaz gibi** konuşturuyor.
Fark önemsiz görünüyor ama toplayıcının en kırılgan yolu burada: gerçek bir ASA
`--More--` basar ve toplayıcı onu görmezse çıktının yarısını alır — **hatasız**.

## Kapsam

### İçinde

- Prompt (`hostname#`, `hostname>`), enable moduna geçiş.
- **Sayfalama**: `--More--` ve `terminal length 0` ile kapatılması.
- Vendor'ın kendi hata mesajları — `% Invalid input detected`,
  `command parse error` gibi. Bizim ürettiğimiz genel bir hata değil.

### Dışında

- Vendor sürüm farkları. FortiGate 7.2 ile 7.4 aynı komuta farklı cevap
  veriyor olabilir; simülatör **bizim yazdığımız** biçimi üretir, yani
  toplayıcıyı sınar, **vendor'ı değil**. Bir müşteri cihazından alınan tek bir
  gerçek çıktı, on senaryodan çok şey söyler (FS §11).

## Kabul kriterleri

- Toplayıcı **sayfalama açıkken de** doğru çıktı alıyor — ya da **alamadığı
  yazılı**.

  İkinci şıkkın kabul kriterinde durması bilinçli: bugün toplayıcının o yolu
  taşıyıp taşımadığı bilinmiyor. "Çalışıyor" diye yazıp sonra çalışmadığını
  bulmak, bu depoda en pahalı hata sınıfı.
- Vendor hata mesajı `DeviceCommandResult`'ta **ayırt edilebilir** — genel bir
  başarısızlığa düşmüyor. "Komut yanlış" ile "cihaz cevap vermedi" farklı
  şeyler ve ikisi tek değere inerse teşhis kaybolur.

## Neden bu ticket ertelenebilir ama silinemez

FS-b'de. FS-a (S01–S05) ürünün gerçek cihaz olmadan koşmasını sağlıyor; bu
ticket **toplayıcının doğru koştuğunu** sağlıyor. İkisi ayrı sorular.

Silinemez çünkü bugün cevabı olmayan bir soruyu açık tutuyor: toplayıcı
sayfalamayı biliyor mu? Ticket kapanmadan cevap **yok**, ve yokluğu kayıtlı.
