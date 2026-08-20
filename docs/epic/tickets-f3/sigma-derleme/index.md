---
title: "T32 — Derleme hattı ve SQL versiyonlama"
kind: ticket
status: 0
---

# T32 — Derleme hattı ve SQL versiyonlama

**Bağımlılık:** T31 · **Sonraki:** T33 · **Yöneten karar:** mimari kararlar §3.1

## Amaç

Sigma kurallarını **derleme zamanında** SQL'e çevirip repoda versiyonlamak.
Sıcak yolda Python yok, belirsizlik yok.

## Kapsam

### İçinde

- SigmaHQ kurallarını çeken, T31'in pipeline'ıyla derleyen ve çıktıyı repoya
yazan hat. Referans: [`clicksiem/sigma_rules`](https://github.com/clicksiem/sigma_rules)
— günlük cron, üretilen kurallar commit'li.
- Üretilen SQL repoda versiyonlu; kural kimliği, kaynak sürümü ve derleme
tarihi ile birlikte.
- **CI kapısı:** derleme çıktısı depodakiyle aynı değilse adım düşüyor. Aksi
halde kural değişir, kimse fark etmez.
- Derlenemeyen kural raporlanıyor ve sayısı **görünür**; sessizce atlanmıyor.
- Sidecar imajında Python 3.13+ (backend'in sert kısıtı; imaj zaten
`python:3.13-slim`).

### Dışında

- Kural yönetimi ve çalıştırma — T33.

## Kabul kriterleri

- Hat tek komutla koşuyor ve çıktısı tekrarlanabilir: aynı girdi, aynı SQL.
- CI kapısı sürüklenmeyi yakalıyor; kapının kırmızı yanabildiği ölçüldü.
- Derlenemeyen kural sayısı sıfırdan büyükse CI **uyarıyor** — kabul edilebilir
ama görünmez olmamalı.
- Üretilen SQL'in en az biri canlı ClickHouse'ta koşup doğru sonucu veriyor.

## Notlar

Build-time kararının **üçüncü gerekçesi** ölçümle geldi: backend üç aylık, iki
yıldızlı, tek geliştiricili. Üretilen SQL repoda durduğu için proje terk edilse
bile mevcut kurallar çalışmaya devam ediyor — kaybedilen yalnızca *yeni* kural
derleme yeteneği, ve LGPL-3.0 fork'a izin veriyor.

Bir tuzak ölçüldü: `clicksiem/sigma_rules` deposundaki dosya sayıları %100 uyum
gibi görünüyor ama **yanıltıcı** — dönüşüm başarısız olduğunda eski çıktı dosyası
depoda kalıyor. Bizim hattımız bunu tekrarlamamalı: başarısız derleme eski
dosyayı **silmeli** ya da açıkça işaretlemeli.
