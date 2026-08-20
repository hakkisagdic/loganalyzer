---
title: "T21 — Alarm motoru"
kind: ticket
status: 2
---

# T21 — Alarm motoru

**Bağımlılık:** T17 · **Sonraki:** T22
**Yöneten karar:** K32

## Amaç

Kimsenin ekrana bakmadığı saatlerde ürünün konuşması. Üç kural tipi, tek
değerlendirici.

## Kapsam

### İçinde

| Tip | Soru | Örnek |
| --- | --- | --- |
| **Eşik** | Sayı bir sınırı aştı mı | 5 dk'da `action=deny` > 100 |
| **Oran** | Değişim hızlandı mı | Hata oranı önceki saate göre 3× |
| **Sessizlik** | Beklenen veri **gelmedi mi** | `fw-core-01` 15 dk'dır susuyor |

- Kural modeli: kaydedilmiş arama + tip + parametreler + değerlendirme aralığı.
- Zamanlayıcı ve değerlendirici; tetiklenme geçmişi.
- **Kapsam altında koşum:** kural, sahibinin kapsamıyla değerlendiriliyor. Bir
ekibin kuralı başka ekibin verisini sayamaz.
- Susturma (bakım penceresi) ve tekrar tetiklenme aralığı.

### Dışında

- Bildirim kanalları — T22.
- Yönetim ekranı — T23.
- Sigma kuralları — F3, ama üretilen SQL bu motora takılacak şekilde tasarlanmalı.

## Kabul kriterleri

- **Sessizlik alarmı gerçek veriyle çalışıyor:** envanterdeki bir kaynak
susturulduğunda eşik sonrası tetikleniyor. Bu en zor tip, çünkü diğer ikisi
verinin **varlığı** üzerinde, bu **yokluğu** üzerinde çalışıyor.
- Bir ekibin kuralı başka ekibin olaylarını saymıyor — testle sabitlenmiş.
- Değerlendirme maliyeti sınırlı: kural sayısı arttığında ClickHouse'a atılan
sorgu sayısı doğrusal ötesi büyümüyor.
- Susturma penceresinde tetiklenme yok, pencere bitince var.

## Notlar

Sessizlik ağ tarafında en değerli alarm: susan cihaz, gürültü yapandan
tehlikelidir. `/v1/health/pipeline` ve T17'nin "son görülme" hesabı bunun
yarısını zaten yapıyor — üçüncü bir kopya yazılmamalı.

K16'nın uyarısı burada geçerli: 50 kişilik kurumda herkes cron'lu kural
yazabiliyorsa tek kötü kural ClickHouse'u doyurur. Değerlendiriciye baştan
eşzamanlılık limiti ve timeout girmeli.
